using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GestorFinanceiro.IntegrationTests.Infra;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.Modules.Investimentos.Contracts;
using GestorFinanceiro.SharedKernel.Aplicacao;
using GestorFinanceiro.UnitTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using static GestorFinanceiro.IntegrationTests.PatrimonioTests;

namespace GestorFinanceiro.IntegrationTests;

/// <summary>GET /api/v1/visao-geral. Relógio: 05/11/2026 (hoje 05/11).</summary>
[Collection("postgres")]
public class VisaoGeralTests(PostgresFixture pg)
{
    private const string Erro = "Não foi possível carregar";

    private sealed class MetasQueFalha : IConsultaDeMetas
    {
        public Task<PainelDeMetasDto> PainelAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("Viagem sintética 4471");
    }

    private async Task<(Contexto C, LogsCapturados Logs)> IniciarComAsync(Action<IServiceCollection>? extra = null)
    {
        var banco = await pg.NovoBancoAsync();
        var fixo = new CasalFixo();
        var logs = new LogsCapturados();
        var api = new ApiDeTeste(banco, relogio: new RelogioFixo(), servicos: s =>
        {
            s.AddSingleton(fixo);
            s.AddScoped<IUsuarioAtual, UsuarioDoTeste>();
            s.AddSingleton<ILoggerProvider>(logs);
            extra?.Invoke(s);
        });
        var pedido = new CadastroDoCasalDto(
            api.CodigoInstalacao, "Família Teste",
            new ConjugeNovoDto("Ana Teste", CpfSintetico.Gerar(1), "senha-da-ana-123"),
            new ConjugeNovoDto("Bruno Teste", CpfSintetico.Gerar(2), "senha-do-bruno-1"));
        Assert.Equal(HttpStatusCode.Created, (await api.CreateClient().PostAsJsonAsync("/api/v1/casal", pedido)).StatusCode);
        var casal = await EscalarAsync<Guid>(banco, "SELECT casal_id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'");
        fixo.Id = casal;
        var ana = await EscalarAsync<Guid>(banco, "SELECT id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'");
        return (new Contexto(api, banco, await api.ClienteLogadoAsync(ana), casal), logs);
    }

    private static async Task LancarAsync(HttpClient cliente, Guid conta, string categoria, string natureza, long valor, string data, int parcelas = 1)
    {
        var resposta = await cliente.PostAsJsonAsync("/api/v1/lancamentos", new
        {
            chaveIdempotente = Guid.NewGuid(), contaId = conta, natureza, valorCentavos = valor, data,
            descricao = "Teste", contraparte = (string?)null, categoriaId = await CategoriaAsync(cliente, categoria), parcelas,
        });
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
    }

    private static async Task TetoAsync(HttpClient cliente, string categoria, long valor) =>
        Assert.Equal(HttpStatusCode.NoContent, (await cliente.PutAsJsonAsync(
            $"/api/v1/orcamento/tetos/2026-11/{await CategoriaAsync(cliente, categoria)}", new { valorCentavos = valor })).StatusCode);

    private static async Task<Guid> MetaAsync(HttpClient cliente, string nome, long alvo, long aporte, Guid conta)
    {
        var r = await cliente.PostAsJsonAsync("/api/v1/metas", new
        {
            nome, valorAlvoCentavos = alvo, aportePlanejadoCentavos = aporte, rendimentoMensalPercentual = 0m, contaIds = new[] { conta },
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await JsonAsync(r)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> ObterAsync(HttpClient cliente)
    {
        var r = await cliente.GetAsync("/api/v1/visao-geral");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await JsonAsync(r);
    }

    private static JsonElement Dados(JsonElement v, string secao)
    {
        var s = v.GetProperty(secao);
        Assert.Equal(JsonValueKind.Null, s.GetProperty("erro").ValueKind);
        return s.GetProperty("dados");
    }

    [Fact]
    public async Task Visao_geral_de_novembro_confere_numero_a_numero()
    {
        var (c, _) = await IniciarComAsync();
        await using var _c = c;
        var h = c.Cliente;
        var conjunta = await CriarContaAsync(h, new { tipo = "CORRENTE", apelido = "Conjunta Itaú", saldoInicialCentavos = 1_000_000L, saldoInicialEm = "2026-09-01" });
        await CriarContaAsync(h, new { tipo = "DINHEIRO", apelido = "Carteira", saldoInicialCentavos = 20_000L, saldoInicialEm = "2026-09-01" });
        var poupanca = await CriarContaAsync(h, new { tipo = "POUPANCA", apelido = "Poupança Casa", saldoInicialCentavos = 5_000_000L, saldoInicialEm = "2026-10-15" });
        var xp = await CriarContaAsync(h, new { tipo = "INVESTIMENTO", apelido = "XP Carro", saldoInicialCentavos = 3_000_000L, saldoInicialEm = "2026-09-01" });
        var visa = await CriarCartaoAsync(h);

        await TetoAsync(h, "Mercado", 160_000);
        await TetoAsync(h, "Restaurantes", 60_000);
        await TetoAsync(h, "Lazer", 50_000);

        await LancarAsync(h, visa, "Restaurantes", "DESPESA", 5_000, "2026-09-20");
        await LancarAsync(h, visa, "Mercado", "DESPESA", 100_000, "2026-10-15", parcelas: 10);
        await LancarAsync(h, conjunta, "Salário", "RECEITA", 900_000, "2026-10-25");
        await LancarAsync(h, conjunta, "Mercado", "DESPESA", 31_200, "2026-11-01");
        await LancarAsync(h, visa, "Restaurantes", "DESPESA", 18_800, "2026-11-03");
        await PagarAsync(h, conjunta, poupanca, 500_000, "2026-11-04");

        var casa = await MetaAsync(h, "Casa", 30_000_000, 500_000, poupanca);
        var carro = await MetaAsync(h, "Carro", 8_000_000, 180_000, xp);

        var v = await ObterAsync(h);

        Assert.Equal("2026-11", v.GetProperty("mes").GetString());
        Assert.Equal("2026-11-05", v.GetProperty("hoje").GetString());
        Assert.Equal(5, v.GetProperty("diaDoMes").GetInt32());
        Assert.Equal(30, v.GetProperty("diasNoMes").GetInt32());
        Assert.Equal("Ana", v.GetProperty("apelido").GetString());

        var gastar = Dados(v, "aindaDaParaGastar");
        Assert.Equal("COM_TETO", gastar.GetProperty("situacao").GetString());
        Assert.Equal(270_000, gastar.GetProperty("tetoCentavos").GetInt64());
        Assert.Equal(60_000, gastar.GetProperty("usadoCentavos").GetInt64());
        Assert.Equal(210_000, gastar.GetProperty("restanteCentavos").GetInt64());
        Assert.Equal(8_000, gastar.GetProperty("porDiaCentavos").GetInt64());
        Assert.Equal(22, gastar.GetProperty("percentualUsado").GetInt32());
        Assert.Equal("2026-11-30", gastar.GetProperty("fimDoMes").GetString());

        var saldo = Dados(v, "saldoDasContas");
        Assert.Equal(1_388_800, saldo.GetProperty("totalCentavos").GetInt64());
        Assert.Equal(0, saldo.GetProperty("outrasContas").GetInt32());
        var contas = saldo.GetProperty("contas").EnumerateArray().ToList();
        Assert.Equal(["Conjunta Itaú", "Carteira"], contas.Select(x => x.GetProperty("apelido").GetString()));
        Assert.Equal([1_368_800L, 20_000L], contas.Select(x => x.GetProperty("saldoCentavos").GetInt64()));

        var aporte = Dados(v, "aportePlanejado");
        Assert.Equal(680_000, aporte.GetProperty("totalCentavos").GetInt64());
        Assert.Equal(500_000, aporte.GetProperty("jaAportadoCentavos").GetInt64());
        var aportes = aporte.GetProperty("metas").EnumerateArray().ToDictionary(m => m.GetProperty("nome").GetString()!, m => m.GetProperty("aportePlanejadoCentavos").GetInt64());
        Assert.Equal(500_000, aportes["Casa"]);
        Assert.Equal(180_000, aportes["Carro"]);

        var pat = Dados(v, "patrimonio");
        Assert.Equal(9_855_000, pat.GetProperty("totalCentavos").GetInt64());
        Assert.Equal(-50_000, pat.GetProperty("variacaoCentavos").GetInt64());
        Assert.Equal(-0.5m, pat.GetProperty("variacaoPercentual").GetDecimal());

        var avisos = Dados(v, "avisosDeFatura").EnumerateArray().ToList();
        var aviso = Assert.Single(avisos);
        Assert.Equal("Compras no cartão Itaú Visa a partir de hoje só vencem em 10/12 — a fatura fechou em 01/11", aviso.GetProperty("texto").GetString());

        var p12 = Dados(v, "patrimonioEm12Meses");
        Assert.Equal("dez/2025 → nov/2026", p12.GetProperty("intervalo").GetString());
        var pontos = p12.GetProperty("pontos").EnumerateArray().ToList();
        Assert.Equal(12, pontos.Count);
        Assert.All(pontos.Take(9), p => Assert.Equal(JsonValueKind.Null, p.GetProperty("totalCentavos").ValueKind));
        Assert.Equal(["2025-12", "2026-01", "2026-02", "2026-03", "2026-04", "2026-05", "2026-06", "2026-07", "2026-08", "2026-09", "2026-10", "2026-11"],
            pontos.Select(p => p.GetProperty("mes").GetString()));
        Assert.Equal(4_015_000, pontos[9].GetProperty("totalCentavos").GetInt64());
        Assert.Equal(9_905_000, pontos[10].GetProperty("totalCentavos").GetInt64());
        Assert.Equal(9_855_000, pontos[11].GetProperty("totalCentavos").GetInt64());
        Assert.Equal("nov", pontos[11].GetProperty("rotulo").GetString());

        var metas = Dados(v, "metas");
        Assert.Equal(2, metas.GetProperty("total").GetInt32());
        var doPainel = await JsonAsync(await h.GetAsync("/api/v1/metas"));
        foreach (var (id, progresso, guardado, alvo) in new[] { (casa, 18, 5_500_000L, 30_000_000L), (carro, 37, 3_000_000L, 8_000_000L) })
        {
            var m = metas.GetProperty("metas").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == id);
            var get = doPainel.GetProperty("metas").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == id);
            Assert.Equal(progresso, m.GetProperty("progressoPercentual").GetInt32());
            Assert.Equal(guardado, m.GetProperty("guardadoCentavos").GetInt64());
            Assert.Equal(alvo, m.GetProperty("valorAlvoCentavos").GetInt64());
            Assert.Equal(get.GetProperty("projecao").GetProperty("situacao").GetString(), m.GetProperty("situacao").GetString());
            Assert.Equal(get.GetProperty("projecao").GetProperty("chegaEm").GetString(), m.GetProperty("chegaEm").GetString());
        }

        var orc = Dados(v, "orcamento");
        Assert.Equal(0, orc.GetProperty("outrasCategorias").GetInt32());
        var cats = orc.GetProperty("categorias").EnumerateArray().ToList();
        Assert.Equal(["Restaurantes", "Mercado", "Lazer"], cats.Select(x => x.GetProperty("nome").GetString()));
        Assert.Equal([18_800L, 41_200L, 0L], cats.Select(x => x.GetProperty("usadoCentavos").GetInt64()));
        Assert.Equal([60_000L, 160_000L, 50_000L], cats.Select(x => x.GetProperty("tetoCentavos").GetInt64()));
        Assert.Equal([31, 25, 0], cats.Select(x => x.GetProperty("percentualUsado").GetInt32()));
        Assert.All(cats, x => Assert.Equal("NORMAL", x.GetProperty("estado").GetString()));

        var venc = Dados(v, "vencimentos").EnumerateArray().ToList();
        Assert.Equal(2, venc.Count);
        Assert.Equal("2026-11-10", venc[0].GetProperty("vencimento").GetString());
        Assert.Equal("COM_ATRASO", venc[0].GetProperty("estado").GetString());
        Assert.Equal(15_000, venc[0].GetProperty("valorCentavos").GetInt64());
        Assert.Equal(5_000, venc[0].GetProperty("atrasoCentavos").GetInt64());
        Assert.Equal("2026-11-01", venc[0].GetProperty("fechamento").GetString());
        Assert.Equal("Itaú Visa", venc[0].GetProperty("apelido").GetString());
        Assert.Equal("2026-12-10", venc[1].GetProperty("vencimento").GetString());
        Assert.Equal("ABERTA", venc[1].GetProperty("estado").GetString());
        Assert.Equal(18_800, venc[1].GetProperty("valorCentavos").GetInt64());
        Assert.Equal("2026-12-01", venc[1].GetProperty("fechamento").GetString());

        var ultimos = Dados(v, "ultimosLancamentos").EnumerateArray().ToList();
        Assert.Equal(["2026-11-04", "2026-11-04", "2026-11-03", "2026-11-01", "2026-10-25"], ultimos.Select(x => x.GetProperty("data").GetString()));
        Assert.Equal([-500_000L, 500_000L], ultimos.Take(2).Select(x => x.GetProperty("valorCentavos").GetInt64()).Order());
        Assert.Equal([-18_800L, -31_200L, 900_000L], ultimos.Skip(2).Select(x => x.GetProperty("valorCentavos").GetInt64()));
    }

    [Fact]
    public async Task Casal_sem_nada_devolve_as_secoes_vazias()
    {
        var (c, _) = await IniciarComAsync();
        await using var _c = c;

        var v = await ObterAsync(c.Cliente);

        foreach (var s in new[] { "aindaDaParaGastar", "saldoDasContas", "aportePlanejado", "patrimonio", "avisosDeFatura",
                     "patrimonioEm12Meses", "metas", "orcamento", "vencimentos", "ultimosLancamentos" })
            Assert.Equal(JsonValueKind.Null, v.GetProperty(s).GetProperty("erro").ValueKind);

        Assert.Equal("SEM_TETO", Dados(v, "aindaDaParaGastar").GetProperty("situacao").GetString());
        Assert.Equal(0, Dados(v, "saldoDasContas").GetProperty("totalCentavos").GetInt64());
        Assert.Equal(0, Dados(v, "aportePlanejado").GetProperty("totalCentavos").GetInt64());
        Assert.Equal(0, Dados(v, "patrimonio").GetProperty("totalCentavos").GetInt64());
        var pontos = Dados(v, "patrimonioEm12Meses").GetProperty("pontos").EnumerateArray().ToList();
        Assert.Equal(12, pontos.Count);
        Assert.All(pontos, p => Assert.Equal(JsonValueKind.Null, p.GetProperty("totalCentavos").ValueKind));
        Assert.Empty(Dados(v, "avisosDeFatura").EnumerateArray());
        Assert.Empty(Dados(v, "metas").GetProperty("metas").EnumerateArray());
        Assert.Empty(Dados(v, "orcamento").GetProperty("categorias").EnumerateArray());
        Assert.Empty(Dados(v, "vencimentos").EnumerateArray());
        Assert.Empty(Dados(v, "ultimosLancamentos").EnumerateArray());
    }

    [Fact]
    public async Task Secao_com_falha_devolve_erro_so_nela_e_loga_tipo_sem_valores()
    {
        var (c, logs) = await IniciarComAsync(s =>
        {
            s.RemoveAll<IConsultaDeMetas>();
            s.AddScoped<IConsultaDeMetas, MetasQueFalha>();
        });
        await using var _c = c;

        var resposta = await c.Cliente.GetAsync("/api/v1/visao-geral");
        var v = await JsonAsync(resposta);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal(Erro, v.GetProperty("metas").GetProperty("erro").GetString());
        Assert.Equal(Erro, v.GetProperty("aportePlanejado").GetProperty("erro").GetString());
        foreach (var s in new[] { "aindaDaParaGastar", "saldoDasContas", "patrimonio", "avisosDeFatura", "patrimonioEm12Meses", "orcamento", "vencimentos", "ultimosLancamentos" })
            Assert.NotEqual(JsonValueKind.Null, v.GetProperty(s).GetProperty("dados").ValueKind);

        var texto = string.Join('\n', logs.Linhas);
        Assert.Contains("Metas", texto);
        Assert.Contains("InvalidOperationException", texto);
        Assert.Contains(await IdDeCorrelacaoAsync(texto), texto);
        Assert.DoesNotContain("Viagem sintética 4471", texto);
        Assert.DoesNotContain("Viagem sintética 4471", await resposta.Content.ReadAsStringAsync());
    }

    // O TraceIdentifier é gerado pelo servidor; o log o carrega ao fim da linha da seção.
    private static Task<string> IdDeCorrelacaoAsync(string texto)
    {
        var linha = texto.Split('\n').First(l => l.Contains("Metas") && l.Contains("falhou"));
        const string marca = "correlação ";
        return Task.FromResult(linha[(linha.IndexOf(marca, StringComparison.Ordinal) + marca.Length)..].Split(' ', '|')[0].Trim());
    }

    [Fact]
    public async Task Sem_sessao_responde_401()
    {
        var (c, _) = await IniciarComAsync();
        await using var _c = c;

        var r = await c.Api.CreateClient().GetAsync("/api/v1/visao-geral");

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Resposta_tem_no_store()
    {
        var (c, _) = await IniciarComAsync();
        await using var _c = c;

        var r = await c.Cliente.GetAsync("/api/v1/visao-geral");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("no-store", r.Headers.CacheControl!.ToString());
    }
}
