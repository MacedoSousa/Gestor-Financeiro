using System.Diagnostics;
using GestorFinanceiro.Modules.Lancamentos.Aplicacao;
using GestorFinanceiro.Modules.Lancamentos.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace GestorFinanceiro.Modules.Lancamentos.Api;

/// <summary>Categorias do casal. Exigem sessão (política padrão do grupo /api/v1).</summary>
internal static class EndpointsDeCategorias
{
    public static IEndpointRouteBuilder MapEndpointsDeCategorias(this IEndpointRouteBuilder api)
    {
        var grupo = api.MapGroup("/categorias").WithTags("Lancamentos");

        grupo.MapGet("/", async Task<Results<Ok<IReadOnlyList<CategoriaDto>>, NotFound>> (ServicoDeCategorias servico, HttpResponse resposta, CancellationToken ct) =>
        {
            resposta.Headers.CacheControl = "no-store";
            return await servico.ListarAsync(ct) is { } lista ? TypedResults.Ok(lista) : TypedResults.NotFound();
        })
        .WithName("ListarCategorias")
        .WithSummary("Categorias do casal, inclusive as arquivadas. Na primeira consulta cria as padrão.");

        grupo.MapPost("/", async Task<Results<Created, ValidationProblem, ProblemHttpResult, NotFound>> (
            NovaCategoriaDto pedido, ServicoDeCategorias servico, CancellationToken ct) =>
            Traduzir(await servico.CriarAsync(pedido, ct), id => TypedResults.Created($"/api/v1/categorias/{id}")))
        .WithName("CriarCategoria")
        .WithSummary("Cria uma categoria. Nome repetido no casal (sem diferenciar maiúsculas) dá 409.")
        .ProducesProblem(StatusCodes.Status409Conflict);

        grupo.MapPut("/{id:guid}", async Task<Results<NoContent, ValidationProblem, ProblemHttpResult, NotFound>> (
            Guid id, NovaCategoriaDto pedido, ServicoDeCategorias servico, CancellationToken ct) =>
            Traduzir(await servico.AtualizarAsync(id, pedido, ct), _ => TypedResults.NoContent()))
        .WithName("AtualizarCategoria")
        .WithSummary("Renomeia ou arquiva uma categoria. Arquivar não mexe nos lançamentos que já a usam.")
        .ProducesProblem(StatusCodes.Status409Conflict);

        grupo.MapDelete("/{id:guid}", async Task<Results<NoContent, ValidationProblem, ProblemHttpResult, NotFound>> (
            Guid id, ServicoDeCategorias servico, CancellationToken ct) =>
            Traduzir(await servico.ApagarAsync(id, ct), _ => TypedResults.NoContent()))
        .WithName("ApagarCategoria")
        .WithSummary("Apaga (soft delete) uma categoria sem lançamentos. Com lançamentos dá 409: arquive em vez de apagar.")
        .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    private static Results<T, ValidationProblem, ProblemHttpResult, NotFound> Traduzir<T>(ResultadoDeCategoria resultado, Func<Guid, T> sucesso)
        where T : IResult =>
        resultado switch
        {
            ResultadoDeCategoria.Ok ok => sucesso(ok.Id),
            ResultadoDeCategoria.Invalido invalido => TypedResults.ValidationProblem(invalido.Erros.ToDictionary()),
            ResultadoDeCategoria.NaoEncontrada => TypedResults.NotFound(),
            ResultadoDeCategoria.NomeRepetido => TypedResults.Problem(title: ServicoDeCategorias.TituloNomeRepetido, statusCode: StatusCodes.Status409Conflict),
            ResultadoDeCategoria.EmUso => TypedResults.Problem(title: ServicoDeCategorias.TituloEmUso, statusCode: StatusCodes.Status409Conflict),
            _ => throw new UnreachableException(resultado.GetType().Name),
        };
}
