using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GestorFinanceiro.Modules.Lancamentos;

/// <summary>Transações de todas as origens, categorias e conciliação. Implementação na Fase 1.</summary>
public static class LancamentosModule
{
    public static IServiceCollection AddLancamentos(this IServiceCollection services, string conexao)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conexao);
        return services;
    }

    public static IEndpointRouteBuilder MapLancamentos(this IEndpointRouteBuilder api) => api;
}
