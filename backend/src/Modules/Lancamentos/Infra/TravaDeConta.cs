using GestorFinanceiro.Modules.Contas.Contracts;
using Microsoft.EntityFrameworkCore;

namespace GestorFinanceiro.Modules.Lancamentos.Infra;

/// <summary>
/// Toma, na transação aberta, a trava de cada conta (<see cref="ChaveDeTrava.Conta"/>, a mesma do apagar conta), antes
/// de validar a conta. Sempre em ordem crescente de id: duas transferências em sentidos opostos não se travam.
/// </summary>
internal static class TravaDeConta
{
    public static async Task TomarAsync(LancamentosDbContext db, IEnumerable<Guid> contas, CancellationToken ct)
    {
        foreach (var id in contas.Distinct().Order())
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({ChaveDeTrava.Conta(id)}, 0))", ct);
    }
}
