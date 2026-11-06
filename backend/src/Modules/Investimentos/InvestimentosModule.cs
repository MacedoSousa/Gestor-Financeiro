using GestorFinanceiro.Modules.Investimentos.Api;
using GestorFinanceiro.Modules.Investimentos.Aplicacao;
using GestorFinanceiro.Modules.Investimentos.Infra;
using GestorFinanceiro.SharedKernel.Persistencia;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GestorFinanceiro.Modules.Investimentos;

/// <summary>Metas (casa, carro) sobre as contas de poupança e investimento. Posições e ativos ficam para o Open Finance.</summary>
public static class InvestimentosModule
{
    public static IServiceCollection AddInvestimentos(this IServiceCollection services, string conexao)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conexao);

        services.AddDbContext<InvestimentosDbContext>((sp, o) => o
            .UseNpgsql(conexao, n => n.MigrationsHistoryTable("__ef_historico", "investimentos"))
            .AddInterceptors(
                sp.GetRequiredService<SoftDeleteInterceptor>(),
                sp.GetRequiredService<ContextoAuditoriaInterceptor>()));

        services.AddScoped<ServicoDeMetas>();
        return services;
    }

    public static IEndpointRouteBuilder MapInvestimentos(this IEndpointRouteBuilder api) => api.MapEndpointsDeMetas();
}
