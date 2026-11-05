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

namespace GestorFinanceiro.Modules.Identidade;

public static class IdentidadeModule
{
    public static IServiceCollection AddIdentidade(this IServiceCollection services, string conexao)
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
        services.AddSingleton<OpcoesDoWebAuthn>(sp =>
        {
            var opcoes = sp.GetRequiredService<IConfiguration>().GetSection(OpcoesDoWebAuthn.Secao).Get<OpcoesDoWebAuthn>() ?? new OpcoesDoWebAuthn();
            return opcoes.EhValida()
                ? opcoes
                : throw new InvalidOperationException($"Configure {OpcoesDoWebAuthn.Secao}:RpId e {OpcoesDoWebAuthn.Secao}:Origens.");
        });
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
