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
/// Faturas a vencer por cartão (restante e atraso). Cartão: fecha dia 1, vence dia 10, saldo inicial em 01/09/2026.
/// Relógio: 05/11/2026 → fechada vence 10/11 (fechou 01/11), aberta vence 10/12.
/// </summary>
[Collection("postgres")]
public class FaturasTests(PostgresFixture pg)
{
    private static readonly DateOnly VencFechada = new(2026, 11, 10);
    private static readonly DateOnly FechFechada = new(2026, 11, 1);
    private static readonly DateOnly VencAberta = new(2026, 12, 10);
    private static readonly DateOnly FechAberta = new(2026, 12, 1);
    private const string SqlAna = "SELECT id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'";
    private const string SqlCasal = "SELECT casal_id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'";

    private sealed class CasalFixo { public Guid? Id { get; set; } }

    private sealed class UsuarioDoTeste(IHttpContextAccessor acessor, CasalFixo fixo) : IUsuarioAtual
    {
        private IUsuarioAtual Atual => acessor.HttpContext is null ? new UsuarioSistema(fixo.Id) : new UsuarioAtualHttp(acessor);
        public Guid? UsuarioId => Atual.UsuarioId;
        public Guid? CasalId => Atual.CasalId;
        public string? Ip => Atual.Ip;
    }

    private sealed record Contexto(ApiDeTeste Api, BancoDeTeste Banco, HttpClient Cliente, Guid Casal) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Api.DisposeAsync();

        public async Task<IReadOnlyList<FaturasDoCartao>> FaturasAsync()
        {
            await using var escopo = Api.Services.CreateAsyncScope();
            return await escopo.ServiceProvider.GetRequiredService<IConsultaDeFaturas>().AVencerAsync();
        }

        public async Task<FaturasDoCartao> DoCartaoAsync(Guid cartao) => (await FaturasAsync()).Single(f => f.ContaId == cartao);
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

    private async Task<Contexto> IniciarAsync()
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

    private static async Task<Guid> CriarContaAsync(HttpClient cliente, object corpo)
    {
        var resposta = await cliente.PostAsJsonAsync("/api/v1/contas", corpo);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return Guid.Parse(resposta.Headers.Location!.ToString().Split('/').Last());
    }

    private static Task<Guid> CriarCartaoAsync(HttpClient cliente, string apelido = "Itaú Visa", long saldo = 0, string saldoEm = "2026-09-01") =>
        CriarContaAsync(cliente, new
        {
            tipo = "CARTAO", apelido, saldoInicialCentavos = saldo, saldoInicialEm = saldoEm,
            diaFechamento = 1, diaVencimento = 10, limiteCentavos = 8_000_000L,
        });

    private static Task<Guid> CriarCorrenteAsync(HttpClient cliente) =>
        CriarContaAsync(cliente, new { tipo = "CORRENTE", apelido = "Corrente", saldoInicialCentavos = 1_000_000L, saldoInicialEm = "2026-09-01" });

    private static async Task<Guid> CategoriaAsync(HttpClient cliente, string nome)
    {
        var lista = await JsonAsync(await cliente.GetAsync("/api/v1/categorias"));
        return lista.EnumerateArray().Single(c => c.GetProperty("nome").GetString() == nome).GetProperty("id").GetGuid();
    }

    private static async Task LancarAsync(HttpClient cliente, Guid conta, long valor, string data, int parcelas = 1)
    {
        var resposta = await cliente.PostAsJsonAsync("/api/v1/lancamentos", new
        {
            chaveIdempotente = Guid.NewGuid(), contaId = conta, natureza = "DESPESA", valorCentavos = valor, data,
            descricao = "Teste", contraparte = (string?)null, categoriaId = await CategoriaAsync(cliente, "Mercado"), parcelas,
        });
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
    }

    private static async Task PagarAsync(HttpClient cliente, Guid origem, Guid cartao, long valor, string data)
    {
        var resposta = await cliente.PostAsJsonAsync("/api/v1/transferencias", new
        {
            chaveIdempotente = Guid.NewGuid(), contaOrigemId = origem, contaDestinoId = cartao, valorCentavos = valor, data,
            descricao = "Pagamento da fatura",
        });
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
    }

    private static void AssertFechada(FaturasDoCartao f, long total, long restante, long atraso)
    {
        Assert.Equal(VencFechada, f.Fechada.Vencimento);
        Assert.Equal(FechFechada, f.Fechada.Fechamento);
        Assert.Equal(total, f.Fechada.TotalCentavos);
        Assert.Equal(restante, f.Fechada.RestanteCentavos);
        Assert.Equal(atraso, f.Fechada.AtrasoCentavos);
    }

    [Fact]
    public async Task Compra_em_10x_so_conta_a_parcela_da_fatura_fechada()
    {
        await using var c = await IniciarAsync();
        var cartao = await CriarCartaoAsync(c.Cliente);
        await LancarAsync(c.Cliente, cartao, 100_000, "2026-10-15", parcelas: 10);

        var f = await c.DoCartaoAsync(cartao);

        AssertFechada(f, total: 10_000, restante: 10_000, atraso: 0);
        Assert.Equal(VencAberta, f.Aberta.Vencimento);
        Assert.Equal(FechAberta, f.Aberta.Fechamento);
        Assert.Equal(0, f.Aberta.TotalAteHojeCentavos);
    }

    [Fact]
    public async Task Fatura_anterior_nao_paga_aparece_como_atraso()
    {
        await using var c = await IniciarAsync();
        var cartao = await CriarCartaoAsync(c.Cliente);
        await LancarAsync(c.Cliente, cartao, 100_000, "2026-10-15", parcelas: 10);
        await LancarAsync(c.Cliente, cartao, 5_000, "2026-09-20");

        AssertFechada(await c.DoCartaoAsync(cartao), total: 10_000, restante: 15_000, atraso: 5_000);
    }

    [Fact]
    public async Task Vencida_desde_o_vencimento_mais_antigo_em_aberto()
    {
        await using var c = await IniciarAsync();
        var cartao = await CriarCartaoAsync(c.Cliente, saldoEm: "2026-07-01");
        var corrente = await CriarCorrenteAsync(c.Cliente);
        await LancarAsync(c.Cliente, cartao, 3_000, "2026-07-15");
        await LancarAsync(c.Cliente, cartao, 4_000, "2026-08-15");
        await LancarAsync(c.Cliente, cartao, 5_000, "2026-09-15");
        await LancarAsync(c.Cliente, cartao, 10_000, "2026-10-15");

        var f = await c.DoCartaoAsync(cartao);
        AssertFechada(f, total: 10_000, restante: 22_000, atraso: 12_000);
        Assert.Equal(new FaturaVencida(new DateOnly(2026, 8, 10), new DateOnly(2026, 8, 1), 12_000), f.Vencida);

        // O pagamento quita a fatura mais antiga primeiro.
        await PagarAsync(c.Cliente, corrente, cartao, 3_000, "2026-11-03");
        Assert.Equal(new FaturaVencida(new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 1), 9_000), (await c.DoCartaoAsync(cartao)).Vencida);

        await PagarAsync(c.Cliente, corrente, cartao, 6_000, "2026-11-03");
        Assert.Equal(new FaturaVencida(new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 1), 3_000), (await c.DoCartaoAsync(cartao)).Vencida);

        await PagarAsync(c.Cliente, corrente, cartao, 3_000, "2026-11-03");
        Assert.Null((await c.DoCartaoAsync(cartao)).Vencida);
    }

    [Fact]
    public async Task Saldo_inicial_antigo_vence_na_fatura_do_seu_periodo()
    {
        await using var c = await IniciarAsync();
        var cartao = await CriarCartaoAsync(c.Cliente, saldo: -20_000, saldoEm: "2026-07-15");
        await LancarAsync(c.Cliente, cartao, 5_000, "2026-09-15");

        var f = await c.DoCartaoAsync(cartao);

        AssertFechada(f, total: 0, restante: 25_000, atraso: 25_000);
        Assert.Equal(new FaturaVencida(new DateOnly(2026, 8, 10), new DateOnly(2026, 8, 1), 25_000), f.Vencida);
    }

    [Fact]
    public async Task Pagamento_parcial_abate_o_restante()
    {
        await using var c = await IniciarAsync();
        var cartao = await CriarCartaoAsync(c.Cliente);
        var corrente = await CriarCorrenteAsync(c.Cliente);
        await LancarAsync(c.Cliente, cartao, 100_000, "2026-10-15", parcelas: 10);
        await PagarAsync(c.Cliente, corrente, cartao, 3_000, "2026-11-03");

        AssertFechada(await c.DoCartaoAsync(cartao), total: 10_000, restante: 7_000, atraso: 0);
    }

    [Fact]
    public async Task Fatura_paga_some_dos_vencimentos()
    {
        await using var c = await IniciarAsync();
        var cartao = await CriarCartaoAsync(c.Cliente);
        var corrente = await CriarCorrenteAsync(c.Cliente);
        await LancarAsync(c.Cliente, cartao, 100_000, "2026-10-15", parcelas: 10);
        await PagarAsync(c.Cliente, corrente, cartao, 10_000, "2026-11-03");

        AssertFechada(await c.DoCartaoAsync(cartao), total: 10_000, restante: 0, atraso: 0);
    }

    [Fact]
    public async Task Parcela_com_data_passada_e_fatura_aberta_nao_entra_na_fechada()
    {
        await using var c = await IniciarAsync();
        var cartao = await CriarCartaoAsync(c.Cliente);
        await LancarAsync(c.Cliente, cartao, 20_000, "2026-11-03", parcelas: 2);

        var f = await c.DoCartaoAsync(cartao);

        AssertFechada(f, total: 0, restante: 0, atraso: 0);
        Assert.Equal(10_000, f.Aberta.TotalAteHojeCentavos);
    }

    [Fact]
    public async Task Saldo_inicial_negativo_do_cartao_entra_no_restante()
    {
        await using var c = await IniciarAsync();
        var cartao = await CriarCartaoAsync(c.Cliente, saldo: -20_000);

        AssertFechada(await c.DoCartaoAsync(cartao), total: 0, restante: 20_000, atraso: 20_000);
    }

    [Fact]
    public async Task Saldo_inicial_do_cadastro_no_periodo_da_fechada_nao_e_atraso()
    {
        await using var c = await IniciarAsync();
        var cartao = await CriarCartaoAsync(c.Cliente, saldo: -150_000, saldoEm: "2026-11-05");

        AssertFechada(await c.DoCartaoAsync(cartao), total: 0, restante: 150_000, atraso: 0);
    }

    [Fact]
    public async Task Saldo_inicial_durante_o_periodo_da_fechada_nao_e_atraso()
    {
        await using var c = await IniciarAsync();
        var cartao = await CriarCartaoAsync(c.Cliente, saldo: -30_000, saldoEm: "2026-10-15");

        AssertFechada(await c.DoCartaoAsync(cartao), total: 0, restante: 30_000, atraso: 0);
    }

    [Fact]
    public async Task Atraso_nunca_passa_do_restante()
    {
        await using var c = await IniciarAsync();
        var cartao = await CriarCartaoAsync(c.Cliente);
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        await LancarAsync(c.Cliente, cartao, 20_000, "2026-09-20");
        // Só estorno (receita) na fatura fechada: total negativo, dívida antiga de 20.000 e restante 15.000.
        await ExecutarAsync(c.Banco,
            "INSERT INTO lancamentos.transacoes (id, casal_id, conta_id, categoria_id, data, valor_centavos, natureza, descricao_cifrada, origem, id_externo, fatura_vencimento) VALUES " +
            $"('{Guid.NewGuid()}', '{c.Casal}', '{cartao}', '{mercado}', '2026-10-20', 5000, 'RECEITA', '\\x01', 'MANUAL', '{Guid.NewGuid()}', '2026-11-10')");

        var f = await c.DoCartaoAsync(cartao);

        Assert.Equal(15_000, f.Fechada.RestanteCentavos);
        Assert.Equal(15_000, f.Fechada.AtrasoCentavos);
    }

    [Fact]
    public async Task Lancamento_anterior_ao_saldo_inicial_fica_fora_do_devido_e_do_total()
    {
        await using var c = await IniciarAsync();
        var cartao = await CriarCartaoAsync(c.Cliente);
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        await LancarAsync(c.Cliente, cartao, 100_000, "2026-10-15", parcelas: 10);
        // A API recusa data anterior ao saldo inicial; semeia direto, como nos outros testes de integração.
        await ExecutarAsync(c.Banco,
            "INSERT INTO lancamentos.transacoes (id, casal_id, conta_id, categoria_id, data, valor_centavos, natureza, descricao_cifrada, origem, id_externo, fatura_vencimento) VALUES " +
            $"('{Guid.NewGuid()}', '{c.Casal}', '{cartao}', '{mercado}', '2026-08-20', -50000, 'DESPESA', '\\x01', 'MANUAL', '{Guid.NewGuid()}', '2026-11-10')");

        AssertFechada(await c.DoCartaoAsync(cartao), total: 10_000, restante: 10_000, atraso: 0);
    }

    [Fact]
    public async Task Faturas_ignoram_outro_casal()
    {
        await using var c = await IniciarAsync();
        var cartao = await CriarCartaoAsync(c.Cliente);
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        var outro = Guid.NewGuid();
        var cartaoAlheio = Guid.NewGuid();
        await ExecutarAsync(c.Banco, $"INSERT INTO identidade.casais (id, nome_familia) VALUES ('{outro}', 'Outra')");
        await ExecutarAsync(c.Banco,
            "INSERT INTO contas.contas (id, casal_id, tipo, apelido, saldo_inicial_centavos, saldo_inicial_em, dia_fechamento, dia_vencimento) " +
            $"VALUES ('{cartaoAlheio}', '{outro}', 'CARTAO', 'Alheio', 0, '2026-09-01', 1, 10)");
        // Compra do outro casal no cartão dele e (por garantia) com o id do nosso cartão.
        await ExecutarAsync(c.Banco,
            "INSERT INTO lancamentos.transacoes (id, casal_id, conta_id, categoria_id, data, valor_centavos, natureza, descricao_cifrada, origem, id_externo, fatura_vencimento) VALUES " +
            $"('{Guid.NewGuid()}', '{outro}', '{cartaoAlheio}', '{mercado}', '2026-10-15', -77000, 'DESPESA', '\\x01', 'MANUAL', '{Guid.NewGuid()}', '2026-11-10'), " +
            $"('{Guid.NewGuid()}', '{outro}', '{cartao}', '{mercado}', '2026-10-16', -66000, 'DESPESA', '\\x01', 'MANUAL', '{Guid.NewGuid()}', '2026-11-10')");

        var faturas = await c.FaturasAsync();

        var unica = Assert.Single(faturas);
        Assert.Equal(cartao, unica.ContaId);
        AssertFechada(unica, total: 0, restante: 0, atraso: 0);
    }
}
