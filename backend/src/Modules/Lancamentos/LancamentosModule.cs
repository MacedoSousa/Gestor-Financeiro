using GestorFinanceiro.Modules.Contas.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Api;
using GestorFinanceiro.Modules.Lancamentos.Aplicacao;
using GestorFinanceiro.Modules.Lancamentos.Infra;
using GestorFinanceiro.SharedKernel.Persistencia;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GestorFinanceiro.Modules.Lancamentos;

/// <summary>Transações de todas as origens, categorias e conciliação.</summary>
public static class LancamentosModule
{
    public static IServiceCollection AddLancamentos(this IServiceCollection services, string conexao)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conexao);

        services.AddDbContext<LancamentosDbContext>((sp, o) => o
            .UseNpgsql(conexao, n => n.MigrationsHistoryTable("__ef_historico", "lancamentos"))
            .AddInterceptors(
                sp.GetRequiredService<SoftDeleteInterceptor>(),
                sp.GetRequiredService<ContextoAuditoriaInterceptor>()));

        services.AddScoped<IVerificadorDeUsoDaConta, VerificadorDeUsoDaConta>();
        services.AddScoped<ServicoDeCategorias>();
        services.AddScoped<ConsultaDeSaldos>();
        services.AddScoped<ConsultaDoExtrato>();
        services.AddScoped<ServicoDeLancamentos>();
        services.AddScoped<ServicoDeTransferencias>();
        return services;
    }

    public static IEndpointRouteBuilder MapLancamentos(this IEndpointRouteBuilder api) => api
        .MapEndpointsDeCategorias()
        .MapEndpointsDeSaldos()
        .MapEndpointsDeLancamentos()
        .MapEndpointsDeTransferencias();
}
