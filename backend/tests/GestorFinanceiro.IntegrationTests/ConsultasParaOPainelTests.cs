using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using GestorFinanceiro.IntegrationTests.Infra;
using GestorFinanceiro.Modules.Investimentos.Contracts;
using GestorFinanceiro.Modules.Orcamento.Contracts;
using Microsoft.Extensions.DependencyInjection;
using static GestorFinanceiro.IntegrationTests.PatrimonioTests;

namespace GestorFinanceiro.IntegrationTests;

/// <summary>Consultas de Orçamento e Investimentos para outros módulos: devolvem o mesmo que o GET. Relógio: 05/11/2026.</summary>
[Collection("postgres")]
public class ConsultasParaOPainelTests(PostgresFixture pg)
{
    private static readonly JsonSerializerOptions Opcoes = new(JsonSerializerDefaults.Web);

    private static JsonNode Serializar<T>(T valor) =>
        JsonNode.Parse(JsonSerializer.Serialize(valor, Opcoes))!;

    private static async Task<JsonNode> CorpoAsync(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
    }

    [Fact]
    public async Task Consulta_do_orcamento_devolve_o_mesmo_que_o_GET()
    {
        await using var c = await IniciarAsync(pg);
        var conta = await CriarCorrenteAsync(c.Cliente);
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        Assert.Equal(HttpStatusCode.NoContent,
            (await c.Cliente.PutAsJsonAsync($"/api/v1/orcamento/tetos/2026-11/{mercado}", new { valorCentavos = 160_000 })).StatusCode);
        await LancarAsync(c.Cliente, conta, 31_200, "2026-11-01");

        var get = await CorpoAsync(await c.Cliente.GetAsync("/api/v1/orcamento?mes=2026-11"));
        await using var escopo = c.Api.Services.CreateAsyncScope();
        var dto = await escopo.ServiceProvider.GetRequiredService<IConsultaDoOrcamento>().DoMesAsync(new DateOnly(2026, 11, 1));

        Assert.True(JsonNode.DeepEquals(get, Serializar(dto)));
        Assert.Contains(dto.Categorias, x => x.CategoriaId == mercado && x.TetoCentavos == 160_000 && x.UsadoCentavos == 31_200);
    }

    [Fact]
    public async Task Consulta_de_metas_devolve_o_mesmo_que_o_GET()
    {
        await using var c = await IniciarAsync(pg);
        var poupanca = await CriarContaAsync(c.Cliente, new
        {
            tipo = "POUPANCA", apelido = "Poupança Casa", saldoInicialCentavos = 5_000_000L, saldoInicialEm = "2026-10-15",
        });
        var criada = await c.Cliente.PostAsJsonAsync("/api/v1/metas", new
        {
            nome = "Casa", valorAlvoCentavos = 30_000_000L, aportePlanejadoCentavos = 500_000L,
            rendimentoMensalPercentual = 0m, contaIds = new[] { poupanca },
        });
        Assert.Equal(HttpStatusCode.Created, criada.StatusCode);

        var get = await CorpoAsync(await c.Cliente.GetAsync("/api/v1/metas"));
        await using var escopo = c.Api.Services.CreateAsyncScope();
        var dto = await escopo.ServiceProvider.GetRequiredService<IConsultaDeMetas>().PainelAsync();

        Assert.True(JsonNode.DeepEquals(get, Serializar(dto)));
        Assert.Single(dto.Metas);
    }

    [Fact]
    public async Task Sem_sessao_nem_casal_a_consulta_falha_sem_dado_no_texto()
    {
        await using var c = await IniciarAsync(pg);
        await using var escopo = c.Api.Services.CreateAsyncScope();
        var fixo = c.Api.Services.GetRequiredService<CasalFixo>();
        fixo.Id = null;

        var e1 = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            escopo.ServiceProvider.GetRequiredService<IConsultaDoOrcamento>().DoMesAsync(new DateOnly(2026, 11, 1)));
        var e2 = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            escopo.ServiceProvider.GetRequiredService<IConsultaDeMetas>().PainelAsync());

        Assert.Equal("Orçamento indisponível para a sessão atual.", e1.Message);
        Assert.Equal("Metas indisponíveis para a sessão atual.", e2.Message);
    }
}
