using GestorFinanceiro.Modules.Contas.Contracts;
using GestorFinanceiro.Modules.Contas.Dominio;
using Microsoft.EntityFrameworkCore;

namespace GestorFinanceiro.Modules.Contas.Infra;

internal sealed class ConsultaDeContas(ContasDbContext db) : IConsultaDeContas
{
    public async Task<ContaResumo?> ObterAsync(Guid contaId, CancellationToken ct = default)
    {
        var conta = await db.Contas.AsNoTracking().FirstOrDefaultAsync(c => c.Id == contaId, ct);
        return conta is null ? null : ParaResumo(conta);
    }

    public async Task<IReadOnlyList<ContaResumo>> ListarAsync(CancellationToken ct = default) =>
        (await db.Contas.AsNoTracking().OrderBy(c => c.Apelido).ToListAsync(ct)).Select(ParaResumo).ToList();

    public DateOnly? VencimentoDaFatura(ContaResumo conta, DateOnly dataCompra)
    {
        ArgumentNullException.ThrowIfNull(conta);
        return conta is { Tipo: "CARTAO", DiaFechamento: { } fechamento, DiaVencimento: { } vencimento }
            ? CalendarioDaFatura.VencimentoDaFatura(dataCompra, fechamento, vencimento)
            : null;
    }

    public DateOnly? FechamentoDaFatura(ContaResumo conta, DateOnly vencimento)
    {
        ArgumentNullException.ThrowIfNull(conta);
        return conta is { Tipo: "CARTAO", DiaFechamento: { } fechamento, DiaVencimento: { } diaVencimento }
            ? CalendarioDaFatura.FechamentoDaFatura(vencimento, fechamento, diaVencimento)
            : null;
    }

    internal static ContaResumo ParaResumo(Conta c) =>
        new(c.Id, c.Apelido, c.Tipo.ToString().ToUpperInvariant(), c.TitularUsuarioId, c.SaldoInicialCentavos, c.SaldoInicialEm,
            c.DiaFechamento, c.DiaVencimento, c.Arquivada, c.Instituicao, c.Ligada);
}
