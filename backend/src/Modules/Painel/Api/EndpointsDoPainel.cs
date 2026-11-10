using GestorFinanceiro.Modules.Painel.Aplicacao;
using GestorFinanceiro.Modules.Painel.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace GestorFinanceiro.Modules.Painel.Api;

/// <summary>Visão geral do casal. Exige sessão (política padrão do grupo /api/v1); o casal vem da sessão.</summary>
internal static class EndpointsDoPainel
{
    public static IEndpointRouteBuilder MapEndpointsDoPainel(this IEndpointRouteBuilder api)
    {
        api.MapGet("/visao-geral", async Task<Results<Ok<VisaoGeralDto>, NotFound>> (
            MontagemDoPainel montagem, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return await montagem.MontarAsync(http.TraceIdentifier, ct) is { } dto
                ? TypedResults.Ok(dto)
                : TypedResults.NotFound();
        })
        .WithTags("Painel")
        .WithName("ObterVisaoGeral")
        .WithSummary("Visão geral do mês corrente (Brasília). Cada seção vem com dados ou com erro, independentes entre si.");

        return api;
    }
}
