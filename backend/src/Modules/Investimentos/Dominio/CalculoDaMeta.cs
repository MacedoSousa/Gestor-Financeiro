using System.Globalization;
using GestorFinanceiro.Financas;
using GestorFinanceiro.Modules.Investimentos.Contracts;

namespace GestorFinanceiro.Modules.Investimentos.Dominio;

/// <summary>
/// Projeção de uma meta sobre o FinanceEngine. Convenção: o fim do mês corrente M é o 1º aporte;
/// com n ≥ 1 a meta é atingida no fim de M + (n − 1).
/// </summary>
internal static class CalculoDaMeta
{
    public static ProjecaoDto Projetar(DateOnly mes, long guardado, long alvo, long aporteMensal, long aporteDoMes, decimal taxaMensal, DateOnly? dataDesejada)
    {
        var m = PrimeiroDia(mes);
        if (guardado >= alvo)
            return new ProjecaoDto(aporteMensal, "ALCANCADA", 0, null, null, false, null);

        var primeiroMes = Math.Max(0, aporteMensal - aporteDoMes);
        var meses = FinanceEngine.MesesAteMeta(guardado, alvo, primeiroMes, aporteMensal, taxaMensal);

        string? chegaEm = null;
        int? diferenca = null;
        DateOnly? d = dataDesejada is { } dd ? PrimeiroDia(dd) : null;
        if (meses is { } n)
        {
            var chega = m.AddMonths(n - 1);
            chegaEm = Formatar(chega);
            if (d is { } alvoData) diferenca = DiferencaEmMeses(chega, alvoData);
        }

        var passou = d is { } dp && dp < m;
        long? necessario = null;
        if (d is { } dn && !passou)
            necessario = FinanceEngine.AporteNecessario(guardado, alvo, DiferencaEmMeses(dn, m) + 1, taxaMensal, aporteDoMes);

        return new ProjecaoDto(aporteMensal, meses is null ? "NAO_CHEGA" : "CHEGA", meses, chegaEm, diferenca, passou, necessario);
    }

    public static int Progresso(long guardado, long alvo)
    {
        if (alvo <= 0 || guardado <= 0) return 0;
        var p = (long)((System.Int128)guardado * 100 / alvo);
        return (int)Math.Clamp(p, 0, 100);
    }

    public static decimal FracaoDePercentual(decimal percentual) => percentual / 100m;

    public static decimal PercentualDeFracao(decimal fracao) => Math.Round(fracao * 100m, 4);

    private static DateOnly PrimeiroDia(DateOnly d) => new(d.Year, d.Month, 1);

    private static int DiferencaEmMeses(DateOnly a, DateOnly b) => (a.Year - b.Year) * 12 + a.Month - b.Month;

    private static string Formatar(DateOnly d) => d.ToString("yyyy-MM", CultureInfo.InvariantCulture);
}
