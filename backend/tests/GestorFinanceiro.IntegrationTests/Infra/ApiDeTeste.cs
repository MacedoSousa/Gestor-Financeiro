using GestorFinanceiro.Modules.Identidade.Aplicacao;
using GestorFinanceiro.Modules.Identidade.Dominio;
using GestorFinanceiro.Modules.Identidade.Infra;
using GestorFinanceiro.SharedKernel.Persistencia;
using GestorFinanceiro.SharedKernel.Seguranca;
using GestorFinanceiro.UnitTests;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GestorFinanceiro.IntegrationTests.Infra;

/// <summary>
/// A API inteira em memória, apontada para um banco de teste e com um código de instalação
/// gerado num arquivo temporário (com a idade pedida, para testar a expiração).
/// </summary>
internal sealed class ApiDeTeste : WebApplicationFactory<Program>
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "gf-teste-" + Guid.NewGuid().ToString("N"));
    private readonly BancoDeTeste _banco;
    private readonly RelogioFixo? _relogio;

    public string CodigoInstalacao { get; }
    public string CaminhoCodigoInstalacao { get; }

    public ApiDeTeste(BancoDeTeste banco, TimeSpan idadeDoCodigo = default, RelogioFixo? relogio = null)
    {
        _banco = banco;
        _relogio = relogio;
        // HTTPS na base: os cookies Secure e __Host- passam pelo CookieContainer.
        ClientOptions.BaseAddress = new Uri("https://localhost");
        Directory.CreateDirectory(_pasta);
        CaminhoCodigoInstalacao = Path.Combine(_pasta, "codigo-instalacao");
        var relogioDoCodigo = new RelogioFixo { Agora = DateTimeOffset.UtcNow - idadeDoCodigo };
        CodigoInstalacao = CodigoDeInstalacaoEmArquivo.Gerar(CaminhoCodigoInstalacao, relogioDoCodigo);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:App", _banco.ConexaoApp);
        builder.UseSetting("Seguranca:ArquivoCodigoInstalacao", CaminhoCodigoInstalacao);
        if (_relogio is not null)
            builder.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton<TimeProvider>(_relogio)));
    }

    protected override void ConfigureClient(HttpClient client) =>
        client.DefaultRequestHeaders.Add("X-Requested-With", "gestor-web");

    /// <summary>Cria a sessão pelo serviço interno e devolve um cliente com o cookie e o cabeçalho anti-CSRF.</summary>
    public async Task<HttpClient> ClienteLogadoAsync(Guid usuarioId, DateTimeOffset? confirmadaEm = null)
    {
        string token;
        await using (var escopo = Services.CreateAsyncScope())
        {
            var db = escopo.ServiceProvider.GetRequiredService<IdentidadeDbContext>();
            var usuario = await db.Usuarios.IgnoreQueryFilters([Filtros.Casal]).FirstAsync(u => u.Id == usuarioId);
            var sessoes = escopo.ServiceProvider.GetRequiredService<ServicoDeSessoes>();
            token = (await sessoes.CriarAsync(usuario, CancellationToken.None)).Token;
            if (confirmadaEm is { } quando)
            {
                var hmac = escopo.ServiceProvider.GetRequiredService<ICofre>().IndiceCego(token, "sessao");
                var sessao = await db.Sessoes.IgnoreQueryFilters([Filtros.Casal]).FirstAsync(x => x.TokenHmac == hmac);
                sessao.Confirmar(quando);
                await db.SaveChangesAsync();
            }
        }

        // Cookie à mão: o container do handler não leria o Set-Cookie de um serviço chamado fora do HTTP.
        var cliente = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = ClientOptions.BaseAddress });
        cliente.DefaultRequestHeaders.Add("Cookie", $"__Host-gf_sessao={token}");
        return cliente;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_pasta)) Directory.Delete(_pasta, recursive: true);
    }
}
