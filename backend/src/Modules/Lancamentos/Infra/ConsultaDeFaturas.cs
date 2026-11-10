using GestorFinanceiro.Modules.Contas.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Aplicacao;
using GestorFinanceiro.Modules.Lancamentos.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Dominio;
using Microsoft.EntityFrameworkCore;

namespace GestorFinanceiro.Modules.Lancamentos.Infra;

/// <summary>
/// Faturas de cada cartão. Devido até a fatura fechada F = saldo inicial + despesas/receitas com fatura até o vencimento de F
/// (qualquer data) + transferências (pagamentos) com data até hoje; lançamentos anteriores a <c>saldo_inicial_em</c> ficam fora,
/// como no saldo. Tudo é SUM agrupado no banco, sob os filtros SoftDelete e Casal; nada é decifrado.
/// </summary>
internal sealed class ConsultaDeFaturas(LancamentosDbContext db, IConsultaDeContas contas, TimeProvider relogio) : IConsultaDeFaturas
{
    public async Task<IReadOnlyList<FaturasDoCartao>> AVencerAsync(CancellationToken ct = default)
    {
        var hoje = Brasilia.Hoje(relogio);
        var resultado = new List<FaturasDoCartao>();
        foreach (var conta in await contas.ListarAsync(ct))
        {
            if (PeriodosDeFatura.De(contas, conta, hoje) is not var (aberta, fechada)) continue;

            var inicio = conta.SaldoInicialEm;
            var grupos = await db.Transacoes.AsNoTracking()
                .Where(t => t.ContaId == conta.Id && t.Data >= inicio)
                .GroupBy(t => new
                {
                    Transferencia = t.Natureza == NaturezaDaTransacao.Transferencia,
                    t.FaturaVencimento,
                    AteHoje = t.Data <= hoje,
                })
                .Select(g => new { g.Key.Transferencia, g.Key.FaturaVencimento, g.Key.AteHoje, Soma = g.Sum(t => t.ValorCentavos) })
                .ToListAsync(ct);

            var devido = conta.SaldoInicialCentavos;
            long totalFechada = 0, totalAberta = 0;
            foreach (var g in grupos)
            {
                if (g.Transferencia)
                {
                    if (g.AteHoje) devido += g.Soma;
                    continue;
                }

                if (g.FaturaVencimento is not { } venc) continue;
                if (venc <= fechada.Vencimento) devido += g.Soma;
                if (venc == fechada.Vencimento) totalFechada -= g.Soma;
                if (venc == aberta.Vencimento && g.AteHoje) totalAberta -= g.Soma;
            }

            var restante = Math.Max(0, -devido);

            // Saldo inicial tomado depois do fechamento da fatura anterior à fechada pertence à fechada (não é atraso).
            // Só a dívida entra (saldo negativo); o total exibido continua sendo a soma dos lançamentos.
            var vencAnterior = contas.VencimentoDaFatura(conta, fechada.Fechamento.AddMonths(-1))!.Value;
            var fechAnterior = contas.FechamentoDaFatura(conta, vencAnterior)!.Value;
            var totalParaAtraso = totalFechada;
            if (conta.SaldoInicialEm > fechAnterior) totalParaAtraso += Math.Max(0, -conta.SaldoInicialCentavos);
            var atraso = Math.Min(restante, Math.Max(0, restante - Math.Max(0, totalParaAtraso)));
            resultado.Add(new FaturasDoCartao(
                conta.Id,
                new FaturaFechadaAPagar(fechada.Vencimento, fechada.Fechamento, totalFechada, restante, atraso),
                new FaturaAbertaAteHoje(aberta.Vencimento, aberta.Fechamento, totalAberta)));
        }

        return resultado;
    }
}
