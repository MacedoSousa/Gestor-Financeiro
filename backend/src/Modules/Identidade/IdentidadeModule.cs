using GestorFinanceiro.Modules.Identidade.Api;
using GestorFinanceiro.Modules.Identidade.Aplicacao;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.Modules.Identidade.Infra;
using GestorFinanceiro.SharedKernel.Aplicacao;
using GestorFinanceiro.SharedKernel.Persistencia;
using Fido2NetLib;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GestorFinanceiro.Modules.Identidade;

public static class IdentidadeModule
{
    /// <param name="validarNaSubida">
    /// Confere a configuração WebAuthn na subida do host. O Host desliga só na geração do OpenAPI no build,
    /// que sobe o app sem configuração e não atende requisição nenhuma.
    /// </param>
    public static IServiceCollection AddIdentidade(this IServiceCollection services, string conexao, bool validarNaSubida = true)
    {
        services.AddDbContext<IdentidadeDbContext>((sp, o) => o
            .UseNpgsql(conexao, n => n.MigrationsHistoryTable("__ef_historico", "identidade"))
            .AddInterceptors(
                sp.GetRequiredService<SoftDeleteInterceptor>(),
                sp.GetRequiredService<ContextoAuditoriaInterceptor>()));

        services.AddScoped<IConsultaDeUsuarios, ConsultaDeUsuarios>();
        services.AddScoped<CadastroDoCasal>();
        services.AddScoped<ServicoDeSessoes>();
        services.AddMemoryCache();
        services.AddSingleton<AutorizacoesDeRegistro>();
        services.AddScoped<TentativaDeLogin>();
        services.AddScoped<PrimeiroAcesso>();
        services.AddScoped<ServicoDeTotp>();
        // Conferida na subida (ValidateOnStart): config errada derruba o boot em vez de dar 500 na primeira passkey.
        // Mesmo sem a conferência na subida, o primeiro uso continua validando (IOptions.Value).
        var webAuthn = services.AddOptions<OpcoesDoWebAuthn>()
            .BindConfiguration(OpcoesDoWebAuthn.Secao)
            .Validate(o => o.EhValida(), $"Configure {OpcoesDoWebAuthn.Secao}:RpId e {OpcoesDoWebAuthn.Secao}:Origens (URLs absolutas).");
        if (validarNaSubida) webAuthn.ValidateOnStart();
        services.AddSingleton<OpcoesDoWebAuthn>(sp => sp.GetRequiredService<IOptions<OpcoesDoWebAuthn>>().Value);
        services.AddSingleton<IFido2>(sp => new Fido2(sp.GetRequiredService<OpcoesDoWebAuthn>().ParaFido2(), metadataService: null));
        services.AddSingleton<DesafiosWebAuthn>();
        services.AddScoped<ServicoDePasskeys>();
        services.AddScoped<RotacaoDoCodigoDeRecuperacao>();
        services.AddScoped<RecuperacaoDeAcesso>();
        services.AddScoped<PedidosDeDispositivo>();
        services.AddScoped<ConfirmacaoDeIdentidade>();
        services.AddScoped<DestravarConjuge>();
        services.AddScoped<GestaoDeCredenciais>();
        services.AddScoped<NovoCodigoDeRecuperacao>();
        services.AddScoped<ISessoes>(sp => sp.GetRequiredService<ServicoDeSessoes>());
        return services;
    }

    public static IEndpointRouteBuilder MapIdentidade(this IEndpointRouteBuilder api)
    {
        var grupo = api.MapGroup("/casal").WithTags("Identidade");

        grupo.MapGet("/", async Task<Results<Ok<CasalDto>, NotFound>> (IConsultaDeUsuarios consulta, IUsuarioAtual atual, CancellationToken ct) =>
        {
            if (atual.CasalId is not { } casalId) return TypedResults.NotFound();
            var membros = await consulta.ListarDoCasalAsync(casalId, ct);
            return TypedResults.Ok(new CasalDto(
                casalId,
                membros.Select(m => new MembroDto(m.UsuarioId, m.Apelido, m.UsuarioId == atual.UsuarioId)).ToList()));
        })
        .WithName("ObterCasal")
        .WithSummary("Membros do casal do usuário logado (sem CPF).");

        grupo.MapCadastro();
        api.MapAutenticacao();
        api.MapTotp();
        api.MapPasskey();
        api.MapDispositivo();
        api.MapCredenciais();

        return api;
    }
}
