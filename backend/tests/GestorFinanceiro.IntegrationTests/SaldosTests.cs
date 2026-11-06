using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GestorFinanceiro.IntegrationTests.Infra;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.UnitTests;
using Npgsql;

namespace GestorFinanceiro.IntegrationTests;

/// <summary>Saldos e totais de fatura: somados no banco, respeitando casal, soft delete e a data do saldo inicial.</summary>
[Collection("postgres")]
public class SaldosTests(PostgresFixture pg)
{
    private const string SqlAna = "SELECT id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'";
    private const string SqlCasal = "SELECT casal_id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'";

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

    private async Task<(ApiDeTeste Api, BancoDeTeste Banco, HttpClient Cliente, Guid Casal)> IniciarAsync(RelogioFixo? relogio = null)
    {
        var banco = await pg.NovoBancoAsync();
        var api = new ApiDeTeste(banco, relogio: relogio);
        var pedido = new CadastroDoCasalDto(
            api.CodigoInstalacao, "Família Teste",
            new ConjugeNovoDto("Ana Teste", CpfSintetico.Gerar(1), "senha-da-ana-123"),
            new ConjugeNovoDto("Bruno Teste", CpfSintetico.Gerar(2), "senha-do-bruno-1"));
        Assert.Equal(HttpStatusCode.Created, (await api.CreateClient().PostAsJsonAsync("/api/v1/casal", pedido)).StatusCode);
        var ana = await EscalarAsync<Guid>(banco, SqlAna);
        var casal = await EscalarAsync<Guid>(banco, SqlCasal);
        return (api, banco, await api.ClienteLogadoAsync(ana), casal);
    }

    private static async Task<Guid> CriarContaAsync(HttpClient cliente, object corpo)
    {
        var resposta = await cliente.PostAsJsonAsync("/api/v1/contas", corpo);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return Guid.Parse(resposta.Headers.Location!.ToString().Split('/').Last());
    }

    private static Task<Guid> CriarCorrenteAsync(HttpClient cliente, string apelido, long saldo, string em = "2026-11-01") =>
        CriarContaAsync(cliente, new { tipo = "CORRENTE", apelido, saldoInicialCentavos = saldo, saldoInicialEm = em });

    private static Task<Guid> CriarCartaoAsync(HttpClient cliente) =>
        CriarContaAsync(cliente, new
        {
            tipo = "CARTAO", apelido = "Nubank", saldoInicialCentavos = 0L, saldoInicialEm = "2026-11-01",
            diaFechamento = 5, diaVencimento = 12, limiteCentavos = 800_000L,
        });

    private static Task InserirAsync(BancoDeTeste banco, Guid casal, Guid conta, string data, long valor, string natureza,
        string? faturaVencimento = null, bool apagada = false) =>
        ExecutarAsync(banco,
            "INSERT INTO lancamentos.transacoes (id, casal_id, conta_id, data, valor_centavos, natureza, descricao_cifrada, origem, id_externo, fatura_vencimento, is_deleted) " +
            $"VALUES ('{Guid.NewGuid()}', '{casal}', '{conta}', '{data}', {valor}, '{natureza}', '\\x01', 'MANUAL', '{Guid.NewGuid()}', " +
            $"{(faturaVencimento is null ? "NULL" : $"'{faturaVencimento}'")}, {(apagada ? "true" : "false")})");

    private static async Task<JsonElement> SaldoDaContaAsync(HttpClient cliente, Guid conta, string em)
    {
        var resposta = await cliente.GetAsync($"/api/v1/saldos?em={em}");
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        return (await JsonAsync(resposta)).EnumerateArray().Single(s => s.GetProperty("contaId").GetGuid() == conta);
    }

    [Fact]
    public async Task Saldo_soma_saldo_inicial_e_transacoes_do_periodo()
    {
        var (api, banco, cliente, casal) = await IniciarAsync();
        await using var _ = api;
        var conta = await CriarCorrenteAsync(cliente, "Itaú", 100_000);
        await InserirAsync(banco, casal, conta, "2026-11-02", -2_500, "DESPESA");
        await InserirAsync(banco, casal, conta, "2026-11-03", 10_000, "RECEITA");
        await InserirAsync(banco, casal, conta, "2026-11-04", -3_000, "TRANSFERENCIA");
        await InserirAsync(banco, casal, conta, "2026-11-04", -9_999, "DESPESA", apagada: true);
        await InserirAsync(banco, casal, conta, "2026-11-20", -50_000, "DESPESA");

        var saldo = await SaldoDaContaAsync(cliente, conta, "2026-11-10");

        Assert.Equal(104_500, saldo.GetProperty("saldoCentavos").GetInt64());
        Assert.Equal(JsonValueKind.Null, saldo.GetProperty("faturaAberta").ValueKind);
        Assert.Equal(JsonValueKind.Null, saldo.GetProperty("faturaFechada").ValueKind);
        Assert.Equal(54_500, (await SaldoDaContaAsync(cliente, conta, "2026-11-30")).GetProperty("saldoCentavos").GetInt64());
    }

    [Fact]
    public async Task Sem_parametro_usa_hoje_no_fuso_de_sao_paulo()
    {
        // 06/11 02:30 em UTC ainda é 05/11 23:30 em São Paulo: o lançamento do dia 6 não entra.
        var (api, banco, cliente, casal) = await IniciarAsync(new RelogioFixo { Agora = new DateTimeOffset(2026, 11, 6, 2, 30, 0, TimeSpan.Zero) });
        await using var _ = api;
        var conta = await CriarCorrenteAsync(cliente, "Itaú", 0);
        await InserirAsync(banco, casal, conta, "2026-11-05", 1_000, "RECEITA");
        await InserirAsync(banco, casal, conta, "2026-11-06", 2_000, "RECEITA");

        var lista = await JsonAsync(await cliente.GetAsync("/api/v1/saldos"));

        Assert.Equal(1_000, lista.EnumerateArray().Single().GetProperty("saldoCentavos").GetInt64());
    }

    [Fact]
    public async Task Transacao_antes_do_saldo_inicial_nao_conta()
    {
        var (api, banco, cliente, casal) = await IniciarAsync();
        await using var _ = api;
        var conta = await CriarCorrenteAsync(cliente, "Itaú", 100_000, em: "2026-11-01");
        await InserirAsync(banco, casal, conta, "2026-10-31", -40_000, "DESPESA");
        await InserirAsync(banco, casal, conta, "2026-11-01", -1_000, "DESPESA");

        var saldo = await SaldoDaContaAsync(cliente, conta, "2026-11-10");

        Assert.Equal(99_000, saldo.GetProperty("saldoCentavos").GetInt64());
    }

    [Fact]
    public async Task Fatura_aberta_e_fechada_do_cartao()
    {
        var (api, banco, cliente, casal) = await IniciarAsync();
        await using var _ = api;
        var cartao = await CriarCartaoAsync(cliente);
        // Fatura que vence em 12/11 (fecha em 05/11): duas compras e um estorno.
        await InserirAsync(banco, casal, cartao, "2026-11-03", -10_000, "DESPESA", "2026-11-12");
        await InserirAsync(banco, casal, cartao, "2026-11-05", -5_000, "DESPESA", "2026-11-12");
        await InserirAsync(banco, casal, cartao, "2026-11-04", 2_000, "RECEITA", "2026-11-12");
        // Pagamento da fatura: transferência, não entra no total.
        await InserirAsync(banco, casal, cartao, "2026-11-09", 13_000, "TRANSFERENCIA", "2026-11-12");
        // Compra depois do fechamento: fatura que vence em 12/12.
        await InserirAsync(banco, casal, cartao, "2026-11-06", -7_000, "DESPESA", "2026-12-12");
        await InserirAsync(banco, casal, cartao, "2026-11-07", -999, "DESPESA", "2026-12-12", apagada: true);

        var saldo = await SaldoDaContaAsync(cliente, cartao, "2026-11-10");

        var aberta = saldo.GetProperty("faturaAberta");
        Assert.Equal("2026-12-12", aberta.GetProperty("vencimento").GetString());
        Assert.Equal("2026-12-05", aberta.GetProperty("fechamento").GetString());
        Assert.Equal(7_000, aberta.GetProperty("totalCentavos").GetInt64());
        var fechada = saldo.GetProperty("faturaFechada");
        Assert.Equal("2026-11-12", fechada.GetProperty("vencimento").GetString());
        Assert.Equal("2026-11-05", fechada.GetProperty("fechamento").GetString());
        Assert.Equal(13_000, fechada.GetProperty("totalCentavos").GetInt64());
        Assert.Equal(-7_000, saldo.GetProperty("saldoCentavos").GetInt64());
    }

    [Fact]
    public async Task Saldo_nao_mistura_casais()
    {
        var (api, banco, cliente, casal) = await IniciarAsync();
        await using var _ = api;
        var conta = await CriarCorrenteAsync(cliente, "Itaú", 1_000);
        var outroCasal = Guid.NewGuid();
        var contaDeFora = Guid.NewGuid();
        await ExecutarAsync(banco, $"INSERT INTO identidade.casais (id, nome_familia) VALUES ('{outroCasal}', 'Outra')");
        await ExecutarAsync(banco,
            "INSERT INTO contas.contas (id, casal_id, tipo, apelido, saldo_inicial_centavos, saldo_inicial_em) " +
            $"VALUES ('{contaDeFora}', '{outroCasal}', 'CORRENTE', 'Alheia', 777, '2026-11-01')");
        await InserirAsync(banco, outroCasal, contaDeFora, "2026-11-02", 5_000, "RECEITA");
        await InserirAsync(banco, casal, conta, "2026-11-02", 500, "RECEITA");

        var resposta = await cliente.GetAsync("/api/v1/saldos?em=2026-11-10");
        var lista = (await JsonAsync(resposta)).EnumerateArray().ToArray();

        Assert.Equal(conta, lista.Single().GetProperty("contaId").GetGuid());
        Assert.Equal(1_500, lista.Single().GetProperty("saldoCentavos").GetInt64());
    }

    [Fact]
    public async Task Exige_sessao()
    {
        var (api, _, _, _) = await IniciarAsync();
        await using var _ = api;

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/v1/saldos")).StatusCode);
    }
}
