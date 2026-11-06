using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GestorFinanceiro.IntegrationTests.Infra;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.UnitTests;
using Npgsql;

namespace GestorFinanceiro.IntegrationTests;

/// <summary>Lançamentos manuais: criar com idempotência, parcelas no cartão, extrato com busca decifrada, editar e apagar por escopo.</summary>
[Collection("postgres")]
public class LancamentosTests(PostgresFixture pg)
{
    private const string Rota = "/api/v1/lancamentos";
    private const string SqlAna = "SELECT id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'";
    private const string SqlBruno = "SELECT id FROM identidade.usuarios WHERE nome_completo = 'Bruno Teste'";
    private const string SqlCasal = "SELECT casal_id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'";

    private sealed record Contexto(ApiDeTeste Api, BancoDeTeste Banco, HttpClient Cliente, Guid Casal, Guid Ana, Guid Bruno) : IAsyncDisposable
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

    private static async Task<List<string>> TextosAsync(BancoDeTeste banco, string sql)
    {
        await using var db = new NpgsqlConnection(banco.ConexaoSuperusuario);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, db);
        await using var leitor = await cmd.ExecuteReaderAsync();
        var lista = new List<string>();
        while (await leitor.ReadAsync()) lista.Add(leitor.GetString(0));
        return lista;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    /// <summary>Relógio padrão: 05/11/2026 09:00 em Brasília.</summary>
    private async Task<Contexto> IniciarAsync(RelogioFixo? relogio = null)
    {
        var banco = await pg.NovoBancoAsync();
        var api = new ApiDeTeste(banco, relogio: relogio ?? new RelogioFixo());
        var pedido = new CadastroDoCasalDto(
            api.CodigoInstalacao, "Família Teste",
            new ConjugeNovoDto("Ana Teste", CpfSintetico.Gerar(1), "senha-da-ana-123"),
            new ConjugeNovoDto("Bruno Teste", CpfSintetico.Gerar(2), "senha-do-bruno-1"));
        Assert.Equal(HttpStatusCode.Created, (await api.CreateClient().PostAsJsonAsync("/api/v1/casal", pedido)).StatusCode);
        var ana = await EscalarAsync<Guid>(banco, SqlAna);
        var bruno = await EscalarAsync<Guid>(banco, SqlBruno);
        var casal = await EscalarAsync<Guid>(banco, SqlCasal);
        return new Contexto(api, banco, await api.ClienteLogadoAsync(ana), casal, ana, bruno);
    }

    private static async Task<Guid> CriarContaAsync(HttpClient cliente, object corpo)
    {
        var resposta = await cliente.PostAsJsonAsync("/api/v1/contas", corpo);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return Guid.Parse(resposta.Headers.Location!.ToString().Split('/').Last());
    }

    private static Task<Guid> CriarCorrenteAsync(HttpClient cliente, string apelido = "Itaú conjunta", Guid? titular = null) =>
        CriarContaAsync(cliente, new { tipo = "CORRENTE", apelido, titularUsuarioId = titular, saldoInicialCentavos = 0L, saldoInicialEm = "2026-01-01" });

    private static Task<Guid> CriarCartaoAsync(HttpClient cliente, string apelido = "Nubank", Guid? titular = null) =>
        CriarContaAsync(cliente, new
        {
            tipo = "CARTAO", apelido, titularUsuarioId = titular, saldoInicialCentavos = 0L, saldoInicialEm = "2026-01-01",
            diaFechamento = 5, diaVencimento = 12, limiteCentavos = 800_000L,
        });

    private static async Task<Guid> CategoriaAsync(HttpClient cliente, string nome)
    {
        var lista = await JsonAsync(await cliente.GetAsync("/api/v1/categorias"));
        return lista.EnumerateArray().Single(c => c.GetProperty("nome").GetString() == nome).GetProperty("id").GetGuid();
    }

    private static object Novo(Guid conta, Guid categoria, long valor = 2_500, string data = "2026-11-04", string descricao = "Padaria",
        string natureza = "DESPESA", string? contraparte = null, int parcelas = 1, Guid? chave = null) => new
    {
        chaveIdempotente = chave ?? Guid.NewGuid(), contaId = conta, natureza, valorCentavos = valor, data, descricao, contraparte,
        categoriaId = categoria, parcelas,
    };

    private static async Task<JsonElement> LancarAsync(HttpClient cliente, object corpo)
    {
        var resposta = await cliente.PostAsJsonAsync(Rota, corpo);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return await JsonAsync(resposta);
    }

    private static async Task<JsonElement[]> ExtratoAsync(HttpClient cliente, string consulta)
    {
        var resposta = await cliente.GetAsync($"{Rota}?{consulta}");
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var json = await JsonAsync(resposta);
        var itens = json.GetProperty("lancamentos").EnumerateArray().ToArray();
        Assert.Equal(itens.Length, json.GetProperty("total").GetInt32());
        return itens;
    }

    private static Task<List<string>> ParcelasAsync(BancoDeTeste banco, Guid grupo) =>
        TextosAsync(banco,
            $"SELECT parcela || '|' || valor_centavos || '|' || is_deleted || '|' || coalesce(fatura_vencimento::text, '') || '|' || data " +
            $"FROM lancamentos.transacoes WHERE grupo_id = '{grupo}' ORDER BY parcela");

    [Fact]
    public async Task Cria_despesa_e_aparece_no_extrato_do_mes()
    {
        await using var c = await IniciarAsync();
        var conta = await CriarCorrenteAsync(c.Cliente);
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");

        var resposta = await c.Cliente.PostAsJsonAsync(Rota, Novo(conta, mercado, 12_345, "2026-11-03", "Feira do sábado", contraparte: "Hortifruti"));

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        var criado = await JsonAsync(resposta);
        var id = criado.GetProperty("id").GetGuid();
        Assert.Equal($"/api/v1/lancamentos/{id}", resposta.Headers.Location!.ToString());
        Assert.Equal(-12_345, criado.GetProperty("valorCentavos").GetInt64());

        Assert.Empty(await ExtratoAsync(c.Cliente, "mes=2026-10"));
        var item = Assert.Single(await ExtratoAsync(c.Cliente, "mes=2026-11"));
        Assert.Equal(id, item.GetProperty("id").GetGuid());
        Assert.Equal("2026-11-03", item.GetProperty("data").GetString());
        Assert.Equal("Feira do sábado", item.GetProperty("descricao").GetString());
        Assert.Equal("Hortifruti", item.GetProperty("contraparte").GetString());
        Assert.Equal(-12_345, item.GetProperty("valorCentavos").GetInt64());
        Assert.Equal("DESPESA", item.GetProperty("natureza").GetString());
        Assert.Equal("MANUAL", item.GetProperty("origem").GetString());
        Assert.Equal("Conjunta", item.GetProperty("quem").GetString());
        Assert.Equal(mercado, item.GetProperty("categoria").GetProperty("id").GetGuid());
        Assert.Equal("Mercado", item.GetProperty("categoria").GetProperty("nome").GetString());
        Assert.False(item.GetProperty("categoria").GetProperty("arquivada").GetBoolean());
        Assert.Equal(conta, item.GetProperty("conta").GetProperty("id").GetGuid());
        Assert.Equal("Itaú conjunta", item.GetProperty("conta").GetProperty("apelido").GetString());
        Assert.Equal("CORRENTE", item.GetProperty("conta").GetProperty("tipo").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("parcela").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("faturaVencimento").ValueKind);

        var receita = await LancarAsync(c.Cliente, Novo(conta, await CategoriaAsync(c.Cliente, "Salário"), 500_000, "2026-11-05", "Salário", natureza: "RECEITA"));
        Assert.Equal(500_000, receita.GetProperty("valorCentavos").GetInt64());
        var extrato = await ExtratoAsync(c.Cliente, "mes=2026-11");
        Assert.Equal(["2026-11-05", "2026-11-03"], extrato.Select(e => e.GetProperty("data").GetString()));

        Assert.Equal(c.Ana, await EscalarAsync<Guid>(c.Banco, $"SELECT autor_usuario_id FROM lancamentos.transacoes WHERE id = '{id}'"));
    }

    [Fact]
    public async Task Descricao_e_contraparte_ficam_cifradas_no_banco_e_fora_da_auditoria()
    {
        await using var c = await IniciarAsync();
        var conta = await CriarCorrenteAsync(c.Cliente);
        var criado = await LancarAsync(c.Cliente, Novo(conta, await CategoriaAsync(c.Cliente, "Saúde"), descricao: "Consulta Dra Zuleica", contraparte: "Clínica Zuleica"));
        var id = criado.GetProperty("id").GetGuid();
        await c.Cliente.PutAsJsonAsync($"{Rota}/{id}", new
        {
            natureza = "DESPESA", valorCentavos = 3_000L, data = "2026-11-04", descricao = "Retorno Dra Zuleica",
            contraparte = "Clínica Zuleica", categoriaId = await CategoriaAsync(c.Cliente, "Saúde"),
        });

        await using var db = new NpgsqlConnection(c.Banco.ConexaoSuperusuario);
        await db.OpenAsync();
        await using (var cmd = new NpgsqlCommand($"SELECT descricao_cifrada, contraparte_cifrada FROM lancamentos.transacoes WHERE id = '{id}'", db))
        await using (var leitor = await cmd.ExecuteReaderAsync())
        {
            Assert.True(await leitor.ReadAsync());
            foreach (var coluna in new[] { 0, 1 })
            {
                var bytes = (byte[])leitor.GetValue(coluna);
                var texto = Encoding.UTF8.GetString(bytes) + Encoding.Latin1.GetString(bytes);
                Assert.DoesNotContain("Zuleica", texto, StringComparison.OrdinalIgnoreCase);
            }
        }

        var eventos = await TextosAsync(c.Banco, $"SELECT alteracoes::text FROM audit.eventos WHERE tabela = 'lancamentos.transacoes' AND entidade_id = '{id}'");
        Assert.True(eventos.Count >= 2);
        Assert.All(eventos, e => Assert.DoesNotContain("Zuleica", e, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(eventos, e => e.Contains("\"descricao_cifrada\": \"alterado\"", StringComparison.Ordinal));
        Assert.Contains(eventos, e => e.Contains("\"contraparte_cifrada\": \"alterado\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Repetir_a_mesma_chave_devolve_o_mesmo_lancamento()
    {
        await using var c = await IniciarAsync();
        var conta = await CriarCorrenteAsync(c.Cliente);
        var cartao = await CriarCartaoAsync(c.Cliente);
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        var chave = Guid.NewGuid();
        var primeiro = await LancarAsync(c.Cliente, Novo(conta, mercado, chave: chave));

        var repetido = await c.Cliente.PostAsJsonAsync(Rota, Novo(conta, mercado, chave: chave));

        Assert.Equal(HttpStatusCode.OK, repetido.StatusCode);
        Assert.Equal(primeiro.GetProperty("id").GetGuid(), (await JsonAsync(repetido)).GetProperty("id").GetGuid());
        Assert.Equal(1L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM lancamentos.transacoes"));

        // Parcelado: a chave vira chave:1..N e repetir devolve a primeira parcela.
        var chaveParcelada = Guid.NewGuid();
        var parcelado = await LancarAsync(c.Cliente, Novo(cartao, mercado, 9_000, parcelas: 3, chave: chaveParcelada));
        var repetidoParcelado = await c.Cliente.PostAsJsonAsync(Rota, Novo(cartao, mercado, 9_000, parcelas: 3, chave: chaveParcelada));
        Assert.Equal(HttpStatusCode.OK, repetidoParcelado.StatusCode);
        Assert.Equal(parcelado.GetProperty("id").GetGuid(), (await JsonAsync(repetidoParcelado)).GetProperty("id").GetGuid());
        Assert.Equal(4L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM lancamentos.transacoes"));
        Assert.Equal(
            [$"{chaveParcelada}:1", $"{chaveParcelada}:2", $"{chaveParcelada}:3"],
            await TextosAsync(c.Banco, $"SELECT id_externo FROM lancamentos.transacoes WHERE id_externo LIKE '{chaveParcelada}%' ORDER BY parcela"));
        Assert.Equal(chave.ToString(), (await TextosAsync(c.Banco, $"SELECT id_externo FROM lancamentos.transacoes WHERE id = '{primeiro.GetProperty("id").GetGuid()}'")).Single());
    }

    [Fact]
    public async Task Mesma_chave_em_paralelo_cria_um_so_lancamento()
    {
        await using var c = await IniciarAsync();
        var conta = await CriarCorrenteAsync(c.Cliente);
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");

        for (var rodada = 0; rodada < 5; rodada++)
        {
            var chave = Guid.NewGuid();
            var respostas = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => c.Cliente.PostAsJsonAsync(Rota, Novo(conta, mercado, chave: chave))));

            Assert.Equal(1, respostas.Count(r => r.StatusCode == HttpStatusCode.Created));
            Assert.Equal(7, respostas.Count(r => r.StatusCode == HttpStatusCode.OK));
            var ids = new HashSet<Guid>();
            foreach (var r in respostas) ids.Add((await JsonAsync(r)).GetProperty("id").GetGuid());
            Assert.Single(ids);
            Assert.Equal(1L, await EscalarAsync<long>(c.Banco, $"SELECT count(*) FROM lancamentos.transacoes WHERE id_externo = '{chave}'"));
        }
    }

    [Fact]
    public async Task Parcelas_somam_o_total_e_caem_em_faturas_seguidas()
    {
        await using var c = await IniciarAsync();
        var cartao = await CriarCartaoAsync(c.Cliente);
        var lazer = await CategoriaAsync(c.Cliente, "Lazer");

        // 06/11 é depois do fechamento (dia 5): primeira parcela na fatura que vence em 12/12.
        var primeira = await LancarAsync(c.Cliente, Novo(cartao, lazer, 10_000, "2026-11-06", "Fone", parcelas: 3));

        Assert.Equal(1, primeira.GetProperty("parcela").GetInt32());
        Assert.Equal(3, primeira.GetProperty("parcelas").GetInt32());
        Assert.Equal(-3_334, primeira.GetProperty("valorCentavos").GetInt64());
        Assert.Equal("2026-12-12", primeira.GetProperty("faturaVencimento").GetString());
        var grupo = primeira.GetProperty("grupoId").GetGuid();
        Assert.Equal(
            ["1|-3334|false|2026-12-12|2026-11-06", "2|-3333|false|2027-01-12|2026-12-06", "3|-3333|false|2027-02-12|2027-01-06"],
            await ParcelasAsync(c.Banco, grupo));
        Assert.Equal(-10_000L, await EscalarAsync<decimal>(c.Banco, $"SELECT sum(valor_centavos) FROM lancamentos.transacoes WHERE grupo_id = '{grupo}'"));
        Assert.Equal(3L, await EscalarAsync<long>(c.Banco, $"SELECT count(*) FROM lancamentos.transacoes WHERE grupo_id = '{grupo}' AND parcelas = 3"));

        var dezembro = Assert.Single(await ExtratoAsync(c.Cliente, "mes=2026-12"));
        Assert.Equal(2, dezembro.GetProperty("parcela").GetInt32());
        Assert.Equal(grupo, dezembro.GetProperty("grupoId").GetGuid());
    }

    [Fact]
    public async Task Parcelas_em_conta_corrente_respondem_400()
    {
        await using var c = await IniciarAsync();
        var conta = await CriarCorrenteAsync(c.Cliente);
        var cartao = await CriarCartaoAsync(c.Cliente);
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");

        Assert.Equal(HttpStatusCode.BadRequest, (await c.Cliente.PostAsJsonAsync(Rota, Novo(conta, mercado, 9_000, parcelas: 3))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Cliente.PostAsJsonAsync(Rota, Novo(cartao, mercado, 9_000, parcelas: 25))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Cliente.PostAsJsonAsync(Rota, Novo(cartao, mercado, 2, parcelas: 3))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Cliente.PostAsJsonAsync(Rota, Novo(cartao, mercado, 0))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Cliente.PostAsJsonAsync(Rota, Novo(cartao, mercado, descricao: " "))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Cliente.PostAsJsonAsync(Rota, Novo(cartao, mercado, descricao: new string('x', 141)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Cliente.PostAsJsonAsync(Rota, Novo(cartao, mercado, natureza: "TRANSFERENCIA"))).StatusCode);
        Assert.Equal(0L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM lancamentos.transacoes"));
    }

    [Fact]
    public async Task Editar_proximas_nao_altera_parcela_de_fatura_fechada()
    {
        // Hoje: 05/11/2026 em Brasília. A fatura que vence em 12/11 fecha hoje: ainda aberta (fecha a partir de 06/11).
        await using var c = await IniciarAsync();
        var cartao = await CriarCartaoAsync(c.Cliente);
        var lazer = await CategoriaAsync(c.Cliente, "Lazer");
        // Compra de 20/09: parcelas nas faturas que vencem 12/10 (fechada em 05/10), 12/11, 12/12 e 12/01.
        var primeira = await LancarAsync(c.Cliente, Novo(cartao, lazer, 40_000, "2026-09-20", "TV", parcelas: 4));
        var grupo = primeira.GetProperty("grupoId").GetGuid();

        // A partir da parcela 1 (fatura fechada), só texto: todas recebem a descrição nova; valores e datas ficam.
        var texto = await c.Cliente.PutAsJsonAsync($"{Rota}/{primeira.GetProperty("id").GetGuid()}?escopo=proximos", new
        {
            natureza = "DESPESA", valorCentavos = 10_000L, data = "2026-09-20", descricao = "TV da sala", contraparte = (string?)null,
            categoriaId = lazer,
        });
        Assert.Equal(HttpStatusCode.NoContent, texto.StatusCode);
        Assert.Equal("TV da sala", Assert.Single(await ExtratoAsync(c.Cliente, "mes=2026-09")).GetProperty("descricao").GetString());

        // A partir da parcela 2 (fecha hoje: ainda aberta), com valor novo: a 1 (fechada) não muda.
        var segunda = Assert.Single(await ExtratoAsync(c.Cliente, "mes=2026-10")).GetProperty("id").GetGuid();
        var resposta = await c.Cliente.PutAsJsonAsync($"{Rota}/{segunda}?escopo=proximos", new
        {
            natureza = "DESPESA", valorCentavos = 9_000L, data = "2026-10-20", descricao = "TV da sala", categoriaId = lazer,
        });

        Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);
        Assert.Equal(
            ["1|-10000|false|2026-10-12|2026-09-20", "2|-9000|false|2026-11-12|2026-10-20", "3|-9000|false|2026-12-12|2026-11-20", "4|-9000|false|2027-01-12|2026-12-20"],
            await ParcelasAsync(c.Banco, grupo));

        // Escopo "este" (padrão) muda só a parcela escolhida.
        var so = await c.Cliente.PutAsJsonAsync($"{Rota}/{segunda}", new
        {
            natureza = "DESPESA", valorCentavos = 8_000L, data = "2026-10-20", descricao = "TV da sala", categoriaId = lazer,
        });
        Assert.Equal(HttpStatusCode.NoContent, so.StatusCode);
        Assert.Equal(
            ["1|-10000|false|2026-10-12|2026-09-20", "2|-8000|false|2026-11-12|2026-10-20", "3|-9000|false|2026-12-12|2026-11-20", "4|-9000|false|2027-01-12|2026-12-20"],
            await ParcelasAsync(c.Banco, grupo));

        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.Cliente.PutAsJsonAsync($"{Rota}/{segunda}?escopo=todas", new { natureza = "DESPESA", valorCentavos = 1L, data = "2026-10-20", descricao = "x", categoriaId = lazer })).StatusCode);
    }

    [Fact]
    public async Task Apagar_proximas_e_soft_delete_das_parcelas_seguintes()
    {
        await using var c = await IniciarAsync();
        var cartao = await CriarCartaoAsync(c.Cliente);
        var lazer = await CategoriaAsync(c.Cliente, "Lazer");
        var primeira = await LancarAsync(c.Cliente, Novo(cartao, lazer, 40_000, "2026-11-05", "Bicicleta", parcelas: 4));
        var grupo = primeira.GetProperty("grupoId").GetGuid();
        var segunda = Assert.Single(await ExtratoAsync(c.Cliente, "mes=2026-12")).GetProperty("id").GetGuid();
        var quarta = Assert.Single(await ExtratoAsync(c.Cliente, "mes=2027-02")).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await c.Cliente.DeleteAsync($"{Rota}/{quarta}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.Cliente.DeleteAsync($"{Rota}/{segunda}?escopo=proximos")).StatusCode);

        var linhas = await ParcelasAsync(c.Banco, grupo);
        Assert.Equal(4, linhas.Count);
        Assert.Equal(["false", "true", "true", "true"], linhas.Select(l => l.Split('|')[2]));
        Assert.Equal(3L, await EscalarAsync<long>(c.Banco, $"SELECT count(*) FROM lancamentos.transacoes WHERE grupo_id = '{grupo}' AND deleted_at IS NOT NULL AND deleted_by = '{c.Ana}'"));
        Assert.Empty(await ExtratoAsync(c.Cliente, "mes=2026-12"));
        Assert.Single(await ExtratoAsync(c.Cliente, "mes=2026-11"));
        Assert.Equal(HttpStatusCode.NotFound, (await c.Cliente.DeleteAsync($"{Rota}/{segunda}")).StatusCode);
    }

    [Fact]
    public async Task Busca_encontra_por_descricao_sem_acento_e_sem_caixa()
    {
        await using var c = await IniciarAsync();
        var conta = await CriarCorrenteAsync(c.Cliente);
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        await LancarAsync(c.Cliente, Novo(conta, mercado, descricao: "Açaí da Praça"));
        await LancarAsync(c.Cliente, Novo(conta, mercado, descricao: "Supermercado", contraparte: "Pão de Açúcar"));
        await LancarAsync(c.Cliente, Novo(conta, mercado, descricao: "Acai em outubro", data: "2026-10-30"));

        Assert.Equal("Açaí da Praça", Assert.Single(await ExtratoAsync(c.Cliente, "mes=2026-11&q=ACAI")).GetProperty("descricao").GetString());
        Assert.Single(await ExtratoAsync(c.Cliente, "mes=2026-11&q=praca"));
        Assert.Equal("Supermercado", Assert.Single(await ExtratoAsync(c.Cliente, "mes=2026-11&q=acucar")).GetProperty("descricao").GetString());
        Assert.Empty(await ExtratoAsync(c.Cliente, "mes=2026-11&q=padaria"));
        Assert.Equal(2, (await ExtratoAsync(c.Cliente, "mes=2026-11")).Length);
    }

    [Fact]
    public async Task Filtro_por_titular_conjunta_e_conjuge()
    {
        await using var c = await IniciarAsync();
        var conjunta = await CriarCorrenteAsync(c.Cliente, "Itaú conjunta");
        var daAna = await CriarCorrenteAsync(c.Cliente, "Inter da Ana", c.Ana);
        var doBruno = await CriarCartaoAsync(c.Cliente, "Nubank do Bruno", c.Bruno);
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        await LancarAsync(c.Cliente, Novo(conjunta, mercado, descricao: "Na conjunta"));
        await LancarAsync(c.Cliente, Novo(daAna, mercado, descricao: "Na da Ana"));
        await LancarAsync(c.Cliente, Novo(doBruno, mercado, descricao: "No do Bruno"));

        var conj = Assert.Single(await ExtratoAsync(c.Cliente, "mes=2026-11&titular=conjunta"));
        Assert.Equal("Na conjunta", conj.GetProperty("descricao").GetString());
        Assert.Equal("Conjunta", conj.GetProperty("quem").GetString());
        var eu = Assert.Single(await ExtratoAsync(c.Cliente, "mes=2026-11&titular=eu"));
        Assert.Equal("Na da Ana", eu.GetProperty("descricao").GetString());
        Assert.Equal("Ana", eu.GetProperty("quem").GetString());
        var conjuge = Assert.Single(await ExtratoAsync(c.Cliente, "mes=2026-11&titular=conjuge"));
        Assert.Equal("No do Bruno", conjuge.GetProperty("descricao").GetString());
        Assert.Equal("Bruno", conjuge.GetProperty("quem").GetString());
        Assert.Equal("CARTAO", conjuge.GetProperty("conta").GetProperty("tipo").GetString());

        Assert.Equal("Na da Ana", Assert.Single(await ExtratoAsync(c.Cliente, $"mes=2026-11&contaId={daAna}")).GetProperty("descricao").GetString());
        Assert.Equal(3, (await ExtratoAsync(c.Cliente, "mes=2026-11&origem=MANUAL")).Length);
        Assert.Empty(await ExtratoAsync(c.Cliente, "mes=2026-11&origem=WHATSAPP"));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Cliente.GetAsync($"{Rota}?mes=2026-11&titular=vizinho")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Cliente.GetAsync($"{Rota}?mes=2026-13")).StatusCode);
    }

    [Fact]
    public async Task Lancamento_de_outro_casal_responde_404()
    {
        await using var c = await IniciarAsync();
        var minhaConta = await CriarCorrenteAsync(c.Cliente);
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        var outroCasal = Guid.NewGuid();
        var contaDeFora = Guid.NewGuid();
        var categoriaDeFora = Guid.NewGuid();
        var lancamentoDeFora = Guid.NewGuid();
        await ExecutarAsync(c.Banco, $"INSERT INTO identidade.casais (id, nome_familia) VALUES ('{outroCasal}', 'Outra')");
        await ExecutarAsync(c.Banco,
            "INSERT INTO contas.contas (id, casal_id, tipo, apelido, saldo_inicial_centavos, saldo_inicial_em) " +
            $"VALUES ('{contaDeFora}', '{outroCasal}', 'CORRENTE', 'Alheia', 0, '2026-01-01')");
        await ExecutarAsync(c.Banco, $"INSERT INTO lancamentos.categorias (id, casal_id, nome, tipo) VALUES ('{categoriaDeFora}', '{outroCasal}', 'Alheia', 'DESPESA')");
        await ExecutarAsync(c.Banco,
            "INSERT INTO lancamentos.transacoes (id, casal_id, conta_id, categoria_id, data, valor_centavos, natureza, descricao_cifrada, origem, id_externo) " +
            $"VALUES ('{lancamentoDeFora}', '{outroCasal}', '{contaDeFora}', '{categoriaDeFora}', '2026-11-02', -1000, 'DESPESA', '\\x01', 'MANUAL', '{Guid.NewGuid()}')");

        Assert.Equal(HttpStatusCode.NotFound, (await c.Cliente.GetAsync($"{Rota}/{lancamentoDeFora}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Cliente.PutAsJsonAsync($"{Rota}/{lancamentoDeFora}", new
        {
            natureza = "DESPESA", valorCentavos = 1L, data = "2026-11-02", descricao = "Meu", categoriaId = mercado,
        })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Cliente.DeleteAsync($"{Rota}/{lancamentoDeFora}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Cliente.PostAsJsonAsync(Rota, Novo(contaDeFora, mercado))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Cliente.PostAsJsonAsync(Rota, Novo(minhaConta, categoriaDeFora))).StatusCode);
        Assert.Empty(await ExtratoAsync(c.Cliente, "mes=2026-11"));
        Assert.False(await EscalarAsync<bool>(c.Banco, $"SELECT is_deleted FROM lancamentos.transacoes WHERE id = '{lancamentoDeFora}'"));
        Assert.Equal(1L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM lancamentos.transacoes"));
    }

    [Fact]
    public async Task Categoria_de_receita_em_despesa_responde_400()
    {
        await using var c = await IniciarAsync();
        var conta = await CriarCorrenteAsync(c.Cliente);
        var salario = await CategoriaAsync(c.Cliente, "Salário");
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");

        Assert.Equal(HttpStatusCode.BadRequest, (await c.Cliente.PostAsJsonAsync(Rota, Novo(conta, salario))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Cliente.PostAsJsonAsync(Rota, Novo(conta, mercado, natureza: "RECEITA"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Cliente.PostAsJsonAsync(Rota, Novo(conta, Guid.NewGuid()))).StatusCode);

        var criado = await LancarAsync(c.Cliente, Novo(conta, mercado));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Cliente.PutAsJsonAsync($"{Rota}/{criado.GetProperty("id").GetGuid()}", new
        {
            natureza = "DESPESA", valorCentavos = 1L, data = "2026-11-02", descricao = "x", categoriaId = salario,
        })).StatusCode);
        Assert.Equal(1L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM lancamentos.transacoes"));
    }

    [Fact]
    public async Task Categoria_arquivada_responde_400()
    {
        await using var c = await IniciarAsync();
        var conta = await CriarCorrenteAsync(c.Cliente);
        var lazer = await CategoriaAsync(c.Cliente, "Lazer");
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        var antigo = await LancarAsync(c.Cliente, Novo(conta, lazer, descricao: "Cinema"));
        Assert.Equal(HttpStatusCode.NoContent, (await c.Cliente.PutAsJsonAsync($"/api/v1/categorias/{lazer}", new { nome = "Lazer", tipo = "DESPESA", arquivada = true })).StatusCode);

        var resposta = await c.Cliente.PostAsJsonAsync(Rota, Novo(conta, lazer));

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal("Categoria arquivada: escolha outra.", (await JsonAsync(resposta)).GetProperty("title").GetString());
        // O lançamento antigo continua mostrando a categoria, marcada como arquivada, e pode ser editado sem trocá-la.
        var item = Assert.Single(await ExtratoAsync(c.Cliente, "mes=2026-11"));
        Assert.True(item.GetProperty("categoria").GetProperty("arquivada").GetBoolean());
        var id = antigo.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await c.Cliente.PutAsJsonAsync($"{Rota}/{id}", new
        {
            natureza = "DESPESA", valorCentavos = 3_000L, data = "2026-11-04", descricao = "Cinema e pipoca", categoriaId = lazer,
        })).StatusCode);
        // Trocar para uma categoria ativa pode; voltar para a arquivada, não.
        Assert.Equal(HttpStatusCode.NoContent, (await c.Cliente.PutAsJsonAsync($"{Rota}/{id}", new
        {
            natureza = "DESPESA", valorCentavos = 3_000L, data = "2026-11-04", descricao = "Cinema e pipoca", categoriaId = mercado,
        })).StatusCode);
        var troca = await c.Cliente.PutAsJsonAsync($"{Rota}/{id}", new
        {
            natureza = "DESPESA", valorCentavos = 3_000L, data = "2026-11-04", descricao = "Cinema e pipoca", categoriaId = lazer,
        });
        Assert.Equal(HttpStatusCode.BadRequest, troca.StatusCode);
        Assert.Equal("Categoria arquivada: escolha outra.", (await JsonAsync(troca)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Conta_arquivada_responde_400()
    {
        await using var c = await IniciarAsync();
        var conta = await CriarCorrenteAsync(c.Cliente);
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        var chave = Guid.NewGuid();
        var antes = await LancarAsync(c.Cliente, Novo(conta, mercado, chave: chave));
        await ExecutarAsync(c.Banco, $"UPDATE contas.contas SET arquivada = true WHERE id = '{conta}'");

        var resposta = await c.Cliente.PostAsJsonAsync(Rota, Novo(conta, mercado));

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal("Conta arquivada: escolha outra.", (await JsonAsync(resposta)).GetProperty("title").GetString());
        Assert.Equal(1L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM lancamentos.transacoes"));
        // A repetição de um POST já aceito continua devolvendo o lançamento.
        var repetido = await c.Cliente.PostAsJsonAsync(Rota, Novo(conta, mercado, chave: chave));
        Assert.Equal(HttpStatusCode.OK, repetido.StatusCode);
        Assert.Equal(antes.GetProperty("id").GetGuid(), (await JsonAsync(repetido)).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Apagar_categoria_e_lancar_em_paralelo_nao_deixa_lancamento_orfao()
    {
        await using var c = await IniciarAsync();
        var conta = await CriarCorrenteAsync(c.Cliente);
        await CategoriaAsync(c.Cliente, "Mercado"); // cria as padrão antes das rodadas

        for (var rodada = 0; rodada < 20; rodada++)
        {
            var criar = await c.Cliente.PostAsJsonAsync("/api/v1/categorias", new { nome = $"Temporaria {rodada}", tipo = "DESPESA" });
            Assert.Equal(HttpStatusCode.Created, criar.StatusCode);
            var categoria = Guid.Parse(criar.Headers.Location!.ToString().Split('/').Last());

            var apagar = c.Cliente.DeleteAsync($"/api/v1/categorias/{categoria}");
            var lancar = c.Cliente.PostAsJsonAsync(Rota, Novo(conta, categoria));
            var (rApagar, rLancar) = (await apagar, await lancar);

            // Um dos dois ganha: ou apagou (e o lançamento recusou), ou lançou (e o apagar deu 409).
            Assert.True(
                (rApagar.StatusCode == HttpStatusCode.NoContent && rLancar.StatusCode == HttpStatusCode.BadRequest)
                || (rApagar.StatusCode == HttpStatusCode.Conflict && rLancar.StatusCode == HttpStatusCode.Created),
                $"rodada {rodada}: apagar {(int)rApagar.StatusCode}, lançar {(int)rLancar.StatusCode}");
        }

        Assert.Equal(0L, await EscalarAsync<long>(c.Banco,
            "SELECT count(*) FROM lancamentos.transacoes t JOIN lancamentos.categorias k ON k.id = t.categoria_id WHERE NOT t.is_deleted AND k.is_deleted"));
    }

    private static object Edicao(Guid categoria, long valor, string data, string descricao) =>
        new { natureza = "DESPESA", valorCentavos = valor, data, descricao, categoriaId = categoria };

    private static async Task Assert409FaturaFechadaAsync(HttpResponseMessage resposta)
    {
        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Equal("Fatura fechada: só descrição e categoria podem mudar.", (await JsonAsync(resposta)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Editar_data_nao_move_parcela_para_fatura_fechada()
    {
        // Hoje: 05/11/2026. Faturas que vencem até 12/10 estão fechadas; a de 12/11 fecha hoje (ainda aberta).
        await using var c = await IniciarAsync();
        var cartao = await CriarCartaoAsync(c.Cliente);
        var lazer = await CategoriaAsync(c.Cliente, "Lazer");
        var primeira = await LancarAsync(c.Cliente, Novo(cartao, lazer, 30_000, "2026-11-06", "Sofá", parcelas: 3));
        var id = primeira.GetProperty("id").GetGuid();
        var grupo = primeira.GetProperty("grupoId").GetGuid();
        string[] original = ["1|-10000|false|2026-12-12|2026-11-06", "2|-10000|false|2027-01-12|2026-12-06", "3|-10000|false|2027-02-12|2027-01-06"];

        // 01/10 cai na fatura que vence 12/10 (fechada): recusa nos dois escopos e nada muda.
        await Assert409FaturaFechadaAsync(await c.Cliente.PutAsJsonAsync($"{Rota}/{id}?escopo=proximos", Edicao(lazer, 10_000, "2026-10-01", "Sofá")));
        await Assert409FaturaFechadaAsync(await c.Cliente.PutAsJsonAsync($"{Rota}/{id}", Edicao(lazer, 10_000, "2026-10-01", "Sofá")));
        Assert.Equal(original, await ParcelasAsync(c.Banco, grupo));

        // 05/11 cai na fatura que vence 12/11 (fecha hoje, aberta): as três andam juntas.
        Assert.Equal(HttpStatusCode.NoContent,
            (await c.Cliente.PutAsJsonAsync($"{Rota}/{id}?escopo=proximos", Edicao(lazer, 10_000, "2026-11-05", "Sofá"))).StatusCode);
        Assert.Equal(
            ["1|-10000|false|2026-11-12|2026-11-05", "2|-10000|false|2026-12-12|2026-12-05", "3|-10000|false|2027-01-12|2027-01-05"],
            await ParcelasAsync(c.Banco, grupo));

        // Compra à vista: mesma regra.
        var avulsa = (await LancarAsync(c.Cliente, Novo(cartao, lazer, 5_000, "2026-11-06", "Livro"))).GetProperty("id").GetGuid();
        await Assert409FaturaFechadaAsync(await c.Cliente.PutAsJsonAsync($"{Rota}/{avulsa}", Edicao(lazer, 5_000, "2026-09-30", "Livro")));
        Assert.Equal(HttpStatusCode.NoContent, (await c.Cliente.PutAsJsonAsync($"{Rota}/{avulsa}", Edicao(lazer, 5_000, "2026-10-20", "Livro"))).StatusCode);
        Assert.Equal("2026-11-12", (await TextosAsync(c.Banco, $"SELECT fatura_vencimento::text FROM lancamentos.transacoes WHERE id = '{avulsa}'")).Single());
    }

    [Fact]
    public async Task Fatura_fechada_so_aceita_descricao_e_categoria()
    {
        await using var c = await IniciarAsync();
        var cartao = await CriarCartaoAsync(c.Cliente);
        var lazer = await CategoriaAsync(c.Cliente, "Lazer");
        var restaurantes = await CategoriaAsync(c.Cliente, "Restaurantes");
        // 01/10: fatura que vence 12/10, fechada em 05/10.
        var id = (await LancarAsync(c.Cliente, Novo(cartao, lazer, 5_000, "2026-10-01", "Show"))).GetProperty("id").GetGuid();
        const string sqlLinha = "SELECT valor_centavos || '|' || data || '|' || fatura_vencimento || '|' || is_deleted FROM lancamentos.transacoes WHERE id = ";

        await Assert409FaturaFechadaAsync(await c.Cliente.PutAsJsonAsync($"{Rota}/{id}", Edicao(lazer, 6_000, "2026-10-01", "Show")));
        await Assert409FaturaFechadaAsync(await c.Cliente.PutAsJsonAsync($"{Rota}/{id}", Edicao(lazer, 5_000, "2026-10-02", "Show")));
        await Assert409FaturaFechadaAsync(await c.Cliente.PutAsJsonAsync($"{Rota}/{id}", new
        {
            natureza = "RECEITA", valorCentavos = 5_000L, data = "2026-10-01", descricao = "Show", categoriaId = await CategoriaAsync(c.Cliente, "Outras receitas"),
        }));
        await Assert409FaturaFechadaAsync(await c.Cliente.DeleteAsync($"{Rota}/{id}"));
        await Assert409FaturaFechadaAsync(await c.Cliente.DeleteAsync($"{Rota}/{id}?escopo=proximos"));
        Assert.Equal("-5000|2026-10-01|2026-10-12|false", (await TextosAsync(c.Banco, $"{sqlLinha}'{id}'")).Single());

        var texto = await c.Cliente.PutAsJsonAsync($"{Rota}/{id}", new
        {
            natureza = "DESPESA", valorCentavos = 5_000L, data = "2026-10-01", descricao = "Jantar", contraparte = "Bistrô", categoriaId = restaurantes,
        });

        Assert.Equal(HttpStatusCode.NoContent, texto.StatusCode);
        var item = await JsonAsync(await c.Cliente.GetAsync($"{Rota}/{id}"));
        Assert.Equal("Jantar", item.GetProperty("descricao").GetString());
        Assert.Equal("Bistrô", item.GetProperty("contraparte").GetString());
        Assert.Equal(restaurantes, item.GetProperty("categoria").GetProperty("id").GetGuid());
        Assert.Equal("-5000|2026-10-01|2026-10-12|false", (await TextosAsync(c.Banco, $"{sqlLinha}'{id}'")).Single());
    }

    [Fact]
    public async Task Proximas_a_partir_de_parcela_em_fatura_fechada_com_valor_responde_409()
    {
        await using var c = await IniciarAsync();
        var cartao = await CriarCartaoAsync(c.Cliente);
        var lazer = await CategoriaAsync(c.Cliente, "Lazer");
        // Parcela 1 na fatura de 12/10 (fechada); as outras abertas.
        var primeira = await LancarAsync(c.Cliente, Novo(cartao, lazer, 40_000, "2026-09-20", "TV", parcelas: 4));
        var grupo = primeira.GetProperty("grupoId").GetGuid();
        var antes = await ParcelasAsync(c.Banco, grupo);

        await Assert409FaturaFechadaAsync(await c.Cliente.PutAsJsonAsync(
            $"{Rota}/{primeira.GetProperty("id").GetGuid()}?escopo=proximos", Edicao(lazer, 9_000, "2026-09-20", "TV da sala")));

        // Tudo ou nada: nem as parcelas abertas mudaram, nem o texto.
        Assert.Equal(antes, await ParcelasAsync(c.Banco, grupo));
        Assert.All(await ExtratoAsync(c.Cliente, "mes=2026-11"), l => Assert.Equal("TV", l.GetProperty("descricao").GetString()));
    }

    [Fact]
    public async Task Chave_de_outro_casal_responde_409_sem_vazar()
    {
        await using var c = await IniciarAsync();
        var conta = await CriarCorrenteAsync(c.Cliente);
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        var outroCasal = Guid.NewGuid();
        var contaDeFora = Guid.NewGuid();
        var lancamentoDeFora = Guid.NewGuid();
        var chave = Guid.NewGuid();
        await ExecutarAsync(c.Banco, $"INSERT INTO identidade.casais (id, nome_familia) VALUES ('{outroCasal}', 'Outra')");
        await ExecutarAsync(c.Banco,
            "INSERT INTO contas.contas (id, casal_id, tipo, apelido, saldo_inicial_centavos, saldo_inicial_em) " +
            $"VALUES ('{contaDeFora}', '{outroCasal}', 'CORRENTE', 'Alheia', 0, '2026-01-01')");
        await ExecutarAsync(c.Banco,
            "INSERT INTO lancamentos.transacoes (id, casal_id, conta_id, data, valor_centavos, natureza, descricao_cifrada, origem, id_externo) " +
            $"VALUES ('{lancamentoDeFora}', '{outroCasal}', '{contaDeFora}', '2026-11-02', -43210, 'DESPESA', convert_to('Segredo alheio', 'UTF8'), 'MANUAL', '{chave}')");

        var resposta = await c.Cliente.PostAsJsonAsync(Rota, Novo(conta, mercado, 1_000, chave: chave));

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        var corpo = await resposta.Content.ReadAsStringAsync();
        Assert.Equal("Chave de idempotência já usada.", JsonDocument.Parse(corpo).RootElement.GetProperty("title").GetString());
        foreach (var vazamento in new[] { "43210", "Segredo", "Alheia", lancamentoDeFora.ToString(), contaDeFora.ToString(), outroCasal.ToString(), "2026-11-02" })
            Assert.DoesNotContain(vazamento, corpo, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM lancamentos.transacoes"));
    }

    [Fact]
    public async Task Exige_sessao()
    {
        await using var c = await IniciarAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await c.Api.CreateClient().GetAsync($"{Rota}?mes=2026-11")).StatusCode);
    }
}
