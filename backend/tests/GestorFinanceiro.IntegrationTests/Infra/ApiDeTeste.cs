using GestorFinanceiro.SharedKernel.Seguranca;
using GestorFinanceiro.UnitTests;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;

namespace GestorFinanceiro.IntegrationTests.Infra;

/// <summary>
/// A API inteira em memória, apontada para um banco de teste e com um código de instalação
/// gerado num arquivo temporário (com a idade pedida, para testar a expiração).
/// </summary>
public sealed class ApiDeTeste : WebApplicationFactory<Program>
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "gf-teste-" + Guid.NewGuid().ToString("N"));
    private readonly BancoDeTeste _banco;

    public string CodigoInstalacao { get; }
    public string CaminhoCodigoInstalacao { get; }

    public ApiDeTeste(BancoDeTeste banco, TimeSpan idadeDoCodigo = default)
    {
        _banco = banco;
        Directory.CreateDirectory(_pasta);
        CaminhoCodigoInstalacao = Path.Combine(_pasta, "codigo-instalacao");
        var relogio = new RelogioFixo { Agora = DateTimeOffset.UtcNow - idadeDoCodigo };
        CodigoInstalacao = CodigoDeInstalacaoEmArquivo.Gerar(CaminhoCodigoInstalacao, relogio);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:App", _banco.ConexaoApp);
        builder.UseSetting("Seguranca:ArquivoCodigoInstalacao", CaminhoCodigoInstalacao);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_pasta)) Directory.Delete(_pasta, recursive: true);
    }
}
