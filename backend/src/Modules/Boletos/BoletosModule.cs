using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GestorFinanceiro.Modules.Boletos;

/// <summary>Boletos (DDA copiado do app Itaú ou lido no WhatsApp) e vencimentos. Implementação na Fase 1.</summary>
public static class BoletosModule
{
    public static IServiceCollection AddBoletos(this IServiceCollection services, string conexao)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conexao);
        return services;
    }

    public static IEndpointRouteBuilder MapBoletos(this IEndpointRouteBuilder api) => api;
}
