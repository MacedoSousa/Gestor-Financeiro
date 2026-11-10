using GestorFinanceiro.Modules.Orcamento.Api;
using GestorFinanceiro.Modules.Orcamento.Aplicacao;
using GestorFinanceiro.Modules.Orcamento.Contracts;
using GestorFinanceiro.Modules.Orcamento.Infra;
using GestorFinanceiro.SharedKernel.Persistencia;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GestorFinanceiro.Modules.Orcamento;

/// <summary>Teto por categoria e mês, receita prevista e a divisão da receita em fatias.</summary>
public static class OrcamentoModule
{
    public static IServiceCollection AddOrcamento(this IServiceCollection services, string conexao)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conexao);

        services.AddDbContext<OrcamentoDbContext>((sp, o) => o
            .UseNpgsql(conexao, n => n.MigrationsHistoryTable("__ef_historico", "orcamento"))
            .AddInterceptors(
                sp.GetRequiredService<SoftDeleteInterceptor>(),
                sp.GetRequiredService<ContextoAuditoriaInterceptor>()));

        services.AddScoped<ServicoDeOrcamento>();
        services.AddScoped<IConsultaDoOrcamento, ConsultaDoOrcamento>();
        return services;
    }

    public static IEndpointRouteBuilder MapOrcamento(this IEndpointRouteBuilder api) => api.MapEndpointsDeOrcamento();
}
