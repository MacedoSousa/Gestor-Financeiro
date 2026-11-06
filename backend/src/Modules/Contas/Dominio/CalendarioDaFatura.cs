namespace GestorFinanceiro.Modules.Contas.Dominio;

/// <summary>
/// Calendário da fatura do cartão. Dias maiores que o mês (31 em fevereiro) valem o último dia do mês.
/// </summary>
internal static class CalendarioDaFatura
{
    /// <summary>
    /// Vencimento da fatura em que uma compra cai. Compra no dia do fechamento (ou antes) entra na fatura que fecha
    /// no mês; depois do fechamento, na do mês seguinte. O vencimento fica no mês do fechamento quando
    /// <paramref name="diaVencimento"/> é maior que <paramref name="diaFechamento"/>; senão, no mês seguinte.
    /// </summary>
    public static DateOnly VencimentoDaFatura(DateOnly dataCompra, int diaFechamento, int diaVencimento)
    {
        var mesDoFechamento = new DateOnly(dataCompra.Year, dataCompra.Month, 1);
        if (dataCompra.Day > Ajustar(mesDoFechamento, diaFechamento))
            mesDoFechamento = mesDoFechamento.AddMonths(1);

        var mesDoVencimento = diaVencimento > diaFechamento ? mesDoFechamento : mesDoFechamento.AddMonths(1);
        return new DateOnly(mesDoVencimento.Year, mesDoVencimento.Month, Ajustar(mesDoVencimento, diaVencimento));
    }

    /// <summary>O inverso: data de fechamento da fatura que vence em <paramref name="vencimento"/>.</summary>
    public static DateOnly FechamentoDaFatura(DateOnly vencimento, int diaFechamento, int diaVencimento)
    {
        var mes = new DateOnly(vencimento.Year, vencimento.Month, 1);
        var mesDoFechamento = diaVencimento > diaFechamento ? mes : mes.AddMonths(-1);
        return new DateOnly(mesDoFechamento.Year, mesDoFechamento.Month, Ajustar(mesDoFechamento, diaFechamento));
    }

    private static int Ajustar(DateOnly mes, int dia) => Math.Min(dia, DateTime.DaysInMonth(mes.Year, mes.Month));
}
