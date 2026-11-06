using System.Net;
using GestorFinanceiro.IntegrationTests.Infra;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;

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

    [Theory]
    [InlineData("Seguranca:WebAuthn:RpId", "")]
    [InlineData("Seguranca:WebAuthn:Origens:0", "nao-e-uma-url")]
    public async Task Api_nao_sobe_com_webauthn_mal_configurado(string chave, string valor)
    {
        await using var api = new ApiDeTeste(await pg.NovoBancoAsync());
        await using var errada = api.WithWebHostBuilder(b => b.UseSetting(chave, valor));

        var erro = Assert.Throws<OptionsValidationException>(() => errada.CreateClient());
        Assert.Contains("Seguranca:WebAuthn", erro.Message);
    }
}
