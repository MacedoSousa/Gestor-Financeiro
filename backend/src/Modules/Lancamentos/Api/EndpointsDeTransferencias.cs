using System.Diagnostics;
using GestorFinanceiro.Modules.Lancamentos.Aplicacao;
using GestorFinanceiro.Modules.Lancamentos.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace GestorFinanceiro.Modules.Lancamentos.Api;

/// <summary>Transferências entre contas. Exigem sessão (política padrão do grupo /api/v1); o casal vem da sessão.</summary>
internal static class EndpointsDeTransferencias
{
    public static IEndpointRouteBuilder MapEndpointsDeTransferencias(this IEndpointRouteBuilder api)
    {
        var grupo = api.MapGroup("/transferencias").WithTags("Lancamentos");

        grupo.MapPost("/", async Task<Results<Created<TransferenciaDto>, Ok<TransferenciaDto>, ValidationProblem, ProblemHttpResult, NotFound>> (
            NovaTransferenciaDto pedido, ServicoDeTransferencias servico, HttpResponse resposta, CancellationToken ct) =>
        {
            resposta.Headers.CacheControl = "no-store";
            return await servico.CriarAsync(pedido, ct) switch
            {
                ResultadoDeTransferencia.Criada c =>
                    TypedResults.Created($"/api/v1/lancamentos/{c.Transferencia.Saida.Id}", c.Transferencia),
                ResultadoDeTransferencia.Existente e => TypedResults.Ok(e.Transferencia),
                ResultadoDeTransferencia.Falhou { Motivo: ResultadoDeLancamento.Invalido i } => TypedResults.ValidationProblem(i.Erros.ToDictionary()),
                ResultadoDeTransferencia.Falhou { Motivo: ResultadoDeLancamento.NaoEncontrado } => TypedResults.NotFound(),
                ResultadoDeTransferencia.Falhou { Motivo: ResultadoDeLancamento.Recusado r } => TypedResults.Problem(title: r.Titulo, statusCode: r.Status),
                var outro => throw new UnreachableException(outro.GetType().Name),
            };
        })
        .WithName("CriarTransferencia")
        .WithSummary("Transfere entre duas contas do casal (pagar a fatura = destino no cartão). Grava as duas pernas juntas; " +
                     "repetir a mesma chave devolve 200 com a transferência já criada. Origem igual ao destino ou conta arquivada dá 400; conta ligada ao banco dá 409. " +
                     "Editar ou apagar uma perna, pelas rotas de lançamentos, age nas duas.")
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }
}
