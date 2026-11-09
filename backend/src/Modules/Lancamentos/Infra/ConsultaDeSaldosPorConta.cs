using GestorFinanceiro.Modules.Contas.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Contracts;
using Microsoft.EntityFrameworkCore;

namespace GestorFinanceiro.Modules.Lancamentos.Infra;

/// <summary>
/// Saldo de cada conta numa data: saldo inicial mais a soma (no banco, sob os filtros SoftDelete e Casal) dos lançamentos
/// de <c>saldo_inicial_em</c> até a data. É a conta que o GET /saldos usa; o contrato <see cref="IConsultaDeSaldos"/> a reaproveita.
/// </summary>
internal sealed class ConsultaDeSaldosPorConta(LancamentosDbContext db, IConsultaDeContas contas) : IConsultaDeSaldos
{
    public async Task<IReadOnlyList<SaldoDeConta>> SaldosAsync(DateOnly em, CancellationToken ct = default) =>
        (await SaldosAsync(await contas.ListarAsync(ct), em, ct)).Select(p => new SaldoDeConta(p.Key, p.Value)).ToList();

    internal async Task<IReadOnlyDictionary<Guid, long>> SaldosAsync(IReadOnlyList<ContaResumo> lista, DateOnly em, CancellationToken ct)
    {
        var saldos = new Dictionary<Guid, long>(lista.Count);
        foreach (var conta in lista)
        {
            var inicio = conta.SaldoInicialEm;
            var movimento = await db.Transacoes.AsNoTracking()
                .Where(t => t.ContaId == conta.Id && t.Data >= inicio && t.Data <= em)
                .SumAsync(t => (long?)t.ValorCentavos, ct) ?? 0;
            saldos[conta.Id] = conta.SaldoInicialCentavos + movimento;
        }
        return saldos;
    }
}
