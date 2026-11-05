using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GestorFinanceiro.Modules.Investimentos;

/// <summary>Posições, aportes e metas (casa, carro). Implementação na Fase 1.</summary>
public static class InvestimentosModule
{
    public static IServiceCollection AddInvestimentos(this IServiceCollection services, string conexao)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conexao);
        return services;
    }

    public static IEndpointRouteBuilder MapInvestimentos(this IEndpointRouteBuilder api) => api;
}
