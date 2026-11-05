using GestorFinanceiro.Modules.Identidade.Aplicacao;
using GestorFinanceiro.Modules.Identidade.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace GestorFinanceiro.Modules.Identidade.Api;

/// <summary>Registro e login por passkey. Anônimos; o registro exige a autorização de registro (cookie).</summary>
internal static class EndpointsDePasskey
{
    public static IEndpointRouteBuilder MapPasskey(this IEndpointRouteBuilder api)
    {
        var grupo = api.MapGroup("/auth/passkey").WithTags("Autenticacao").AllowAnonymous();

        grupo.MapPost("/registro/opcoes", async Task<Results<Ok<OpcoesDeRegistroDePasskeyDto>, ProblemHttpResult>> (
            ServicoDePasskeys passkeys, HttpRequest requisicao, HttpResponse resposta, CancellationToken ct) =>
        {
            resposta.Headers.CacheControl = "no-store";
            return await passkeys.OpcoesDeRegistroAsync(requisicao.Cookies[NomesDoLogin.CookieDeRegistro], ct) switch
            {
                ResultadoDaPasskey.OpcoesDeRegistro o => TypedResults.Ok(o.Dto),
                _ => TypedResults.Problem(title: EndpointsDeTotp.MensagemSemAutorizacao, statusCode: StatusCodes.Status401Unauthorized),
            };
        })
        .WithName("OpcoesDeRegistroDePasskey")
        .WithSummary("Opções de criação da passkey (credencial descobrível, UV obrigatória, attestation none). Exige o cookie de autorização de registro.")
        .ProducesProblem(StatusCodes.Status401Unauthorized);

        grupo.MapPost("/registro", async Task<Results<NoContent, ProblemHttpResult>> (
            NovaPasskeyDto pedido, ServicoDePasskeys passkeys, HttpRequest requisicao, HttpResponse resposta, CancellationToken ct) =>
        {
            resposta.Headers.CacheControl = "no-store";
            return Resposta(await passkeys.RegistrarAsync(requisicao.Cookies[NomesDoLogin.CookieDeRegistro], pedido, ct), resposta);
        })
        .WithName("RegistrarPasskey")
        .WithSummary("Grava a passkey, encerra o primeiro acesso e abre a sessão (204 com cookie).")
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status423Locked);

        grupo.MapPost("/opcoes", Ok<OpcoesDeLoginComPasskeyDto> (ServicoDePasskeys passkeys, HttpResponse resposta) =>
        {
            resposta.Headers.CacheControl = "no-store";
            return TypedResults.Ok(passkeys.OpcoesDeLogin());
        })
        .WithName("OpcoesDeLoginComPasskey")
        .WithSummary("Opções de asserção sem allowCredentials (credencial descobrível) e com UV obrigatória.");

        grupo.MapPost("/", async Task<Results<NoContent, ProblemHttpResult>> (
            RespostaDeLoginComPasskeyDto pedido, ServicoDePasskeys passkeys, HttpResponse resposta, CancellationToken ct) =>
        {
            resposta.Headers.CacheControl = "no-store";
            return Resposta(await passkeys.EntrarAsync(pedido, ct), resposta);
        })
        .WithName("EntrarComPasskey")
        .WithSummary("Entra com a asserção da passkey; 204 com o cookie de sessão.")
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status423Locked);

        return api;
    }

    private static Results<NoContent, ProblemHttpResult> Resposta(ResultadoDaPasskey resultado, HttpResponse resposta)
    {
        switch (resultado)
        {
            case ResultadoDaPasskey.Entrou entrou:
                ServicoDeSessoes.EscreverCookie(resposta, entrou.Token, entrou.ExpiraEm);
                return TypedResults.NoContent();
            case ResultadoDaPasskey.Bloqueado:
                return TypedResults.Problem(title: EndpointsDeSessao.MensagemContaBloqueada, statusCode: StatusCodes.Status423Locked);
            case ResultadoDaPasskey.NaoAutorizado:
                return TypedResults.Problem(title: EndpointsDeTotp.MensagemSemAutorizacao, statusCode: StatusCodes.Status401Unauthorized);
            case ResultadoDaPasskey.ApelidoInvalido:
                return TypedResults.Problem(title: EndpointsDeTotp.MensagemApelidoInvalido, statusCode: StatusCodes.Status400BadRequest);
            default:
                return TypedResults.Problem(title: EndpointsDeSessao.MensagemFalhaDeLogin, statusCode: StatusCodes.Status401Unauthorized);
        }
    }
}
