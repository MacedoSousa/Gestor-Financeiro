namespace GestorFinanceiro.Financas;

/// <summary>
/// Toda conta financeira do sistema mora aqui, em decimal e testada.
/// Dinheiro em centavos (long), taxa como fração ao mês (decimal). Sem dependências do projeto.
/// O Gemini nunca calcula: só explica o que este código devolve.
/// </summary>
public static class FinanceEngine
{
    private const int LimiteMeses = 1200;
    private const long LimiteAporte = 100_000_000_000;

    /// <summary>
    /// Meses até o saldo atingir o alvo: juros antes do aporte, aporte no fim do mês.
    /// O mês 1 recebe o aporte do primeiro mês (o que falta do mês corrente), os seguintes o aporte mensal.
    /// 0 se já alcançada; null se nunca chega ou passa de 1.200 meses.
    /// </summary>
    public static int? MesesAteMeta(long atualCentavos, long alvoCentavos, long aportePrimeiroMesCentavos, long aporteMensalCentavos, decimal taxaMensal)
    {
        if (atualCentavos >= alvoCentavos) return 0;
        if (aportePrimeiroMesCentavos <= 0 && aporteMensalCentavos <= 0 && (taxaMensal <= 0 || atualCentavos <= 0)) return null;

        var saldo = (decimal)atualCentavos;
        var meta = (decimal)alvoCentavos;

        for (var mes = 1; mes <= LimiteMeses; mes++)
        {
            saldo = saldo * (1 + taxaMensal) + (mes == 1 ? aportePrimeiroMesCentavos : aporteMensalCentavos);
            if (saldo >= meta) return mes;
        }
        return null;
    }

    /// <summary>
    /// Menor aporte mensal P (centavos) que leva a meta em até <paramref name="meses"/> meses,
    /// já contando o que foi aportado no mês corrente (o primeiro mês recebe max(0, P − já aportado)).
    /// Null se meses ≤ 0 ou se nem o limite de aporte basta.
    /// </summary>
    public static long? AporteNecessario(long atualCentavos, long alvoCentavos, int meses, decimal taxaMensal, long jaAportadoNoMesCentavos)
    {
        if (meses <= 0) return null;

        bool Basta(long p) =>
            MesesAteMeta(atualCentavos, alvoCentavos, Math.Max(0, p - jaAportadoNoMesCentavos), p, taxaMensal) is int n && n <= meses;

        if (!Basta(LimiteAporte)) return null;

        long baixo = 0, alto = LimiteAporte;
        while (baixo < alto)
        {
            var meio = baixo + (alto - baixo) / 2;
            if (Basta(meio)) alto = meio; else baixo = meio + 1;
        }
        return baixo;
    }

    /// <summary>Taxa de poupança do mês: quanto da receita foi investido.</summary>
    public static decimal TaxaDePoupanca(long receitaCentavos, long investidoCentavos) =>
        receitaCentavos <= 0 ? 0 : decimal.Round((decimal)investidoCentavos / receitaCentavos, 4);

    /// <summary>Melhor dia para comprar no cartão: o dia seguinte ao fechamento da fatura.</summary>
    public static DateOnly MelhorDiaDeCompra(DateOnly fechamento) => fechamento.AddDays(1);
}
