using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GestorFinanceiro.Modules.Painel;

/// <summary>Visão geral do casal. Sem schema: só combina os Contracts dos outros módulos.</summary>
public static class PainelModule
{
    public static IServiceCollection AddPainel(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapPainel(this IEndpointRouteBuilder api) => api;
}
