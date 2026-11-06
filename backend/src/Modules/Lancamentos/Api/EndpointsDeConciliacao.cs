using System.Diagnostics;
using GestorFinanceiro.Modules.Lancamentos.Aplicacao;
using GestorFinanceiro.Modules.Lancamentos.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace GestorFinanceiro.Modules.Lancamentos.Api;

/// <summary>
/// Conciliação de saldo. A rota fica sob /contas, mas é do Lancamentos: lança receita. O módulo Contas mapeia o mesmo
/// prefixo sem rota de conciliação, então não há conflito.
/// </summary>
internal static class EndpointsDeConciliacao
{
    public static IEndpointRouteBuilder MapEndpointsDeConciliacao(this IEndpointRouteBuilder api)
    {
        api.MapPost("/contas/{id:guid}/conciliacao", async Task<Results<Ok<ConciliacaoDto>, ValidationProblem, ProblemHttpResult, NotFound>> (
            Guid id, ConciliacaoPedidoDto pedido, ServicoDeConciliacao servico, HttpResponse resposta, CancellationToken ct) =>
        {
            resposta.Headers.CacheControl = "no-store";
            return await servico.ConciliarAsync(id, pedido, ct) switch
            {
                ResultadoDaConciliacao.Feita f => TypedResults.Ok(f.Conciliacao),
                ResultadoDaConciliacao.Invalida i => TypedResults.ValidationProblem(i.Erros.ToDictionary()),
                ResultadoDaConciliacao.Recusada r => TypedResults.Problem(title: r.Titulo, statusCode: r.Status),
                ResultadoDaConciliacao.NaoEncontrada => TypedResults.NotFound(),
                var outro => throw new UnreachableException(outro.GetType().Name),
            };
        })
        .WithTags("Lancamentos")
        .WithName("ConciliarConta")
        .WithSummary("Concilia o saldo de hoje de uma poupança ou investimento com o do banco. Diferença positiva vira receita " +
                     "em Rendimentos (LANCADO); igual dá CONFERE e menor dá MENOR, sem lançar. Saldo exibido diferente do de hoje dá 409.")
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }
}
