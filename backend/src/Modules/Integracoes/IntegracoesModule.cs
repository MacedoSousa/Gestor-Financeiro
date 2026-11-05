using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GestorFinanceiro.Modules.Integracoes;

/// <summary>
/// Pluggy (Fase 2), gateway de mensagens e leitor de comprovantes (Fase 3).
/// Cada provedor fica atrás de uma interface para poder ser trocado (ADR-008).
/// </summary>
public static class IntegracoesModule
{
    public static IServiceCollection AddIntegracoes(this IServiceCollection services, string conexao)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conexao);
        return services;
    }

    public static IEndpointRouteBuilder MapIntegracoes(this IEndpointRouteBuilder api) => api;
}
