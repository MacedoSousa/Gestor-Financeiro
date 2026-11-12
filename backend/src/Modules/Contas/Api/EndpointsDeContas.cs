using System.Diagnostics;
using GestorFinanceiro.Modules.Contas.Aplicacao;
using GestorFinanceiro.Modules.Contas.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace GestorFinanceiro.Modules.Contas.Api;

/// <summary>Contas e cartões do casal. Exigem sessão (política padrão do grupo /api/v1).</summary>
internal static class EndpointsDeContas
{
    public static IEndpointRouteBuilder MapEndpointsDeContas(this IEndpointRouteBuilder api)
    {
        var grupo = api.MapGroup("/contas").WithTags("Contas");

        grupo.MapGet("/", async Task<Ok<IReadOnlyList<ContaDto>>> (ServicoDeContas servico, HttpResponse resposta, CancellationToken ct) =>
        {
            resposta.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await servico.ListarAsync(ct));
        })
        .WithName("ListarContas")
        .WithSummary("Contas e cartões do casal, inclusive arquivados. Agência e número só mascarados.");

        grupo.MapPost("/", async Task<Results<Created, ValidationProblem, ProblemHttpResult, NotFound>> (
            NovaContaDto pedido, ServicoDeContas servico, CancellationToken ct) =>
            Traduzir(await servico.CriarAsync(pedido, ct), id => TypedResults.Created($"/api/v1/contas/{id}")))
        .WithName("CriarConta")
        .WithSummary("Cria conta ou cartão. Apelido repetido no casal dá 409; titular fora do casal ou cartão sem dias dá 400.")
        .ProducesProblem(StatusCodes.Status409Conflict);

        grupo.MapPut("/{id:guid}", async Task<Results<NoContent, ValidationProblem, ProblemHttpResult, NotFound>> (
            Guid id, NovaContaDto pedido, ServicoDeContas servico, CancellationToken ct) =>
            Traduzir(await servico.AtualizarAsync(id, pedido, ct), _ => TypedResults.NoContent()))
        .WithName("AtualizarConta")
        .WithSummary("Atualiza (e arquiva) uma conta. Agência e número nulos mantêm o que está guardado; texto vazio apaga. " +
                     "Em conta ligada ao banco, mudar o saldo inicial dá 409.")
        .ProducesProblem(StatusCodes.Status409Conflict);

        grupo.MapDelete("/{id:guid}", async Task<Results<NoContent, ValidationProblem, ProblemHttpResult, NotFound>> (
            Guid id, ServicoDeContas servico, CancellationToken ct) =>
            Traduzir(await servico.ApagarAsync(id, ct), _ => TypedResults.NoContent()))
        .WithName("ApagarConta")
        .WithSummary("Apaga (soft delete) uma conta sem lançamentos. Com lançamentos, ou ligada ao banco, dá 409: arquive em vez de apagar.")
        .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    private static Results<T, ValidationProblem, ProblemHttpResult, NotFound> Traduzir<T>(ResultadoDeConta resultado, Func<Guid, T> sucesso)
        where T : IResult =>
        resultado switch
        {
            ResultadoDeConta.Ok ok => sucesso(ok.Id),
            ResultadoDeConta.Invalido invalido => TypedResults.ValidationProblem(invalido.Erros.ToDictionary()),
            ResultadoDeConta.NaoEncontrada => TypedResults.NotFound(),
            ResultadoDeConta.ApelidoRepetido => TypedResults.Problem(title: ServicoDeContas.TituloApelidoRepetido, statusCode: StatusCodes.Status409Conflict),
            ResultadoDeConta.EmUso => TypedResults.Problem(title: ServicoDeContas.TituloEmUso, statusCode: StatusCodes.Status409Conflict),
            ResultadoDeConta.SaldoDoBanco => TypedResults.Problem(title: ServicoDeContas.TituloSaldoDoBanco, statusCode: StatusCodes.Status409Conflict),
            ResultadoDeConta.Ligada => TypedResults.Problem(title: ServicoDeContas.TituloLigada, statusCode: StatusCodes.Status409Conflict),
            _ => throw new UnreachableException(resultado.GetType().Name),
        };
}
