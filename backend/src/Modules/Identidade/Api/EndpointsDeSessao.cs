using System.Security.Claims;
using GestorFinanceiro.Modules.Identidade.Aplicacao;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.SharedKernel.Aplicacao;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace GestorFinanceiro.Modules.Identidade.Api;

/// <summary>Quem sou eu e sair. Exigem sessão (política padrão do grupo /api/v1).</summary>
internal static class EndpointsDeSessao
{
    public static IEndpointRouteBuilder MapAutenticacao(this IEndpointRouteBuilder api)
    {
        var grupo = api.MapGroup("/auth").WithTags("Autenticacao");

        grupo.MapGet("/eu", async Task<Results<Ok<EuDto>, UnauthorizedHttpResult>> (
            ClaimsPrincipal usuario, IConsultaDeUsuarios consulta, IUsuarioAtual atual, TimeProvider relogio, HttpResponse resposta, CancellationToken ct) =>
        {
            if (atual.UsuarioId is not { } usuarioId || atual.CasalId is not { } casalId) return TypedResults.Unauthorized();
            var membros = await consulta.ListarDoCasalAsync(casalId, ct);
            var eu = membros.FirstOrDefault(m => m.UsuarioId == usuarioId);
            if (eu is null) return TypedResults.Unauthorized();
            var conjuge = membros.FirstOrDefault(m => m.UsuarioId != usuarioId);
            var recente = ExigirConfirmacaoRecente.ConfirmacaoEstaRecente(
                usuario.FindFirstValue(NomesDoLogin.ClaimConfirmadaEm), relogio.GetUtcNow());
            resposta.Headers.CacheControl = "no-store";
            return TypedResults.Ok(new EuDto(usuarioId, eu.Apelido, conjuge?.Apelido ?? string.Empty, recente));
        })
        .WithName("ObterEu")
        .WithSummary("Quem está logado e se a confirmação de identidade ainda é recente.");

        grupo.MapPost("/sair", async Task<NoContent> (
            ClaimsPrincipal usuario, ServicoDeSessoes sessoes, HttpResponse resposta, CancellationToken ct) =>
        {
            if (Guid.TryParse(usuario.FindFirstValue(NomesDoLogin.ClaimSessao), out var sessaoId))
                await sessoes.EncerrarAsync(sessaoId, ct);
            ServicoDeSessoes.ApagarCookie(resposta);
            return TypedResults.NoContent();
        })
        .WithName("Sair")
        .WithSummary("Encerra a sessão e apaga o cookie.");

        return api;
    }
}
