using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GestorFinanceiro.Modules.Auditoria;

/// <summary>
/// Só leitura: lista eventos de audit.eventos e verifica a cadeia de hashes.
/// Quem grava a auditoria são os triggers do banco (db/migrations/V0001).
/// </summary>
public static class AuditoriaModule
{
    public static IServiceCollection AddAuditoria(this IServiceCollection services, string conexao)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conexao);
        return services;
    }

    public static IEndpointRouteBuilder MapAuditoria(this IEndpointRouteBuilder api) => api;
}
