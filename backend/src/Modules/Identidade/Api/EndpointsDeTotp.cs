using GestorFinanceiro.Modules.Identidade.Aplicacao;
using GestorFinanceiro.Modules.Identidade.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace GestorFinanceiro.Modules.Identidade.Api;

/// <summary>Registro e login por TOTP. Anônimos; o registro exige a autorização de registro (cookie).</summary>
internal static class EndpointsDeTotp
{
    internal const string MensagemSemAutorizacao = "Autorização de registro ausente ou expirada. Comece o acesso de novo.";
    internal const string MensagemApelidoInvalido = "Informe um nome para o dispositivo, com até 60 caracteres.";

    public static IEndpointRouteBuilder MapTotp(this IEndpointRouteBuilder api)
    {
        var grupo = api.MapGroup("/auth/totp").WithTags("Autenticacao").AllowAnonymous();

        grupo.MapPost("/registro", async Task<Results<Ok<TotpGeradoDto>, ProblemHttpResult>> (
            NovoTotpDto pedido, ServicoDeTotp totp, HttpRequest requisicao, HttpResponse resposta, CancellationToken ct) =>
        {
            resposta.Headers.CacheControl = "no-store";
            return await totp.RegistrarAsync(requisicao.Cookies[NomesDoLogin.CookieDeRegistro], pedido.Apelido, ct) switch
            {
                ResultadoDoTotp.Gerado g => TypedResults.Ok(new TotpGeradoDto(g.CredencialId, g.OtpauthUri)),
                ResultadoDoTotp.ApelidoInvalido => TypedResults.Problem(title: MensagemApelidoInvalido, statusCode: StatusCodes.Status400BadRequest),
                _ => TypedResults.Problem(title: MensagemSemAutorizacao, statusCode: StatusCodes.Status401Unauthorized),
            };
        })
        .WithName("RegistrarTotp")
        .WithSummary("Gera um segredo TOTP não confirmado e devolve a URI otpauth (no-store). Exige o cookie de autorização de registro.")
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized);

        grupo.MapPost("/confirmacao", async Task<Results<NoContent, ProblemHttpResult>> (
            ConfirmarTotpDto pedido, ServicoDeTotp totp, HttpRequest requisicao, HttpResponse resposta, CancellationToken ct) =>
        {
            resposta.Headers.CacheControl = "no-store";
            return Resposta(await totp.ConfirmarAsync(requisicao.Cookies[NomesDoLogin.CookieDeRegistro], pedido.CredencialId, pedido.Codigo, ct), resposta);
        })
        .WithName("ConfirmarTotp")
        .WithSummary("Confirma o TOTP com o primeiro código, encerra o primeiro acesso e abre a sessão (204 com cookie).")
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status423Locked);

        grupo.MapPost("/", async Task<Results<NoContent, ProblemHttpResult>> (
            EntrarComTotpDto pedido, ServicoDeTotp totp, HttpResponse resposta, CancellationToken ct) =>
        {
            resposta.Headers.CacheControl = "no-store";
            return Resposta(await totp.EntrarAsync(pedido.Cpf, pedido.Codigo, ct), resposta);
        })
        .WithName("EntrarComTotp")
        .WithSummary("Entra com CPF e código TOTP; 204 com o cookie de sessão.")
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status423Locked);

        return api;
    }

    private static Results<NoContent, ProblemHttpResult> Resposta(ResultadoDoTotp resultado, HttpResponse resposta)
    {
        switch (resultado)
        {
            case ResultadoDoTotp.Entrou entrou:
                ServicoDeSessoes.EscreverCookie(resposta, entrou.Token, entrou.ExpiraEm);
                return TypedResults.NoContent();
            case ResultadoDoTotp.Bloqueado:
                return TypedResults.Problem(title: EndpointsDeSessao.MensagemContaBloqueada, statusCode: StatusCodes.Status423Locked);
            case ResultadoDoTotp.NaoAutorizado:
                return TypedResults.Problem(title: MensagemSemAutorizacao, statusCode: StatusCodes.Status401Unauthorized);
            default:
                return TypedResults.Problem(title: EndpointsDeSessao.MensagemFalhaDeLogin, statusCode: StatusCodes.Status401Unauthorized);
        }
    }
}
