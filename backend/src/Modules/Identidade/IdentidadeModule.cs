using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.Modules.Identidade.Infra;
using GestorFinanceiro.SharedKernel.Aplicacao;
using GestorFinanceiro.SharedKernel.Persistencia;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
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

        return api;
    }
}
