using GestorFinanceiro.Modules.Contas.Contracts;
using GestorFinanceiro.Modules.Investimentos.Contracts;
using GestorFinanceiro.Modules.Investimentos.Dominio;
using GestorFinanceiro.Modules.Investimentos.Infra;
using GestorFinanceiro.SharedKernel.Aplicacao;
using Microsoft.EntityFrameworkCore;

namespace GestorFinanceiro.Modules.Investimentos.Aplicacao;

/// <summary>
/// Liga a conta de um grupo de investimento à meta (nascimento, D17). Na mesma trava por casal que as escritas de metas;
/// idempotente: a conta já na meta devolve verdadeiro sem gravar de novo.
/// </summary>
internal sealed class MetasDeContas(InvestimentosDbContext db, IUsuarioAtual atual, IConsultaDeContas contas) : IMetasDeContas
{
    private static readonly HashSet<string> TiposDeInvestimento = new(StringComparer.Ordinal) { "POUPANCA", "INVESTIMENTO" };

    public async Task<bool> LigarContaAMetaAsync(Guid contaId, Guid metaId, CancellationToken ct)
    {
        var casalId = atual.CasalId ?? throw new InvalidOperationException("Meta sem casal.");
        if (await contas.ObterAsync(contaId, ct) is not { } conta || !TiposDeInvestimento.Contains(conta.Tipo)) return false;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended('investimentos.casal:' || {casalId}::text, 0))", ct);

        if (!await db.Metas.AnyAsync(m => m.Id == metaId, ct)) return false;
        var vinculo = await db.MetasContas.FirstOrDefaultAsync(v => v.ContaId == contaId, ct);
        if (vinculo is not null) return vinculo.MetaId == metaId;

        db.MetasContas.Add(MetaConta.Criar(casalId, metaId, contaId));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }
}
