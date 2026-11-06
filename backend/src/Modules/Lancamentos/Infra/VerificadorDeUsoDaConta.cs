using GestorFinanceiro.Modules.Contas.Contracts;
using Microsoft.EntityFrameworkCore;

namespace GestorFinanceiro.Modules.Lancamentos.Infra;

/// <summary>A conta está em uso quando tem algum lançamento não apagado (o filtro SoftDelete já cuida disso).</summary>
internal sealed class VerificadorDeUsoDaConta(LancamentosDbContext db) : IVerificadorDeUsoDaConta
{
    public Task<bool> EstaEmUsoAsync(Guid contaId, CancellationToken ct) =>
        db.Transacoes.AnyAsync(t => t.ContaId == contaId, ct);
}
