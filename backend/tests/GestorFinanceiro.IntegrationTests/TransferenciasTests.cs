using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GestorFinanceiro.IntegrationTests.Infra;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.UnitTests;
using Npgsql;

namespace GestorFinanceiro.IntegrationTests;

/// <summary>Transferências entre contas e pagamento de fatura: duas pernas no mesmo grupo, editadas e apagadas juntas.</summary>
[Collection("postgres")]
public class TransferenciasTests(PostgresFixture pg)
{
    private const string Rota = "/api/v1/transferencias";
    private const string RotaDeLancamentos = "/api/v1/lancamentos";
    private const string SqlAna = "SELECT id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'";

    private sealed record Contexto(ApiDeTeste Api, BancoDeTeste Banco, HttpClient Cliente) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Api.DisposeAsync();
    }

    private static async Task<T> EscalarAsync<T>(BancoDeTeste banco, string sql)
    {
        await using var db = new NpgsqlConnection(banco.ConexaoSuperusuario);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, db);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task ExecutarAsync(BancoDeTeste banco, string sql)
    {
        await using var db = new NpgsqlConnection(banco.ConexaoSuperusuario);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, db);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    /// <summary>Relógio padrão: 05/11/2026 09:00 em Brasília.</summary>
    private async Task<Contexto> IniciarAsync()
    {
        var banco = await pg.NovoBancoAsync();
        var api = new ApiDeTeste(banco, relogio: new RelogioFixo());
        var pedido = new CadastroDoCasalDto(
            api.CodigoInstalacao, "Família Teste",
            new ConjugeNovoDto("Ana Teste", CpfSintetico.Gerar(1), "senha-da-ana-123"),
            new ConjugeNovoDto("Bruno Teste", CpfSintetico.Gerar(2), "senha-do-bruno-1"));
        Assert.Equal(HttpStatusCode.Created, (await api.CreateClient().PostAsJsonAsync("/api/v1/casal", pedido)).StatusCode);
        var ana = await EscalarAsync<Guid>(banco, SqlAna);
        return new Contexto(api, banco, await api.ClienteLogadoAsync(ana));
    }

    private static async Task<Guid> CriarContaAsync(HttpClient cliente, object corpo)
    {
        var resposta = await cliente.PostAsJsonAsync("/api/v1/contas", corpo);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return Guid.Parse(resposta.Headers.Location!.ToString().Split('/').Last());
    }

    private static Task<Guid> CriarCorrenteAsync(HttpClient cliente, string apelido = "Itaú conjunta", long saldoInicial = 0) =>
        CriarContaAsync(cliente, new { tipo = "CORRENTE", apelido, saldoInicialCentavos = saldoInicial, saldoInicialEm = "2026-01-01" });

    private static Task<Guid> CriarCartaoAsync(HttpClient cliente, string apelido = "Nubank") =>
        CriarContaAsync(cliente, new
        {
            tipo = "CARTAO", apelido, saldoInicialCentavos = 0L, saldoInicialEm = "2026-01-01",
            diaFechamento = 5, diaVencimento = 12, limiteCentavos = 800_000L,
        });

    private static object Nova(Guid origem, Guid destino, long valor = 50_000, string data = "2026-11-04",
        string descricao = "Reserva do mês", Guid? chave = null) => new
    {
        chaveIdempotente = chave ?? Guid.NewGuid(), contaOrigemId = origem, contaDestinoId = destino, valorCentavos = valor, data,
        descricao,
    };

    private static async Task<JsonElement> TransferirAsync(HttpClient cliente, object corpo)
    {
        var resposta = await cliente.PostAsJsonAsync(Rota, corpo);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return await JsonAsync(resposta);
    }

    private static async Task<JsonElement[]> ExtratoAsync(HttpClient cliente, string consulta)
    {
        var resposta = await cliente.GetAsync($"{RotaDeLancamentos}?{consulta}");
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        return (await JsonAsync(resposta)).GetProperty("lancamentos").EnumerateArray().ToArray();
    }

    private static async Task<JsonElement> SaldoAsync(HttpClient cliente, Guid conta, string em = "2026-11-05")
    {
        var resposta = await cliente.GetAsync($"/api/v1/saldos?em={em}");
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        return (await JsonAsync(resposta)).EnumerateArray().Single(s => s.GetProperty("contaId").GetGuid() == conta);
    }

    private static Task<long> ContarAsync(BancoDeTeste banco, string condicao = "true") =>
        EscalarAsync<long>(banco, $"SELECT count(*) FROM lancamentos.transacoes WHERE {condicao}");

    [Fact]
    public async Task Transferencia_cria_par_e_nao_conta_como_despesa_nem_receita()
    {
        await using var c = await IniciarAsync();
        var corrente = await CriarCorrenteAsync(c.Cliente, saldoInicial: 100_000);
        var poupanca = await CriarCorrenteAsync(c.Cliente, "Poupança");

        var resposta = await c.Cliente.PostAsJsonAsync(Rota, Nova(corrente, poupanca, 30_000));

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        var criada = await JsonAsync(resposta);
        var grupo = criada.GetProperty("grupoId").GetGuid();
        var saida = criada.GetProperty("saida");
        var entrada = criada.GetProperty("entrada");
        Assert.Equal(corrente, saida.GetProperty("conta").GetProperty("id").GetGuid());
        Assert.Equal(poupanca, entrada.GetProperty("conta").GetProperty("id").GetGuid());
        Assert.Equal(-30_000, saida.GetProperty("valorCentavos").GetInt64());
        Assert.Equal(30_000, entrada.GetProperty("valorCentavos").GetInt64());
        foreach (var perna in new[] { saida, entrada })
        {
            Assert.Equal("TRANSFERENCIA", perna.GetProperty("natureza").GetString());
            Assert.Equal(JsonValueKind.Null, perna.GetProperty("categoria").ValueKind);
            Assert.Equal(grupo, perna.GetProperty("grupoId").GetGuid());
            Assert.Equal("Reserva do mês", perna.GetProperty("descricao").GetString());
            Assert.Equal("2026-11-04", perna.GetProperty("data").GetString());
        }

        var extrato = await ExtratoAsync(c.Cliente, "mes=2026-11");
        Assert.Equal(2, extrato.Length);
        Assert.All(extrato, l => Assert.Equal("TRANSFERENCIA", l.GetProperty("natureza").GetString()));
        Assert.Equal(70_000, (await SaldoAsync(c.Cliente, corrente)).GetProperty("saldoCentavos").GetInt64());
        Assert.Equal(30_000, (await SaldoAsync(c.Cliente, poupanca)).GetProperty("saldoCentavos").GetInt64());

        // Repetir a chave devolve a mesma transferência, sem criar outro par.
        var chave = Guid.NewGuid();
        var primeira = await TransferirAsync(c.Cliente, Nova(corrente, poupanca, 1_000, chave: chave));
        var repetida = await c.Cliente.PostAsJsonAsync(Rota, Nova(corrente, poupanca, 1_000, chave: chave));
        Assert.Equal(HttpStatusCode.OK, repetida.StatusCode);
        Assert.Equal(primeira.GetProperty("grupoId").GetGuid(), (await JsonAsync(repetida)).GetProperty("grupoId").GetGuid());
        Assert.Equal(4L, await ContarAsync(c.Banco));
    }

    [Fact]
    public async Task Pagamento_de_fatura_reduz_saldo_do_cartao_sem_mudar_o_total_da_fatura()
    {
        await using var c = await IniciarAsync();
        var corrente = await CriarCorrenteAsync(c.Cliente, saldoInicial: 100_000);
        var cartao = await CriarCartaoAsync(c.Cliente);
        var categorias = await JsonAsync(await c.Cliente.GetAsync("/api/v1/categorias"));
        var mercado = categorias.EnumerateArray().Single(k => k.GetProperty("nome").GetString() == "Mercado").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await c.Cliente.PostAsJsonAsync(RotaDeLancamentos, new
        {
            chaveIdempotente = Guid.NewGuid(), contaId = cartao, natureza = "DESPESA", valorCentavos = 25_000L, data = "2026-11-04",
            descricao = "Supermercado", categoriaId = mercado, parcelas = 1,
        })).StatusCode);
        var antes = await SaldoAsync(c.Cliente, cartao);
        Assert.Equal(-25_000, antes.GetProperty("saldoCentavos").GetInt64());
        Assert.Equal(25_000, antes.GetProperty("faturaAberta").GetProperty("totalCentavos").GetInt64());

        var pagamento = await TransferirAsync(c.Cliente, Nova(corrente, cartao, 25_000, "2026-11-05", "Pagamento da fatura"));

        Assert.Equal(JsonValueKind.Null, pagamento.GetProperty("entrada").GetProperty("faturaVencimento").ValueKind);
        Assert.Equal(JsonValueKind.Null, pagamento.GetProperty("saida").GetProperty("faturaVencimento").ValueKind);
        var depois = await SaldoAsync(c.Cliente, cartao);
        Assert.Equal(0, depois.GetProperty("saldoCentavos").GetInt64());
        Assert.Equal(25_000, depois.GetProperty("faturaAberta").GetProperty("totalCentavos").GetInt64());
        Assert.Equal(75_000, (await SaldoAsync(c.Cliente, corrente)).GetProperty("saldoCentavos").GetInt64());
    }

    [Fact]
    public async Task Transferencia_invalida_nao_grava_nada()
    {
        await using var c = await IniciarAsync();
        var corrente = await CriarCorrenteAsync(c.Cliente);
        var outroCasal = Guid.NewGuid();
        var contaDeFora = Guid.NewGuid();
        await ExecutarAsync(c.Banco, $"INSERT INTO identidade.casais (id, nome_familia) VALUES ('{outroCasal}', 'Outra')");
        await ExecutarAsync(c.Banco,
            "INSERT INTO contas.contas (id, casal_id, tipo, apelido, saldo_inicial_centavos, saldo_inicial_em) " +
            $"VALUES ('{contaDeFora}', '{outroCasal}', 'CORRENTE', 'Alheia', 0, '2026-01-01')");

        Assert.Equal(HttpStatusCode.BadRequest, (await c.Cliente.PostAsJsonAsync(Rota, Nova(corrente, corrente))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Cliente.PostAsJsonAsync(Rota, Nova(corrente, contaDeFora))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Cliente.PostAsJsonAsync(Rota, Nova(contaDeFora, corrente))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Cliente.PostAsJsonAsync(Rota, Nova(corrente, Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Cliente.PostAsJsonAsync(Rota, Nova(corrente, contaDeFora, valor: 0))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Cliente.PostAsJsonAsync(Rota, Nova(corrente, contaDeFora, descricao: " "))).StatusCode);

        Assert.Equal(0L, await ContarAsync(c.Banco));
    }

    [Fact]
    public async Task Apagar_uma_perna_apaga_as_duas()
    {
        await using var c = await IniciarAsync();
        var corrente = await CriarCorrenteAsync(c.Cliente);
        var poupanca = await CriarCorrenteAsync(c.Cliente, "Poupança");
        var criada = await TransferirAsync(c.Cliente, Nova(corrente, poupanca));
        var grupo = criada.GetProperty("grupoId").GetGuid();
        var entrada = criada.GetProperty("entrada").GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await c.Cliente.DeleteAsync($"{RotaDeLancamentos}/{entrada}")).StatusCode);

        Assert.Empty(await ExtratoAsync(c.Cliente, "mes=2026-11"));
        Assert.Equal(2L, await ContarAsync(c.Banco, $"grupo_id = '{grupo}' AND is_deleted"));
    }

    [Fact]
    public async Task Editar_valor_muda_as_duas_pernas()
    {
        await using var c = await IniciarAsync();
        var corrente = await CriarCorrenteAsync(c.Cliente);
        var poupanca = await CriarCorrenteAsync(c.Cliente, "Poupança");
        var criada = await TransferirAsync(c.Cliente, Nova(corrente, poupanca, 50_000));
        var entrada = criada.GetProperty("entrada").GetProperty("id").GetGuid();

        var resposta = await c.Cliente.PutAsJsonAsync($"{RotaDeLancamentos}/{entrada}", new
        {
            natureza = "TRANSFERENCIA", valorCentavos = 42_000L, data = "2026-11-03", descricao = "Reserva ajustada",
        });

        Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);
        var extrato = await ExtratoAsync(c.Cliente, "mes=2026-11");
        Assert.Equal(2, extrato.Length);
        var porConta = extrato.ToDictionary(l => l.GetProperty("conta").GetProperty("id").GetGuid());
        Assert.Equal(-42_000, porConta[corrente].GetProperty("valorCentavos").GetInt64());
        Assert.Equal(42_000, porConta[poupanca].GetProperty("valorCentavos").GetInt64());
        Assert.All(extrato, l =>
        {
            Assert.Equal("2026-11-03", l.GetProperty("data").GetString());
            Assert.Equal("Reserva ajustada", l.GetProperty("descricao").GetString());
            Assert.Equal("TRANSFERENCIA", l.GetProperty("natureza").GetString());
        });

        // Transferência não vira despesa, nem ganha categoria, pela rota de lançamentos.
        var categorias = await JsonAsync(await c.Cliente.GetAsync("/api/v1/categorias"));
        var mercado = categorias.EnumerateArray().Single(k => k.GetProperty("nome").GetString() == "Mercado").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Cliente.PutAsJsonAsync($"{RotaDeLancamentos}/{entrada}", new
        {
            natureza = "DESPESA", valorCentavos = 42_000L, data = "2026-11-03", descricao = "Virou despesa", categoriaId = mercado,
        })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Cliente.PutAsJsonAsync($"{RotaDeLancamentos}/{entrada}", new
        {
            natureza = "TRANSFERENCIA", valorCentavos = 42_000L, data = "2026-11-03", descricao = "Com categoria", categoriaId = mercado,
        })).StatusCode);
    }
}
