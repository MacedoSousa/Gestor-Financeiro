using GestorFinanceiro.Modules.Identidade.Aplicacao;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.SharedKernel.Aplicacao;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace GestorFinanceiro.Modules.Identidade.Api;

/// <summary>Credenciais do usuário logado e o novo código de recuperação do casal.</summary>
internal static class EndpointsDeCredenciais
{
    internal const string MensagemUltimaCredencial = "Você precisa manter pelo menos uma forma de entrar.";

    public static IEndpointRouteBuilder MapCredenciais(this IEndpointRouteBuilder api)
    {
        var grupo = api.MapGroup("/dispositivos/credenciais").WithTags("Dispositivos");

        grupo.MapGet("/", async Task<Ok<IReadOnlyList<CredencialDto>>> (GestaoDeCredenciais gestao, HttpResponse resposta, CancellationToken ct) =>
        {
            resposta.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await gestao.ListarAsync(ct));
        })
        .WithName("ListarCredenciais")
        .WithSummary("Passkeys e TOTPs ativos e confirmados do próprio usuário.");

        grupo.MapDelete("/{id:guid}", async Task<Results<NoContent, NotFound, ProblemHttpResult>> (Guid id, GestaoDeCredenciais gestao, CancellationToken ct) =>
            await gestao.RemoverAsync(id, ct) switch
            {
                ResultadoDaRemocao.Removida => TypedResults.NoContent(),
                ResultadoDaRemocao.UltimaCredencial => TypedResults.Problem(title: MensagemUltimaCredencial, statusCode: StatusCodes.Status409Conflict),
                _ => TypedResults.NotFound(),
            })
        .AddEndpointFilter<ExigirConfirmacaoRecente>()
        .WithName("RemoverCredencial")
        .WithSummary("Remove (soft delete) uma credencial do próprio usuário; exige confirmação recente. A última ativa e confirmada dá 409.")
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status409Conflict);

        grupo.MapPost("/autorizacao", Results<NoContent, UnauthorizedHttpResult> (IUsuarioAtual atual, AutorizacoesDeRegistro autorizacoes, HttpResponse resposta) =>
        {
            if (atual.UsuarioId is not { } eu || atual.CasalId is not { } casalId) return TypedResults.Unauthorized();
            resposta.Headers.CacheControl = "no-store";
            AutorizacoesDeRegistro.EscreverCookie(resposta, autorizacoes.Emitir(eu, casalId, OrigemDoRegistro.UsuarioLogado));
            return TypedResults.NoContent();
        })
        .AddEndpointFilter<ExigirConfirmacaoRecente>()
        .WithName("AutorizarNovaCredencial")
        .WithSummary("Emite o cookie de autorização de registro para o próprio usuário adicionar outra passkey ou TOTP; exige confirmação recente.")
        .ProducesProblem(StatusCodes.Status403Forbidden);

        api.MapPost("/casal/codigo-recuperacao", async Task<Results<Created<CodigoRecuperacaoDto>, UnauthorizedHttpResult>> (
            NovoCodigoDeRecuperacao novo, HttpResponse resposta, CancellationToken ct) =>
        {
            resposta.Headers.CacheControl = "no-store";
            if (await novo.GerarAsync(ct) is not { } codigo) return TypedResults.Unauthorized();
            return TypedResults.Created("/api/v1/casal", new CodigoRecuperacaoDto(codigo));
        })
        .AddEndpointFilter<ExigirConfirmacaoRecente>()
        .WithTags("Identidade")
        .WithName("GerarCodigoDeRecuperacao")
        .WithSummary("Gera um código de recuperação novo para o casal (o anterior deixa de valer); mostrado uma única vez, no-store. Exige confirmação recente.")
        .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }
}
