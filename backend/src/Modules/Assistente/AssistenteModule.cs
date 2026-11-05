using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GestorFinanceiro.Modules.Assistente;

/// <summary>
/// FinanceEngine (cálculo determinístico) + Gemini (só redige, Fase 4).
/// O Gemini recebe agregados com apelidos e o texto é conferido contra os números.
/// </summary>
public static class AssistenteModule
{
    public static IServiceCollection AddAssistente(this IServiceCollection services, string conexao)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conexao);
        return services;
    }

    public static IEndpointRouteBuilder MapAssistente(this IEndpointRouteBuilder api) => api;
}
