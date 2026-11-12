namespace GestorFinanceiro.Modules.Integracoes.Pluggy;

/// <param name="TotalDoBancoCentavos">Total da fatura informado pelo banco.</param>
/// <param name="SomaDasTransacoesCentavos">Soma das transações com o billId da fatura, no sinal da Pluggy (compra positiva).</param>
internal sealed record DiferencaDeFatura(string FaturaId, DateOnly Vencimento, long TotalDoBancoCentavos, long SomaDasTransacoesCentavos)
{
    public long DiferencaCentavos => TotalDoBancoCentavos - SomaDasTransacoesCentavos;
}

/// <summary>
/// Conferência do cartão por fatura (CEO 11/11 21h, decisão 12; substitui a fórmula do saldo da D3, que a medição mostrou
/// não fechar): para cada fatura do banco, o total informado contra a soma das transações com aquele billId.
/// Pendentes (sem billId) e transações de fatura que o banco não listou ficam fora. Só é conferida a fatura que vence
/// a partir de dois meses depois do início do histórico: antes disso ela pode ter compras de antes do histórico, que
/// não foram importadas. A regra das duas sincronizações seguidas fica com quem chama (Tarefa 4.5).
/// </summary>
internal static class ConferenciaDasFaturas
{
    public static IReadOnlyList<DiferencaDeFatura> Diferencas(
        IEnumerable<FaturaPluggy> faturas, IEnumerable<TransacaoPluggy> transacoes, DateOnly historicoDesde)
    {
        var somas = transacoes
            .Where(t => t.FaturaId is not null)
            .GroupBy(t => t.FaturaId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(t => t.ValorCentavos), StringComparer.Ordinal);
        var primeiraConferida = historicoDesde.AddMonths(2);

        return faturas
            .Where(f => f.Vencimento >= primeiraConferida)
            .Select(f => new DiferencaDeFatura(f.Id, f.Vencimento, f.TotalCentavos, somas.GetValueOrDefault(f.Id)))
            .Where(d => d.DiferencaCentavos != 0)
            .OrderBy(d => d.Vencimento)
            .ToList();
    }
}
