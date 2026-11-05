using System.Net;
using GestorFinanceiro.IntegrationTests.Infra;

namespace GestorFinanceiro.IntegrationTests;

[Collection("postgres")]
public class ApiDeTesteTests(PostgresFixture pg)
{
    [Fact]
    public async Task Api_sobe_e_responde_saude()
    {
        await using var api = new ApiDeTeste(await pg.NovoBancoAsync());
        var resposta = await api.CreateClient().GetAsync("/api/v1/saude");
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.True(File.Exists(api.CaminhoCodigoInstalacao));
        Assert.NotEmpty(api.CodigoInstalacao);
    }
}
