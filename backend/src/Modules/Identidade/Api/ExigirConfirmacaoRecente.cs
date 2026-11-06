using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.Modules.Identidade.Dominio;
using Microsoft.AspNetCore.Http;

namespace GestorFinanceiro.Modules.Identidade.Api;

/// <summary>Ações sensíveis exigem passkey ou TOTP confirmados nos últimos 5 minutos.</summary>
internal sealed class ExigirConfirmacaoRecente : IEndpointFilter
{
    internal const string Titulo = "Confirme sua identidade para continuar.";

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext contexto, EndpointFilterDelegate proximo)
    {
        var http = contexto.HttpContext;
        var agora = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        if (!ConfirmacaoEstaRecente(http.User.FindFirst(NomesDoLogin.ClaimConfirmadaEm)?.Value, agora))
            return ValueTask.FromResult<object?>(TypedResults.Problem(title: Titulo, statusCode: StatusCodes.Status403Forbidden));
        return proximo(contexto);
    }

    internal static bool ConfirmacaoEstaRecente(string? claim, DateTimeOffset agora) =>
        DateTimeOffset.TryParse(claim, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var quando)
        && agora - quando <= TemposDoLogin.ConfirmacaoRecente;
}
