using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GestorFinanceiro.Modules.Contas;

/// <summary>Contas bancárias, cartões, faturas e saldos. Implementação na Fase 1.</summary>
public static class ContasModule
{
    public static IServiceCollection AddContas(this IServiceCollection services, string conexao)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conexao);
        return services;
    }

    public static IEndpointRouteBuilder MapContas(this IEndpointRouteBuilder api) => api;
}
