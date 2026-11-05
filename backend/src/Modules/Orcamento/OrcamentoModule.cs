using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GestorFinanceiro.Modules.Orcamento;

/// <summary>Teto por categoria e mês, saldo restante. Implementação na Fase 1.</summary>
public static class OrcamentoModule
{
    public static IServiceCollection AddOrcamento(this IServiceCollection services, string conexao)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conexao);
        return services;
    }

    public static IEndpointRouteBuilder MapOrcamento(this IEndpointRouteBuilder api) => api;
}
