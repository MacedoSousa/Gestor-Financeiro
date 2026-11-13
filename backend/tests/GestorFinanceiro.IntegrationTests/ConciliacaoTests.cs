using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GestorFinanceiro.IntegrationTests.Infra;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.UnitTests;
using Npgsql;

namespace GestorFinanceiro.IntegrationTests;

/// <summary>
/// Conciliação de saldo: o casal informa o saldo do banco numa poupança ou investimento; sob o lock do casal o servidor
/// recalcula o saldo de hoje, recusa tela desatualizada (409) e lança a diferença positiva como Rendimentos.
/// </summary>
[Collection("postgres")]
public class ConciliacaoTests(PostgresFixture pg)
{
    private const string SqlAna = "SELECT id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'";
    private const string SqlCasal = "SELECT casal_id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'";
    private const string TituloTelaDesatualizada = "O saldo mudou desde que a tela foi aberta. Confira de novo.";

    private sealed record Contexto(ApiDeTeste Api, BancoDeTeste Banco, HttpClient Cliente, Guid Casal) : IAsyncDisposable
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
        var casal = await EscalarAsync<Guid>(banco, SqlCasal);
        return new Contexto(api, banco, await api.ClienteLogadoAsync(ana), casal);
    }

    private static async Task<Guid> CriarContaAsync(HttpClient cliente, string tipo = "POUPANCA", long saldo = 100_000)
    {
        var resposta = await cliente.PostAsJsonAsync("/api/v1/contas",
            new { tipo, apelido = $"Conta {tipo}", saldoInicialCentavos = saldo, saldoInicialEm = "2026-11-01" });
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return Guid.Parse(resposta.Headers.Location!.ToString().Split('/').Last());
    }

    private static Task<HttpResponseMessage> ConciliarAsync(HttpClient cliente, Guid conta, long? informado, long? exibido) =>
        cliente.PostAsJsonAsync($"/api/v1/contas/{conta}/conciliacao",
            new { saldoInformadoCentavos = informado, saldoExibidoCentavos = exibido });

    private static async Task<long> SaldoDeHojeAsync(HttpClient cliente, Guid conta)
    {
        var lista = await JsonAsync(await cliente.GetAsync("/api/v1/saldos"));
        return lista.EnumerateArray().Single(s => s.GetProperty("contaId").GetGuid() == conta).GetProperty("saldoCentavos").GetInt64();
    }

    private static Task<long> TransacoesAsync(BancoDeTeste banco) =>
        EscalarAsync<long>(banco, "SELECT count(*) FROM lancamentos.transacoes");

    private static async Task<string> TituloAsync(HttpResponseMessage r) => (await JsonAsync(r)).GetProperty("title").GetString()!;

    private static Task InserirDespesaAsync(BancoDeTeste banco, Guid casal, Guid conta, string data, long valor) =>
        ExecutarAsync(banco,
            "INSERT INTO lancamentos.transacoes (id, casal_id, conta_id, data, valor_centavos, natureza, descricao_cifrada, origem, id_externo, revisao) " +
            $"VALUES ('{Guid.NewGuid()}', '{casal}', '{conta}', '{data}', {-valor}, 'DESPESA', '\\x01', 'MANUAL', '{Guid.NewGuid()}', 'A_REVISAR')");

    [Fact]
    public async Task Diferenca_positiva_lanca_rendimentos()
    {
        await using var c = await IniciarAsync();
        var conta = await CriarContaAsync(c.Cliente);

        var resposta = await ConciliarAsync(c.Cliente, conta, 103_500, 100_000);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var json = await JsonAsync(resposta);
        Assert.Equal("LANCADO", json.GetProperty("resultado").GetString());
        Assert.Equal(3_500, json.GetProperty("diferencaCentavos").GetInt64());
        var lancamento = json.GetProperty("lancamento");
        Assert.Equal("RECEITA", lancamento.GetProperty("natureza").GetString());
        Assert.Equal(3_500, lancamento.GetProperty("valorCentavos").GetInt64());
        Assert.Equal("2026-11-05", lancamento.GetProperty("data").GetString());
        Assert.Equal("Rendimentos (conciliação)", lancamento.GetProperty("descricao").GetString());
        Assert.Equal("MANUAL", lancamento.GetProperty("origem").GetString());
        Assert.Equal(conta, lancamento.GetProperty("conta").GetProperty("id").GetGuid());
        var id = lancamento.GetProperty("id").GetGuid();
        Assert.Equal("RENDIMENTOS", await EscalarAsync<string>(c.Banco,
            $"SELECT k.papel FROM lancamentos.transacoes t JOIN lancamentos.categorias k ON k.id = t.categoria_id WHERE t.id = '{id}'"));
        Assert.StartsWith("conciliacao:", await EscalarAsync<string>(c.Banco, $"SELECT id_externo FROM lancamentos.transacoes WHERE id = '{id}'"));
        // Descrição cifrada como todo lançamento: o texto não está no banco.
        Assert.False(await EscalarAsync<bool>(c.Banco,
            $"SELECT position(convert_to('Rendimentos', 'UTF8') in descricao_cifrada) > 0 FROM lancamentos.transacoes WHERE id = '{id}'"));
        Assert.Equal(103_500, await SaldoDeHojeAsync(c.Cliente, conta));
        // A conciliação antes de abrir as categorias não impede as padrão: há uma só Rendimentos e as demais existem.
        var categorias = (await JsonAsync(await c.Cliente.GetAsync("/api/v1/categorias"))).EnumerateArray().ToList();
        Assert.Single(categorias, k => k.GetProperty("papel").GetString() == "RENDIMENTOS");
        Assert.Contains(categorias, k => k.GetProperty("nome").GetString() == "Mercado");
    }

    [Fact]
    public async Task Saldo_igual_confere_sem_lancar()
    {
        await using var c = await IniciarAsync();
        var conta = await CriarContaAsync(c.Cliente, "INVESTIMENTO");

        var resposta = await ConciliarAsync(c.Cliente, conta, 100_000, 100_000);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var json = await JsonAsync(resposta);
        Assert.Equal("CONFERE", json.GetProperty("resultado").GetString());
        Assert.Equal(0, json.GetProperty("diferencaCentavos").GetInt64());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("lancamento").ValueKind);
        Assert.Equal(0, await TransacoesAsync(c.Banco));
    }

    [Fact]
    public async Task Saldo_menor_nao_lanca()
    {
        await using var c = await IniciarAsync();
        var conta = await CriarContaAsync(c.Cliente);

        var resposta = await ConciliarAsync(c.Cliente, conta, 90_000, 100_000);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var json = await JsonAsync(resposta);
        Assert.Equal("MENOR", json.GetProperty("resultado").GetString());
        Assert.Equal(-10_000, json.GetProperty("diferencaCentavos").GetInt64());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("lancamento").ValueKind);
        Assert.Equal(0, await TransacoesAsync(c.Banco));
    }

    [Fact]
    public async Task Tela_desatualizada_responde_409()
    {
        await using var c = await IniciarAsync();
        var conta = await CriarContaAsync(c.Cliente);
        await InserirDespesaAsync(c.Banco, c.Casal, conta, "2026-11-03", 1_000);

        var resposta = await ConciliarAsync(c.Cliente, conta, 103_500, 100_000);

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Equal(TituloTelaDesatualizada, await TituloAsync(resposta));
        Assert.Equal(1, await TransacoesAsync(c.Banco));
    }

    [Fact]
    public async Task Conciliacao_dupla_em_paralelo_lanca_uma_vez()
    {
        await using var c = await IniciarAsync();
        var conta = await CriarContaAsync(c.Cliente);
        await c.Cliente.GetAsync("/api/v1/categorias"); // as padrão já existem: as duas correm direto para o lançamento.

        var respostas = await Task.WhenAll(
            ConciliarAsync(c.Cliente, conta, 103_500, 100_000),
            ConciliarAsync(c.Cliente, conta, 103_500, 100_000));

        var codigos = respostas.Select(r => r.StatusCode).OrderBy(s => s).ToArray();
        Assert.Equal(1L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM lancamentos.transacoes WHERE natureza = 'RECEITA'"));
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], codigos);
        var ok = respostas.Single(r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal("LANCADO", (await JsonAsync(ok)).GetProperty("resultado").GetString());
        Assert.Equal(TituloTelaDesatualizada, await TituloAsync(respostas.Single(r => r.StatusCode == HttpStatusCode.Conflict)));
        Assert.Equal(103_500, await SaldoDeHojeAsync(c.Cliente, conta));
    }

    [Fact]
    public async Task Conta_de_outro_tipo_400()
    {
        await using var c = await IniciarAsync();
        foreach (var tipo in new[] { "CORRENTE", "DINHEIRO" })
        {
            var conta = await CriarContaAsync(c.Cliente, tipo);
            var resposta = await ConciliarAsync(c.Cliente, conta, 103_500, 100_000);
            Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
            Assert.Equal("Só contas de poupança ou investimento são conciliadas aqui.", await TituloAsync(resposta));
        }
        Assert.Equal(0, await TransacoesAsync(c.Banco));
    }

    [Fact]
    public async Task Conta_arquivada_400()
    {
        await using var c = await IniciarAsync();
        var conta = await CriarContaAsync(c.Cliente);
        await ExecutarAsync(c.Banco, $"UPDATE contas.contas SET arquivada = true WHERE id = '{conta}'");

        var resposta = await ConciliarAsync(c.Cliente, conta, 103_500, 100_000);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal("Conta arquivada: escolha outra.", await TituloAsync(resposta));
        Assert.Equal(0, await TransacoesAsync(c.Banco));
    }

    [Fact]
    public async Task Conta_de_outro_casal_404()
    {
        await using var c = await IniciarAsync();
        var outroCasal = Guid.NewGuid();
        var contaDeFora = Guid.NewGuid();
        await ExecutarAsync(c.Banco, $"INSERT INTO identidade.casais (id, nome_familia) VALUES ('{outroCasal}', 'Outra')");
        await ExecutarAsync(c.Banco,
            "INSERT INTO contas.contas (id, casal_id, tipo, apelido, saldo_inicial_centavos, saldo_inicial_em) " +
            $"VALUES ('{contaDeFora}', '{outroCasal}', 'POUPANCA', 'Alheia', 100000, '2026-11-01')");

        var resposta = await ConciliarAsync(c.Cliente, contaDeFora, 103_500, 100_000);

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ConciliarAsync(c.Cliente, Guid.NewGuid(), 103_500, 100_000)).StatusCode);
        Assert.Equal(0, await TransacoesAsync(c.Banco));
    }

    [Fact]
    public async Task Campos_ausentes_400()
    {
        await using var c = await IniciarAsync();
        var conta = await CriarContaAsync(c.Cliente);

        var resposta = await c.Cliente.PostAsJsonAsync($"/api/v1/contas/{conta}/conciliacao", new { });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        var erros = (await JsonAsync(resposta)).GetProperty("errors");
        Assert.True(erros.TryGetProperty("saldoInformadoCentavos", out _));
        Assert.True(erros.TryGetProperty("saldoExibidoCentavos", out _));
        Assert.Equal(0, await TransacoesAsync(c.Banco));
    }

    [Fact]
    public async Task Fora_do_limite_400()
    {
        await using var c = await IniciarAsync();
        var conta = await CriarContaAsync(c.Cliente);

        var acima = await ConciliarAsync(c.Cliente, conta, 100_000_000_001, 100_000);
        var abaixo = await ConciliarAsync(c.Cliente, conta, 103_500, -100_000_000_001);

        Assert.Equal(HttpStatusCode.BadRequest, acima.StatusCode);
        Assert.True((await JsonAsync(acima)).GetProperty("errors").TryGetProperty("saldoInformadoCentavos", out _));
        Assert.Equal(HttpStatusCode.BadRequest, abaixo.StatusCode);
        Assert.True((await JsonAsync(abaixo)).GetProperty("errors").TryGetProperty("saldoExibidoCentavos", out _));
        // Os extremos valem: o saldo de hoje é 100.000, então -100.000.000.000 é só "menor".
        var limite = await ConciliarAsync(c.Cliente, conta, -100_000_000_000, 100_000);
        Assert.Equal(HttpStatusCode.OK, limite.StatusCode);
        Assert.Equal("MENOR", (await JsonAsync(limite)).GetProperty("resultado").GetString());
        Assert.Equal(0, await TransacoesAsync(c.Banco));
    }

    [Fact]
    public async Task Conciliacao_funciona_com_Rendimentos_arquivada()
    {
        await using var c = await IniciarAsync();
        var conta = await CriarContaAsync(c.Cliente);
        var rendimentos = (await JsonAsync(await c.Cliente.GetAsync("/api/v1/categorias"))).EnumerateArray()
            .Single(k => k.GetProperty("papel").GetString() == "RENDIMENTOS").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await c.Cliente.PutAsJsonAsync($"/api/v1/categorias/{rendimentos}",
            new { nome = "Rendimentos", tipo = "RECEITA", arquivada = true })).StatusCode);

        var resposta = await ConciliarAsync(c.Cliente, conta, 103_500, 100_000);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var lancamento = (await JsonAsync(resposta)).GetProperty("lancamento");
        Assert.Equal(rendimentos, lancamento.GetProperty("categoria").GetProperty("id").GetGuid());
        Assert.True(lancamento.GetProperty("categoria").GetProperty("arquivada").GetBoolean());
        Assert.Equal(1L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM lancamentos.categorias WHERE papel = 'RENDIMENTOS'"));
    }

    [Fact]
    public async Task Conciliacao_ignora_lancamento_futuro()
    {
        await using var c = await IniciarAsync();
        var conta = await CriarContaAsync(c.Cliente);
        await InserirDespesaAsync(c.Banco, c.Casal, conta, "2026-11-20", 40_000);
        Assert.Equal(100_000, await SaldoDeHojeAsync(c.Cliente, conta));

        var resposta = await ConciliarAsync(c.Cliente, conta, 103_500, 100_000);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var json = await JsonAsync(resposta);
        Assert.Equal("LANCADO", json.GetProperty("resultado").GetString());
        Assert.Equal(3_500, json.GetProperty("diferencaCentavos").GetInt64());
        Assert.Equal(103_500, await SaldoDeHojeAsync(c.Cliente, conta));
    }

    [Fact]
    public async Task Conciliacao_cria_rendimentos_se_faltar()
    {
        await using var c = await IniciarAsync();
        var conta = await CriarContaAsync(c.Cliente);
        await c.Cliente.GetAsync("/api/v1/categorias");
        // O caso que a V0009 deixa sem papel: o casal já tinha uma despesa chamada "Rendimentos".
        await ExecutarAsync(c.Banco, "UPDATE lancamentos.categorias SET papel = NULL, tipo = 'DESPESA' WHERE papel = 'RENDIMENTOS'");

        var resposta = await ConciliarAsync(c.Cliente, conta, 103_500, 100_000);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var categoria = (await JsonAsync(resposta)).GetProperty("lancamento").GetProperty("categoria");
        Assert.Equal("Rendimentos (conciliação)", categoria.GetProperty("nome").GetString());
        Assert.Equal("RECEITA|RENDIMENTOS", await EscalarAsync<string>(c.Banco,
            $"SELECT tipo || '|' || papel FROM lancamentos.categorias WHERE id = '{categoria.GetProperty("id").GetGuid()}'"));
        Assert.Equal("DESPESA", await EscalarAsync<string>(c.Banco,
            "SELECT tipo FROM lancamentos.categorias WHERE nome = 'Rendimentos' AND NOT is_deleted"));

        // Sem nenhuma com o nome nem com o papel, nasce "Rendimentos".
        await ExecutarAsync(c.Banco, "UPDATE lancamentos.categorias SET is_deleted = true WHERE lower(nome) LIKE 'rendimentos%'");
        var segunda = await ConciliarAsync(c.Cliente, conta, 104_000, 103_500);
        Assert.Equal(HttpStatusCode.OK, segunda.StatusCode);
        Assert.Equal("Rendimentos", (await JsonAsync(segunda)).GetProperty("lancamento").GetProperty("categoria").GetProperty("nome").GetString());
    }
}
