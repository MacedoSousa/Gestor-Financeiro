using GestorFinanceiro.Modules.Contas.Api;
using GestorFinanceiro.Modules.Contas.Aplicacao;
using GestorFinanceiro.Modules.Contas.Contracts;
using GestorFinanceiro.Modules.Contas.Infra;
using GestorFinanceiro.SharedKernel.Persistencia;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GestorFinanceiro.Modules.Contas;

/// <summary>Contas bancárias, cartões e o calendário da fatura.</summary>
public static class ContasModule
{
    public static IServiceCollection AddContas(this IServiceCollection services, string conexao)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conexao);

        services.AddDbContext<ContasDbContext>((sp, o) => o
            .UseNpgsql(conexao, n => n.MigrationsHistoryTable("__ef_historico", "contas"))
            .AddInterceptors(
                sp.GetRequiredService<SoftDeleteInterceptor>(),
                sp.GetRequiredService<ContextoAuditoriaInterceptor>()));

        services.AddScoped<IConsultaDeContas, ConsultaDeContas>();
        services.AddScoped<ServicoDeContas>();
        services.AddScoped<IContasLigadas, ContasLigadas>();
        return services;
    }

    public static IEndpointRouteBuilder MapContas(this IEndpointRouteBuilder api) => api.MapEndpointsDeContas();
}
