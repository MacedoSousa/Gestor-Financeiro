using GestorFinanceiro.SharedKernel.Dominio;

namespace GestorFinanceiro.Modules.Lancamentos.Dominio;

/// <summary>Divisão de uma compra em parcelas, sem juros, em centavos exatos.</summary>
internal static class Parcelamento
{
    internal const int MaximoDeParcelas = 24;

    /// <summary>A soma é exata; a sobra de centavos vai para a primeira parcela.</summary>
    public static IReadOnlyList<Dinheiro> Dividir(Dinheiro total, int parcelas)
    {
        if (parcelas is < 1 or > MaximoDeParcelas)
            throw new ArgumentOutOfRangeException(nameof(parcelas), "Parcelas devem ser de 1 a 24.");
        if (total.Centavos <= 0)
            throw new ArgumentException("O total a parcelar deve ser positivo.", nameof(total));

        var basico = total.Centavos / parcelas;
        if (basico == 0)
            throw new ArgumentException("Parcela de zero centavos é inválida.", nameof(total));
        var sobra = total.Centavos - basico * parcelas;

        var partes = new Dinheiro[parcelas];
        for (var i = 0; i < parcelas; i++)
            partes[i] = new Dinheiro(i == 0 ? basico + sobra : basico);
        return partes;
    }
}
