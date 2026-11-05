using GestorFinanceiro.SharedKernel.Dominio;

namespace GestorFinanceiro.Modules.Assistente.Dominio;

/// <summary>
/// Toda conta financeira do sistema mora aqui, em decimal e testada.
/// O Gemini nunca calcula: só explica o que este código devolve.
/// </summary>
internal static class FinanceEngine
{
    private const int LimiteMeses = 1200;

    /// <summary>
    /// Meses até o saldo atingir o alvo, com aporte no fim de cada mês:
    /// FV = PV·(1+i)^n + PMT·((1+i)^n − 1)/i. Null se nunca chega (sem aporte e sem rendimento).
    /// </summary>
    public static int? MesesAteMeta(Dinheiro atual, Dinheiro alvo, Dinheiro aporteMensal, decimal taxaMensal)
    {
        if (atual >= alvo) return 0;
        if (aporteMensal.Centavos <= 0 && (taxaMensal <= 0 || atual.Centavos <= 0)) return null;

        var saldo = atual.EmReais;
        var meta = alvo.EmReais;
        var aporte = aporteMensal.EmReais;

        for (var mes = 1; mes <= LimiteMeses; mes++)
        {
            saldo = saldo * (1 + taxaMensal) + aporte;
            if (saldo >= meta) return mes;
        }
        return null;
    }

    /// <summary>Taxa de poupança do mês: quanto da receita foi investido.</summary>
    public static decimal TaxaDePoupanca(Dinheiro receita, Dinheiro investido) =>
        receita.Centavos <= 0 ? 0 : decimal.Round((decimal)investido.Centavos / receita.Centavos, 4);

    /// <summary>
    /// Melhor dia para comprar no cartão: o dia seguinte ao fechamento da fatura.
    /// </summary>
    public static DateOnly MelhorDiaDeCompra(DateOnly fechamento) => fechamento.AddDays(1);
}
