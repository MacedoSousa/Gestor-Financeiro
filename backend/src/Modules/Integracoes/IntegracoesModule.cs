using GestorFinanceiro.Modules.Integracoes.Pluggy;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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

        // Pluggy, só de leitura (ADR-015). Url e PastaDeSegredos na seção "Pluggy" da configuração.
        services.AddOptions<OpcoesDaPluggy>().BindConfiguration("Pluggy");
        services.AddSingleton<CacheDeChavesDaPluggy>();
        services.AddSingleton<IEsperaDaPluggy, EsperaReal>();
        services.AddSingleton<SegredosDaPluggy>();
        services.AddHttpClient<IClientePluggy, ClientePluggy>((sp, http) =>
        {
            http.BaseAddress = new Uri(sp.GetRequiredService<IOptions<OpcoesDaPluggy>>().Value.Url);
            http.Timeout = TimeSpan.FromSeconds(30);
        });
        return services;
    }

    public static IEndpointRouteBuilder MapIntegracoes(this IEndpointRouteBuilder api) => api;
}
