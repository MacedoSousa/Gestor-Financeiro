using GestorFinanceiro.Modules.Integracoes.Api;
using GestorFinanceiro.Modules.Integracoes.Aplicacao;
using GestorFinanceiro.Modules.Integracoes.Infra;
using GestorFinanceiro.Modules.Integracoes.Pluggy;
using GestorFinanceiro.SharedKernel.Persistencia;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
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

        services.AddDbContext<IntegracoesDbContext>((sp, o) => o
            .UseNpgsql(conexao, n => n.MigrationsHistoryTable("__ef_historico", "integracoes"))
            .AddInterceptors(
                sp.GetRequiredService<SoftDeleteInterceptor>(),
                sp.GetRequiredService<ContextoAuditoriaInterceptor>()));
        services.AddScoped<ServicoDeConexoes>();
        services.AddScoped<Descoberta>();

        // Pluggy, só de leitura (ADR-015). Url e PastaDeSegredos na seção "Pluggy" da configuração.
        services.AddOptions<OpcoesDaPluggy>().BindConfiguration("Pluggy");
        services.AddSingleton<CacheDeChavesDaPluggy>();
        services.AddSingleton<IEsperaDaPluggy, EsperaReal>();
        services.AddSingleton<SegredosDaPluggy>();
        services.AddHttpClient<IClientePluggy, ClientePluggy>((sp, http) =>
            {
                http.BaseAddress = new Uri(sp.GetRequiredService<IOptions<OpcoesDaPluggy>>().Value.Url);
                http.Timeout = TimeSpan.FromSeconds(30);
            })
            // Sem o log padrão do HttpClientFactory: ele grava a URL de cada chamada, com o id do item (cifrado no
            // banco, D7) e o da conta (regra 5). O log próprio do ClientePluggy (rota sem ids, status, código) continua.
            .RemoveAllLoggers();
        return services;
    }

    public static IEndpointRouteBuilder MapIntegracoes(this IEndpointRouteBuilder api) =>
        api.MapEndpointsDeConexoes().MapEndpointsDeNascimento();
}
