using System.Diagnostics;
using GestorFinanceiro.Modules.Lancamentos.Aplicacao;
using GestorFinanceiro.Modules.Lancamentos.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace GestorFinanceiro.Modules.Lancamentos.Api;

/// <summary>Lançamentos manuais e extrato. Exigem sessão (política padrão do grupo /api/v1); o casal vem da sessão.</summary>
internal static class EndpointsDeLancamentos
{
    public static IEndpointRouteBuilder MapEndpointsDeLancamentos(this IEndpointRouteBuilder api)
    {
        var grupo = api.MapGroup("/lancamentos").WithTags("Lancamentos");

        grupo.MapGet("/", async Task<Results<Ok<ExtratoDto>, ValidationProblem, NotFound>> (
            string? mes, Guid? contaId, string? titular, string? origem, string? q,
            ConsultaDoExtrato consulta, HttpResponse resposta, CancellationToken ct) =>
        {
            resposta.Headers.CacheControl = "no-store";
            var (extrato, erros) = await consulta.ObterAsync(new FiltroDoExtrato(mes, contaId, titular, origem, q), ct);
            if (erros is not null) return TypedResults.ValidationProblem(erros);
            return extrato is null ? TypedResults.NotFound() : TypedResults.Ok(extrato);
        })
        .WithName("ListarLancamentos")
        .WithSummary("Extrato do mês (AAAA-MM; padrão: o mês de hoje em Brasília), por data decrescente. " +
                     "titular: conjunta, eu ou conjuge. q busca na descrição e na contraparte, sem acento e sem caixa.");

        grupo.MapGet("/{id:guid}", async Task<Results<Ok<LancamentoDto>, NotFound>> (
            Guid id, ConsultaDoExtrato consulta, HttpResponse resposta, CancellationToken ct) =>
        {
            resposta.Headers.CacheControl = "no-store";
            return await consulta.ObterPorIdAsync(id, ct) is { } dto ? TypedResults.Ok(dto) : TypedResults.NotFound();
        })
        .WithName("ObterLancamento")
        .WithSummary("Um lançamento do casal.");

        grupo.MapPost("/", async Task<Results<Created<LancamentoDto>, Ok<LancamentoDto>, ValidationProblem, ProblemHttpResult, NotFound>> (
            NovoLancamentoDto pedido, ServicoDeLancamentos servico, HttpResponse resposta, CancellationToken ct) =>
        {
            resposta.Headers.CacheControl = "no-store";
            return await servico.CriarAsync(pedido, ct) switch
            {
                ResultadoDeLancamento.Criado c => TypedResults.Created($"/api/v1/lancamentos/{c.Lancamento.Id}", c.Lancamento),
                ResultadoDeLancamento.Existente e => TypedResults.Ok(e.Lancamento),
                ResultadoDeLancamento.Invalido i => TypedResults.ValidationProblem(i.Erros.ToDictionary()),
                ResultadoDeLancamento.NaoEncontrado => TypedResults.NotFound(),
                ResultadoDeLancamento.Recusado r => TypedResults.Problem(title: r.Titulo, statusCode: r.Status),
                var outro => throw new UnreachableException(outro.GetType().Name),
            };
        })
        .WithName("CriarLancamento")
        .WithSummary("Cria despesa ou receita (parcelada só em cartão). Repetir a mesma chave devolve 200 com o lançamento já criado. " +
                     "Conta ou categoria arquivada dá 400.")
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict);

        grupo.MapPut("/{id:guid}", async Task<Results<NoContent, ValidationProblem, ProblemHttpResult, NotFound>> (
            Guid id, string? escopo, EditarLancamentoDto pedido, ServicoDeLancamentos servico, CancellationToken ct) =>
            Traduzir(await servico.EditarAsync(id, escopo, pedido, ct)))
        .WithName("EditarLancamento")
        .WithSummary("Edita um lançamento. escopo: este (padrão) ou proximos (esta parcela e as seguintes). " +
                     "Em fatura fechada só descrição, contraparte e categoria mudam; mudar valor ou data ali, ou levar algo para fatura fechada, dá 409.")
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict);

        grupo.MapDelete("/{id:guid}", async Task<Results<NoContent, ValidationProblem, ProblemHttpResult, NotFound>> (
            Guid id, string? escopo, ServicoDeLancamentos servico, CancellationToken ct) =>
            Traduzir(await servico.ApagarAsync(id, escopo, ct)))
        .WithName("ApagarLancamento")
        .WithSummary("Apaga (soft delete). escopo: este (padrão) ou proximos (esta parcela e as seguintes, pulando faturas fechadas). " +
                     "Apagar lançamento de fatura fechada dá 409.")
        .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    private static Results<NoContent, ValidationProblem, ProblemHttpResult, NotFound> Traduzir(ResultadoDeLancamento resultado) =>
        resultado switch
        {
            ResultadoDeLancamento.Feito => TypedResults.NoContent(),
            ResultadoDeLancamento.Invalido invalido => TypedResults.ValidationProblem(invalido.Erros.ToDictionary()),
            ResultadoDeLancamento.NaoEncontrado => TypedResults.NotFound(),
            ResultadoDeLancamento.Recusado r => TypedResults.Problem(title: r.Titulo, statusCode: r.Status),
            _ => throw new UnreachableException(resultado.GetType().Name),
        };
}
