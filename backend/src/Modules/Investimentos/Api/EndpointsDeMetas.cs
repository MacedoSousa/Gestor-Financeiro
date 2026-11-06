using System.Diagnostics;
using GestorFinanceiro.Modules.Investimentos.Aplicacao;
using GestorFinanceiro.Modules.Investimentos.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace GestorFinanceiro.Modules.Investimentos.Api;

/// <summary>Metas do casal. Exigem sessão (política padrão do grupo /api/v1); o casal vem da sessão.</summary>
internal static class EndpointsDeMetas
{
    public static IEndpointRouteBuilder MapEndpointsDeMetas(this IEndpointRouteBuilder api)
    {
        var grupo = api.MapGroup("/metas").WithTags("Metas");
        grupo.AddEndpointFilter(async (contexto, proximo) =>
        {
            contexto.HttpContext.Response.Headers.CacheControl = "no-store";
            return await proximo(contexto);
        });

        grupo.MapGet("/", async Task<Results<Ok<PainelDeMetasDto>, NotFound>> (ServicoDeMetas servico, CancellationToken ct) =>
            await servico.PainelAsync(ct) switch
            {
                ResultadoDeMetas.Painel p => TypedResults.Ok(p.Dto),
                ResultadoDeMetas.NaoEncontrado => TypedResults.NotFound(),
                var outro => throw new UnreachableException(outro.GetType().Name),
            })
        .WithName("ListarMetas")
        .WithSummary("Painel do mês corrente (Brasília): metas com guardado, aporte do mês e projeção, e todas as contas " +
                     "de poupança e investimento do casal (inclusive arquivadas) com a meta de cada uma.");

        grupo.MapPost("/", async Task<Results<Created<MetaDto>, ValidationProblem, ProblemHttpResult, NotFound>> (
            NovaMetaDto pedido, ServicoDeMetas servico, CancellationToken ct) =>
            await servico.CriarAsync(pedido, ct) switch
            {
                ResultadoDeMetas.Criada c => TypedResults.Created($"/api/v1/metas/{c.Meta.Id}", c.Meta),
                var outro => Erro(outro),
            })
        .WithName("CriarMeta")
        .WithSummary("Cria a meta com suas contas (poupança ou investimento, não arquivadas, fora de outra meta). " +
                     "Rendimento em % ao mês (0 a 5, até 4 casas); data desejada AAAA-MM a partir do mês atual. " +
                     "Conta inexistente dá 404; conta em outra meta ou nome repetido (sem caixa) dá 409.")
        .ProducesProblem(StatusCodes.Status409Conflict);

        grupo.MapPut("/{id:guid}", async Task<Results<NoContent, ValidationProblem, ProblemHttpResult, NotFound>> (
            Guid id, NovaMetaDto pedido, ServicoDeMetas servico, CancellationToken ct) =>
            Traduzir(await servico.EditarAsync(id, pedido, ct)))
        .WithName("EditarMeta")
        .WithSummary("Edita a meta; contaIds substitui o conjunto. Conta arquivada que já estava pode ficar; " +
                     "uma data desejada passada que não mudou é aceita.")
        .ProducesProblem(StatusCodes.Status409Conflict);

        grupo.MapDelete("/{id:guid}", async Task<Results<NoContent, ValidationProblem, ProblemHttpResult, NotFound>> (
            Guid id, ServicoDeMetas servico, CancellationToken ct) =>
            Traduzir(await servico.ApagarAsync(id, ct)))
        .WithName("ApagarMeta")
        .WithSummary("Apaga a meta e libera as contas (soft delete). O dinheiro não muda.");

        grupo.MapGet("/{id:guid}/simulacao", async Task<Results<Ok<ProjecaoDto>, ValidationProblem, NotFound>> (
            Guid id, long? aporte, ServicoDeMetas servico, CancellationToken ct) =>
            await servico.SimularAsync(id, aporte, ct) switch
            {
                ResultadoDeMetas.Simulacao s => TypedResults.Ok(s.Projecao),
                ResultadoDeMetas.Invalido i => TypedResults.ValidationProblem(i.Erros.ToDictionary()),
                ResultadoDeMetas.NaoEncontrado => TypedResults.NotFound(),
                var outro => throw new UnreachableException(outro.GetType().Name),
            })
        .WithName("SimularMeta")
        .WithSummary("Projeção da meta com o aporte mensal informado (0 a 100.000.000.000 centavos) no lugar do planejado. " +
                     "Não grava nada.");

        return api;
    }

    private static Results<NoContent, ValidationProblem, ProblemHttpResult, NotFound> Traduzir(ResultadoDeMetas resultado) =>
        resultado switch
        {
            ResultadoDeMetas.Feito => TypedResults.NoContent(),
            ResultadoDeMetas.Invalido i => TypedResults.ValidationProblem(i.Erros.ToDictionary()),
            ResultadoDeMetas.Recusado r => TypedResults.Problem(title: r.Titulo, statusCode: r.Status),
            ResultadoDeMetas.NaoEncontrado => TypedResults.NotFound(),
            _ => throw new UnreachableException(resultado.GetType().Name),
        };

    private static Results<Created<MetaDto>, ValidationProblem, ProblemHttpResult, NotFound> Erro(ResultadoDeMetas resultado) =>
        resultado switch
        {
            ResultadoDeMetas.Invalido i => TypedResults.ValidationProblem(i.Erros.ToDictionary()),
            ResultadoDeMetas.Recusado r => TypedResults.Problem(title: r.Titulo, statusCode: r.Status),
            ResultadoDeMetas.NaoEncontrado => TypedResults.NotFound(),
            _ => throw new UnreachableException(resultado.GetType().Name),
        };
}
