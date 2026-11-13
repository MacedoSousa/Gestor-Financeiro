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

        grupo.MapPost("/items/{id:guid}/nascimento", async Task<Results<NoContent, ValidationProblem, ProblemHttpResult, NotFound>> (
            Guid id, NascimentoDto pedido, Nascimento nascimento, CancellationToken ct) =>
            Feito(await nascimento.NascerAsync(id, pedido, ct)))
        .WithName("ConfirmarNascimento")
        .WithSummary("Cria as contas confirmadas, já ligadas ao banco (CRIAR), liga a contas que já existem (LIGAR) ou registra " +
                     "\"não importar\". Cada grupo de aplicações vira uma conta INVESTIMENTO, opcionalmente numa meta. Repetir o " +
                     "pedido não duplica. Só o titular (senão 403); apelido repetido ou conta que já vem de outra conexão dão 409.")
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status502BadGateway)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        grupo.MapPost("/vinculos/{id:guid}/inclusao", async Task<Results<NoContent, ValidationProblem, ProblemHttpResult, NotFound>> (
            Guid id, InclusaoDto pedido, Nascimento nascimento, CancellationToken ct) =>
            Feito(await nascimento.IncluirAsync(id, pedido, ct)))
        .WithName("IncluirContaNaoImportada")
        .WithSummary("Inclui uma conta marcada \"não importar\": CRIAR ou LIGAR, como na confirmação. Só o titular (senão 403); " +
                     "conta que não está marcada dá 409.")
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status409Conflict);

        grupo.MapPost("/items/{id:guid}/sincronizacao", async Task<Results<Ok<SincronizacaoDto>, ProblemHttpResult, NotFound>> (
            Guid id, Sincronizacao sincronizacao, CancellationToken ct) =>
            await sincronizacao.SincronizarAsync(id, ct) switch
            {
                ResultadoDaSincronizacao.Feita f => TypedResults.Ok(f.Dto),
                ResultadoDaSincronizacao.Recusada { Motivo: ResultadoDeNascimento.Recusado r } => Problema(r),
                ResultadoDaSincronizacao.Recusada { Motivo: ResultadoDeNascimento.NaoEncontrado } => TypedResults.NotFound(),
                var outro => throw new UnreachableException(outro.GetType().Name),
            })
        .WithName("SincronizarItem")
        .WithSummary("Importa agora as contas e os cartões nascidos deste banco: na primeira vez, o histórico desde 2026-01-01 e o " +
                     "saldo inicial; depois, os últimos 10 dias. Conta arquivada ou \"não importar\" fica de fora; a falha numa conta " +
                     "não desfaz as outras (contasComFalha). Só o titular (senão 403); banco recusou dá 502, Pluggy fora do ar dá 503.")
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status502BadGateway)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return api;
    }

    private static Results<NoContent, ValidationProblem, ProblemHttpResult, NotFound> Feito(ResultadoDeNascimento resultado) =>
        resultado switch
        {
            ResultadoDeNascimento.Feito => TypedResults.NoContent(),
            ResultadoDeNascimento.Invalido i => TypedResults.ValidationProblem(i.Erros.ToDictionary()),
            ResultadoDeNascimento.Recusado r => Problema(r),
            ResultadoDeNascimento.NaoEncontrado => TypedResults.NotFound(),
            _ => throw new UnreachableException(resultado.GetType().Name),
        };

    internal static ProblemHttpResult Problema(ResultadoDeNascimento.Recusado r) =>
        TypedResults.Problem(title: r.Titulo, statusCode: r.Status,
            extensions: r.Extras?.ToDictionary(e => e.Key, e => e.Value));
}
