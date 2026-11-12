using System.Diagnostics;
using GestorFinanceiro.Modules.Integracoes.Aplicacao;
using GestorFinanceiro.Modules.Integracoes.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace GestorFinanceiro.Modules.Integracoes.Api;

/// <summary>
/// Descoberta e nascimento das contas ligadas (Fase 2, etapa 3). Só o titular da conexão (D21); o casal vem da sessão.
/// Nenhuma resposta traz o itemId da Pluggy, agência, número de conta nem final de cartão.
/// </summary>
internal static class EndpointsDeNascimento
{
    public static IEndpointRouteBuilder MapEndpointsDeNascimento(this IEndpointRouteBuilder api)
    {
        var grupo = api.MapGroup("/integracoes").WithTags("Integrações");
        grupo.AddEndpointFilter(async (contexto, proximo) =>
        {
            contexto.HttpContext.Response.Headers.CacheControl = "no-store";
            return await proximo(contexto);
        });

        grupo.MapGet("/items/{id:guid}/descoberta", async Task<Results<Ok<DescobertaDto>, ProblemHttpResult, NotFound>> (
            Guid id, Descoberta descoberta, CancellationToken ct) =>
            await descoberta.ListarAsync(id, ct) switch
            {
                ResultadoDeNascimento.Descoberta d => TypedResults.Ok(d.Dto),
                ResultadoDeNascimento.Recusado r => Problema(r),
                ResultadoDeNascimento.NaoEncontrado => TypedResults.NotFound(),
                var outro => throw new UnreachableException(outro.GetType().Name),
            })
        .WithName("DescobrirContas")
        .WithSummary("O que o banco tem neste item e o sistema ainda não: contas, cartões (com dias e limite sugeridos), " +
                     "aplicações e as contas marcadas \"não importar\". Só lê. Só o titular (senão 403); banco recusou dá 502, " +
                     "Pluggy fora do ar dá 503.")
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status502BadGateway)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return api;
    }

    internal static ProblemHttpResult Problema(ResultadoDeNascimento.Recusado r) =>
        TypedResults.Problem(title: r.Titulo, statusCode: r.Status,
            extensions: r.Extras?.ToDictionary(e => e.Key, e => e.Value));
}
