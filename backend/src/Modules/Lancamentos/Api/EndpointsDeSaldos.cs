using GestorFinanceiro.Modules.Lancamentos.Aplicacao;
using GestorFinanceiro.Modules.Lancamentos.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GestorFinanceiro.Modules.Lancamentos.Api;

/// <summary>Saldos das contas e totais de fatura. Exigem sessão (política padrão do grupo /api/v1).</summary>
internal static class EndpointsDeSaldos
{
    public static IEndpointRouteBuilder MapEndpointsDeSaldos(this IEndpointRouteBuilder api)
    {
        api.MapGet("/saldos", async (DateOnly? em, ConsultaDeSaldos consulta, HttpResponse resposta, CancellationToken ct) =>
        {
            resposta.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await consulta.ObterAsync(em, ct));
        })
        .WithTags("Lancamentos")
        .WithName("ObterSaldos")
        .WithSummary("Saldo de cada conta do casal na data (padrão: hoje em Brasília) e, nos cartões, a fatura aberta e a última fechada.");

        return api;
    }
}
