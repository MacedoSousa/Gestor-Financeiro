using System.Security.Claims;
using System.Text.Encodings.Web;
using GestorFinanceiro.Modules.Identidade.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace GestorFinanceiro.Api.Infra;

/// <summary>Autentica pelo cookie de sessão. Nunca loga o token; só ids.</summary>
internal sealed class AutenticacaoPorSessao(
    IOptionsMonitor<AuthenticationSchemeOptions> opcoes, ILoggerFactory logger, UrlEncoder encoder, ISessoes sessoes)
    : AuthenticationHandler<AuthenticationSchemeOptions>(opcoes, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(NomesDoLogin.CookieDeSessao, out var token) || string.IsNullOrEmpty(token))
            return AuthenticateResult.NoResult();

        var sessao = await sessoes.ValidarAsync(token, Context.RequestAborted);
        if (sessao is null) return AuthenticateResult.Fail("Sessão inválida.");

        var identidade = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, sessao.UsuarioId.ToString()),
            new Claim(NomesDoLogin.ClaimCasal, sessao.CasalId.ToString()),
            new Claim(NomesDoLogin.ClaimSessao, sessao.SessaoId.ToString()),
            new Claim(NomesDoLogin.ClaimConfirmadaEm, sessao.ConfirmadaEm.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
        ], Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identidade), Scheme.Name));
    }
}
