using System.Globalization;
using GestorFinanceiro.Modules.Contas.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Aplicacao;
using Microsoft.EntityFrameworkCore;

namespace GestorFinanceiro.Modules.Lancamentos.Infra;

/// <summary>
/// Saldo de cada conta numa data: saldo inicial mais a soma (no banco, sob os filtros SoftDelete e Casal) dos lançamentos
/// de <c>saldo_inicial_em</c> até a data. É a conta que o GET /saldos usa; o contrato <see cref="IConsultaDeSaldos"/> a reaproveita.
/// </summary>
internal sealed class ConsultaDeSaldosPorConta(LancamentosDbContext db, IConsultaDeContas contas, TimeProvider relogio) : IConsultaDeSaldos
{
    public async Task<IReadOnlyList<SaldoDeConta>> SaldosAsync(DateOnly em, CancellationToken ct = default) =>
        (await SaldosAsync(await contas.ListarAsync(ct), em, ct)).Select(p => new SaldoDeConta(p.Key, p.Value)).ToList();

    public async Task<IReadOnlyList<PatrimonioDoMes>> PatrimonioPorMesAsync(DateOnly primeiroMes, int meses, CancellationToken ct = default)
    {
        var lista = await contas.ListarAsync(ct);
        var hoje = Brasilia.Hoje(relogio);
        var pontos = Enumerable.Range(0, Math.Max(meses, 0))
            .Select(i => primeiroMes.AddMonths(i))
            .Select(m => (Mes: m, Ponto: Menor(m.AddMonths(1).AddDays(-1), hoje)))
            .ToList();
        if (pontos.Count == 0) return [];

        // Uma query: movimento por conta e dia (SQL), sob os filtros SoftDelete e Casal. O dia (não o mês) permite
        // descartar em memória o que é anterior ao saldo inicial de cada conta, que já está no saldo inicial.
        var ultimo = pontos.Max(p => p.Ponto);
        var primeiro = lista.Count == 0 ? ultimo : lista.Min(c => c.SaldoInicialEm);
        var movimentos = await db.Transacoes.AsNoTracking()
            .Where(t => t.Data >= primeiro && t.Data <= ultimo)
            .GroupBy(t => new { t.ContaId, t.Data })
            .Select(g => new { g.Key.ContaId, g.Key.Data, Soma = g.Sum(t => t.ValorCentavos) })
            .ToListAsync(ct);
        var porConta = movimentos.ToLookup(m => m.ContaId);

        return pontos.Select(p =>
        {
            var noPonto = lista.Where(c => c.SaldoInicialEm <= p.Ponto).ToList();
            if (noPonto.Count == 0) return new PatrimonioDoMes(p.Mes.ToString("yyyy-MM", CultureInfo.InvariantCulture), null);
            var total = noPonto.Sum(c => c.SaldoInicialCentavos
                + porConta[c.Id].Where(m => m.Data >= c.SaldoInicialEm && m.Data <= p.Ponto).Sum(m => m.Soma));
            return new PatrimonioDoMes(p.Mes.ToString("yyyy-MM", CultureInfo.InvariantCulture), total);
        }).ToList();
    }

    private static DateOnly Menor(DateOnly a, DateOnly b) => a < b ? a : b;

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
