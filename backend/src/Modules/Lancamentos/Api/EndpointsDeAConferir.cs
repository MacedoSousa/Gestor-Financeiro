using System.Diagnostics;
using GestorFinanceiro.Modules.Lancamentos.Aplicacao;
using GestorFinanceiro.Modules.Lancamentos.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace GestorFinanceiro.Modules.Lancamentos.Api;

/// <summary>A caixa "A conferir" (Fase 2, D11, Tarefa 6.1). Exige sessão (política do grupo /api/v1); só o casal da sessão.</summary>
internal static class EndpointsDeAConferir
{
    public static IEndpointRouteBuilder MapEndpointsDeAConferir(this IEndpointRouteBuilder api)
    {
        var grupo = api.MapGroup("/a-conferir").WithTags("Lancamentos");

        grupo.MapGet("/", async Task<Results<Ok<IReadOnlyList<ItemAConferirDto>>, NotFound>> (AConferir caixa, CancellationToken ct) =>
            await caixa.ListarAsync(ct) is { } itens ? TypedResults.Ok(itens) : TypedResults.NotFound())
        .WithName("ListarAConferir")
        .WithSummary("Itens abertos de \"A conferir\", com as opções de cada um. Nada é descartado: resolvido sai da lista e fica guardado.");

        grupo.MapPost("/{id:guid}/resolucao", async Task<Results<NoContent, ValidationProblem, ProblemHttpResult, NotFound>> (
            Guid id, ResolucaoDto pedido, AConferir caixa, CancellationToken ct) =>
            await caixa.ResolverAsync(id, pedido, ct) switch
            {
                ResultadoDeLancamento.Feito => TypedResults.NoContent(),
                ResultadoDeLancamento.Invalido i => TypedResults.ValidationProblem(i.Erros.ToDictionary()),
                ResultadoDeLancamento.Recusado r => TypedResults.Problem(title: r.Titulo, statusCode: r.Status),
                ResultadoDeLancamento.NaoEncontrado => TypedResults.NotFound(),
                var outro => throw new UnreachableException(outro.GetType().Name),
            })
        .WithName("ResolverAConferir")
        .WithSummary("Resolve um item: TRANSFERENCIA, APORTE ou RESGATE (com contaId), GASTO, RECEITA ou IR_IOF_TAXA (com categoriaId), " +
                     "PAREAR_MOVIMENTO (com movimentoId), RENDIMENTO, AJUSTAR_SALDO_INICIAL, CIENTE. DEIXAR_PARA_DEPOIS mantém aberto. " +
                     "Repetir a mesma opção num item resolvido não faz nada; outra opção dá 409. Diferença do grupo que mudou desde a tela dá 409. " +
                     "Opção que não é do item dá 400.")
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }
}
