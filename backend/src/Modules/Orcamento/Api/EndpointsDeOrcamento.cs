using System.Diagnostics;
using GestorFinanceiro.Modules.Orcamento.Aplicacao;
using GestorFinanceiro.Modules.Orcamento.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace GestorFinanceiro.Modules.Orcamento.Api;

/// <summary>Orçamento do mês. Exigem sessão (política padrão do grupo /api/v1); o casal vem da sessão.</summary>
internal static class EndpointsDeOrcamento
{
    public static IEndpointRouteBuilder MapEndpointsDeOrcamento(this IEndpointRouteBuilder api)
    {
        var grupo = api.MapGroup("/orcamento").WithTags("Orcamento");
        grupo.AddEndpointFilter(async (contexto, proximo) =>
        {
            contexto.HttpContext.Response.Headers.CacheControl = "no-store";
            return await proximo(contexto);
        });

        grupo.MapGet("/", async Task<Results<Ok<OrcamentoDoMesDto>, ValidationProblem, NotFound>> (
            string? mes, ServicoDeOrcamento servico, CancellationToken ct) =>
            await servico.ObterAsync(mes, ct) switch
            {
                ResultadoDoOrcamento.DoMes d => TypedResults.Ok(d.Orcamento),
                ResultadoDoOrcamento.Invalido i => TypedResults.ValidationProblem(i.Erros.ToDictionary()),
                ResultadoDoOrcamento.NaoEncontrado => TypedResults.NotFound(),
                var outro => throw new UnreachableException(outro.GetType().Name),
            })
        .WithName("ObterOrcamento")
        .WithSummary("Orçamento do mês (AAAA-MM de 2020-01 até hoje + 24 meses; padrão: o mês de hoje em Brasília): " +
                     "fatias, folga, teto e usado por categoria de despesa e o mês de onde copiar tetos.");

        grupo.MapPut("/tetos/{mes}/{categoriaId:guid}", async Task<Results<NoContent, ValidationProblem, ProblemHttpResult, NotFound>> (
            string mes, Guid categoriaId, ValorDoMesDto pedido, ServicoDeOrcamento servico, CancellationToken ct) =>
            pedido.ValorCentavos is not { } valor ? SemValor() : Traduzir(await servico.DefinirTetoAsync(mes, categoriaId, valor, ct)))
        .WithName("DefinirTeto")
        .WithSummary("Cria ou atualiza o teto da categoria de despesa no mês (0 a 100.000.000.000 centavos; zero é um teto). " +
                     "Categoria de receita dá 400; teto novo em categoria arquivada dá 400 (atualizar é permitido).")
        .ProducesProblem(StatusCodes.Status400BadRequest);

        grupo.MapDelete("/tetos/{mes}/{categoriaId:guid}", async Task<Results<NoContent, ValidationProblem, ProblemHttpResult, NotFound>> (
            string mes, Guid categoriaId, ServicoDeOrcamento servico, CancellationToken ct) =>
            Traduzir(await servico.RemoverTetoAsync(mes, categoriaId, ct)))
        .WithName("RemoverTeto")
        .WithSummary("Remove o teto (soft delete). Sem teto no mês dá 404.");

        grupo.MapPut("/receita/{mes}", async Task<Results<NoContent, ValidationProblem, ProblemHttpResult, NotFound>> (
            string mes, ValorDoMesDto pedido, ServicoDeOrcamento servico, CancellationToken ct) =>
            pedido.ValorCentavos is not { } valor ? SemValor() : Traduzir(await servico.DefinirReceitaAsync(mes, valor, ct)))
        .WithName("DefinirReceitaPrevista")
        .WithSummary("Cria ou atualiza a receita prevista do mês (0 a 100.000.000.000 centavos).");

        grupo.MapDelete("/receita/{mes}", async Task<Results<NoContent, ValidationProblem, ProblemHttpResult, NotFound>> (
            string mes, ServicoDeOrcamento servico, CancellationToken ct) =>
            Traduzir(await servico.RemoverReceitaAsync(mes, ct)))
        .WithName("RemoverReceitaPrevista")
        .WithSummary("Remove a receita prevista (soft delete). Sem receita no mês dá 404.");

        grupo.MapPost("/{mes}/copia", async Task<Results<Ok<CopiaDeTetosDto>, ValidationProblem, NotFound>> (
            string mes, ServicoDeOrcamento servico, CancellationToken ct) =>
            await servico.CopiarAsync(mes, ct) switch
            {
                ResultadoDoOrcamento.Copia c => TypedResults.Ok(c.Resultado),
                ResultadoDoOrcamento.Invalido i => TypedResults.ValidationProblem(i.Erros.ToDictionary()),
                ResultadoDoOrcamento.NaoEncontrado => TypedResults.NotFound(),
                var outro => throw new UnreachableException(outro.GetType().Name),
            })
        .WithName("CopiarTetos")
        .WithSummary("Copia para o mês os tetos do último mês anterior com teto, só nas categorias ativas sem linha no mês. " +
                     "origem é nula quando não há de onde copiar.");

        return api;
    }

    private static Results<NoContent, ValidationProblem, ProblemHttpResult, NotFound> SemValor() =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["valorCentavos"] = ["Informe o valor."] });

    private static Results<NoContent, ValidationProblem, ProblemHttpResult, NotFound> Traduzir(ResultadoDoOrcamento resultado) =>
        resultado switch
        {
            ResultadoDoOrcamento.Feito => TypedResults.NoContent(),
            ResultadoDoOrcamento.Invalido invalido => TypedResults.ValidationProblem(invalido.Erros.ToDictionary()),
            ResultadoDoOrcamento.NaoEncontrado => TypedResults.NotFound(),
            ResultadoDoOrcamento.Recusado r => TypedResults.Problem(title: r.Titulo, statusCode: r.Status),
            _ => throw new UnreachableException(resultado.GetType().Name),
        };
}
