using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GestorFinanceiro.Api.Infra;
using GestorFinanceiro.IntegrationTests.Infra;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Contracts;
using GestorFinanceiro.SharedKernel.Aplicacao;
using GestorFinanceiro.UnitTests;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GestorFinanceiro.IntegrationTests;

/// <summary>
/// Patrimônio por mês (IConsultaDeSaldos). Relógio: 05/11/2026; contas com saldo inicial em 01/09/2026 salvo indicação.
/// </summary>
[Collection("postgres")]
public class PatrimonioTests(PostgresFixture pg)
{
    private const string SqlAna = "SELECT id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'";
    private const string SqlCasal = "SELECT casal_id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'";

    internal sealed class CasalFixo { public Guid? Id { get; set; } }

    internal sealed class UsuarioDoTeste(IHttpContextAccessor acessor, CasalFixo fixo) : IUsuarioAtual
    {
        private IUsuarioAtual Atual => acessor.HttpContext is null ? new UsuarioSistema(fixo.Id) : new UsuarioAtualHttp(acessor);
        public Guid? UsuarioId => Atual.UsuarioId;
        public Guid? CasalId => Atual.CasalId;
        public string? Ip => Atual.Ip;
    }

    internal sealed record Contexto(ApiDeTeste Api, BancoDeTeste Banco, HttpClient Cliente, Guid Casal) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Api.DisposeAsync();

        public async Task<IReadOnlyList<PatrimonioDoMes>> PatrimonioAsync()
        {
            await using var escopo = Api.Services.CreateAsyncScope();
            return await escopo.ServiceProvider.GetRequiredService<IConsultaDeSaldos>().PatrimonioPorMesAsync(new DateOnly(2025, 12, 1), 12);
        }

        public async Task<IReadOnlyList<SaldoDeConta>> SaldosAsync(DateOnly em)
        {
            await using var escopo = Api.Services.CreateAsyncScope();
            return await escopo.ServiceProvider.GetRequiredService<IConsultaDeSaldos>().SaldosAsync(em);
        }
    }

    internal static async Task<T> EscalarAsync<T>(BancoDeTeste banco, string sql)
    {
        await using var db = new NpgsqlConnection(banco.ConexaoSuperusuario);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, db);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    internal static async Task ExecutarAsync(BancoDeTeste banco, string sql)
    {
        await using var db = new NpgsqlConnection(banco.ConexaoSuperusuario);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, db);
        await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task<JsonElement> JsonAsync(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    internal static async Task<Contexto> IniciarAsync(PostgresFixture pg)
    {
        var banco = await pg.NovoBancoAsync();
        var fixo = new CasalFixo();
        var api = new ApiDeTeste(banco, relogio: new RelogioFixo(), servicos: s =>
        {
            s.AddSingleton(fixo);
            s.AddScoped<IUsuarioAtual, UsuarioDoTeste>();
        });
        var pedido = new CadastroDoCasalDto(
            api.CodigoInstalacao, "Família Teste",
            new ConjugeNovoDto("Ana Teste", CpfSintetico.Gerar(1), "senha-da-ana-123"),
            new ConjugeNovoDto("Bruno Teste", CpfSintetico.Gerar(2), "senha-do-bruno-1"));
        Assert.Equal(HttpStatusCode.Created, (await api.CreateClient().PostAsJsonAsync("/api/v1/casal", pedido)).StatusCode);
        var casal = await EscalarAsync<Guid>(banco, SqlCasal);
        fixo.Id = casal;
        var ana = await EscalarAsync<Guid>(banco, SqlAna);
        return new Contexto(api, banco, await api.ClienteLogadoAsync(ana), casal);
    }

    internal static async Task<Guid> CriarContaAsync(HttpClient cliente, object corpo)
    {
        var resposta = await cliente.PostAsJsonAsync("/api/v1/contas", corpo);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return Guid.Parse(resposta.Headers.Location!.ToString().Split('/').Last());
    }

    internal static Task<Guid> CriarCartaoAsync(HttpClient cliente, string apelido = "Itaú Visa", long saldo = 0) =>
        CriarContaAsync(cliente, new
        {
            tipo = "CARTAO", apelido, saldoInicialCentavos = saldo, saldoInicialEm = "2026-09-01",
            diaFechamento = 1, diaVencimento = 10, limiteCentavos = 8_000_000L,
        });

    internal static Task<Guid> CriarCorrenteAsync(HttpClient cliente) =>
        CriarContaAsync(cliente, new { tipo = "CORRENTE", apelido = "Corrente", saldoInicialCentavos = 1_000_000L, saldoInicialEm = "2026-09-01" });

    internal static async Task<Guid> CategoriaAsync(HttpClient cliente, string nome)
    {
        var lista = await JsonAsync(await cliente.GetAsync("/api/v1/categorias"));
        return lista.EnumerateArray().Single(c => c.GetProperty("nome").GetString() == nome).GetProperty("id").GetGuid();
    }

    internal static async Task LancarAsync(HttpClient cliente, Guid conta, long valor, string data, int parcelas = 1)
    {
        var resposta = await cliente.PostAsJsonAsync("/api/v1/lancamentos", new
        {
            chaveIdempotente = Guid.NewGuid(), contaId = conta, natureza = "DESPESA", valorCentavos = valor, data,
            descricao = "Teste", contraparte = (string?)null, categoriaId = await CategoriaAsync(cliente, "Mercado"), parcelas,
        });
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
    }

    internal static async Task PagarAsync(HttpClient cliente, Guid origem, Guid cartao, long valor, string data)
    {
        var resposta = await cliente.PostAsJsonAsync("/api/v1/transferencias", new
        {
            chaveIdempotente = Guid.NewGuid(), contaOrigemId = origem, contaDestinoId = cartao, valorCentavos = valor, data,
            descricao = "Pagamento da fatura",
        });
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
    }


    private static string Mes(IReadOnlyList<PatrimonioDoMes> p, string mes) => p.Single(x => x.Mes == mes).TotalCentavos?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "nulo";

    private static Task<Guid> CriarPoupancaAsync(HttpClient cliente, string inicio, long saldo = 5_000_000) =>
        CriarContaAsync(cliente, new { tipo = "POUPANCA", apelido = "Poupança", saldoInicialCentavos = saldo, saldoInicialEm = inicio });

    [Fact]
    public async Task Conta_nao_entra_antes_do_saldo_inicial()
    {
        await using var c = await IniciarAsync(pg);
        await CriarCorrenteAsync(c.Cliente);
        await CriarPoupancaAsync(c.Cliente, "2026-10-15");

        var p = await c.PatrimonioAsync();

        Assert.Equal(12, p.Count);
        Assert.Equal("2025-12", p[0].Mes);
        Assert.Equal("2026-11", p[11].Mes);
        Assert.Equal("1000000", Mes(p, "2026-09"));
        Assert.Equal("6000000", Mes(p, "2026-10"));
    }

    [Fact]
    public async Task Mes_sem_nenhuma_conta_e_nulo()
    {
        await using var c = await IniciarAsync(pg);
        await CriarCorrenteAsync(c.Cliente);

        var p = await c.PatrimonioAsync();

        Assert.Equal("nulo", Mes(p, "2026-08"));
        Assert.Null(p.Single(x => x.Mes == "2025-12").TotalCentavos);
    }

    [Fact]
    public async Task Cartao_entra_negativo_e_parcela_futura_nao()
    {
        await using var c = await IniciarAsync(pg);
        var cartao = await CriarCartaoAsync(c.Cliente);
        await LancarAsync(c.Cliente, cartao, 100_000, "2026-10-15", parcelas: 10);

        var p = await c.PatrimonioAsync();

        Assert.Equal("-10000", Mes(p, "2026-11"));
        Assert.Equal("-10000", Mes(p, "2026-10"));
        Assert.Equal("0", Mes(p, "2026-09"));
    }

    [Fact]
    public async Task Conta_arquivada_continua_no_patrimonio()
    {
        await using var c = await IniciarAsync(pg);
        var conta = await CriarCorrenteAsync(c.Cliente);
        await ExecutarAsync(c.Banco, $"UPDATE contas.contas SET arquivada = true WHERE id = '{conta}'");

        Assert.Equal("1000000", Mes(await c.PatrimonioAsync(), "2026-11"));
    }

    [Fact]
    public async Task Patrimonio_de_hoje_igual_a_soma_dos_saldos()
    {
        await using var c = await IniciarAsync(pg);
        var corrente = await CriarCorrenteAsync(c.Cliente);
        var cartao = await CriarCartaoAsync(c.Cliente);
        await CriarPoupancaAsync(c.Cliente, "2026-10-15");
        await LancarAsync(c.Cliente, cartao, 100_000, "2026-10-15", parcelas: 10);
        await PagarAsync(c.Cliente, corrente, cartao, 3_000, "2026-11-03");

        var p = await c.PatrimonioAsync();
        var soma = (await c.SaldosAsync(new DateOnly(2026, 11, 5))).Sum(s => s.SaldoCentavos);

        Assert.Equal(soma, p[^1].TotalCentavos);
    }

    [Fact]
    public async Task Patrimonio_ignora_outro_casal()
    {
        await using var c = await IniciarAsync(pg);
        await CriarCorrenteAsync(c.Cliente);
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        var outro = Guid.NewGuid();
        var contaAlheia = Guid.NewGuid();
        await ExecutarAsync(c.Banco, $"INSERT INTO identidade.casais (id, nome_familia) VALUES ('{outro}', 'Outra')");
        await ExecutarAsync(c.Banco,
            "INSERT INTO contas.contas (id, casal_id, tipo, apelido, saldo_inicial_centavos, saldo_inicial_em) " +
            $"VALUES ('{contaAlheia}', '{outro}', 'CORRENTE', 'Alheia', 9000000, '2026-09-01')");
        await ExecutarAsync(c.Banco,
            "INSERT INTO lancamentos.transacoes (id, casal_id, conta_id, categoria_id, data, valor_centavos, natureza, descricao_cifrada, origem, id_externo) VALUES " +
            $"('{Guid.NewGuid()}', '{outro}', '{contaAlheia}', '{mercado}', '2026-10-15', -77000, 'DESPESA', '\\x01', 'MANUAL', '{Guid.NewGuid()}')");

        var p = await c.PatrimonioAsync();

        Assert.Equal("1000000", Mes(p, "2026-10"));
        Assert.Equal("1000000", Mes(p, "2026-11"));
    }
}
