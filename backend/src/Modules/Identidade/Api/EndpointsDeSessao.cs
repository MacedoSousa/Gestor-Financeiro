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
    internal const string MensagemFalhaDeLogin = "Não foi possível entrar. Confira os dados e tente de novo.";
    internal const string MensagemContaBloqueada = "Conta bloqueada por excesso de tentativas. Tente mais tarde ou peça ao outro cônjuge para destravar.";
    internal const string MensagemConfirmacaoInvalida = "Informe o código TOTP ou a passkey.";
    internal const string MensagemJaConfigurado = "Este acesso já foi configurado. Entre com sua passkey ou TOTP.";

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
        .AllowAnonymous()
        .WithName("Sair")
        .WithSummary("Encerra a sessão (se houver) e apaga o cookie; sempre 204.");

        grupo.MapPost("/primeiro-acesso", async Task<Results<NoContent, ProblemHttpResult>> (
            PrimeiroAcessoDto pedido, PrimeiroAcesso primeiroAcesso, HttpResponse resposta, CancellationToken ct) =>
        {
            resposta.Headers.CacheControl = "no-store";
            switch (await primeiroAcesso.ExecutarAsync(pedido.Cpf, pedido.Senha, ct))
            {
                case ResultadoDoPrimeiroAcesso.Autorizado autorizado:
                    AutorizacoesDeRegistro.EscreverCookie(resposta, autorizado.Token);
                    return TypedResults.NoContent();
                case ResultadoDoPrimeiroAcesso.Bloqueado:
                    return TypedResults.Problem(title: MensagemContaBloqueada, statusCode: StatusCodes.Status423Locked);
                case ResultadoDoPrimeiroAcesso.JaConfigurado:
                    return TypedResults.Problem(title: MensagemJaConfigurado, statusCode: StatusCodes.Status403Forbidden);
                default:
                    return TypedResults.Problem(title: MensagemFalhaDeLogin, statusCode: StatusCodes.Status401Unauthorized);
            }
        })
        .AllowAnonymous()
        .WithName("PrimeiroAcesso")
        .WithSummary("Primeiro acesso por CPF e senha do cadastro; responde 204 com o cookie de autorização de registro.")
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status423Locked);

        grupo.MapPost("/confirmar/opcoes", async Task<Results<Ok<OpcoesDeConfirmacaoDto>, UnauthorizedHttpResult>> (
            ClaimsPrincipal usuario, IUsuarioAtual atual, ServicoDePasskeys passkeys, HttpResponse resposta, CancellationToken ct) =>
        {
            if (atual.UsuarioId is not { } usuarioId || !Guid.TryParse(usuario.FindFirstValue(NomesDoLogin.ClaimSessao), out var sessaoId))
                return TypedResults.Unauthorized();
            resposta.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await passkeys.OpcoesDeConfirmacaoAsync(usuarioId, sessaoId, ct));
        })
        .WithName("OpcoesDeConfirmacao")
        .WithSummary("Opções de asserção para confirmar a identidade, com as passkeys do usuário da sessão; o desafio fica ligado à sessão.");

        grupo.MapPost("/confirmar", async Task<Results<NoContent, ProblemHttpResult, UnauthorizedHttpResult>> (
            ConfirmacaoDeIdentidadeDto pedido, ClaimsPrincipal usuario, IUsuarioAtual atual, ConfirmacaoDeIdentidade confirmacao,
            HttpResponse resposta, CancellationToken ct) =>
        {
            if (atual.UsuarioId is not { } usuarioId || !Guid.TryParse(usuario.FindFirstValue(NomesDoLogin.ClaimSessao), out var sessaoId))
                return TypedResults.Unauthorized();
            resposta.Headers.CacheControl = "no-store";
            return await confirmacao.ConfirmarAsync(usuarioId, sessaoId, pedido, ct) switch
            {
                ResultadoDaConfirmacao.Confirmado => TypedResults.NoContent(),
                ResultadoDaConfirmacao.Bloqueado => TypedResults.Problem(title: MensagemContaBloqueada, statusCode: StatusCodes.Status423Locked),
                ResultadoDaConfirmacao.Invalido => TypedResults.Problem(title: MensagemConfirmacaoInvalida, statusCode: StatusCodes.Status400BadRequest),
                _ => TypedResults.Problem(title: MensagemFalhaDeLogin, statusCode: StatusCodes.Status401Unauthorized),
            };
        })
        .WithName("ConfirmarIdentidade")
        .WithSummary("Renova a confirmação recente com o TOTP ou uma passkey do próprio usuário; erro conta falha.")
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status423Locked);

        return api;
    }
}
