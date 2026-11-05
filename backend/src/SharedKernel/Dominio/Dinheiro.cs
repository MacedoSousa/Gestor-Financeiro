using System.Globalization;

namespace GestorFinanceiro.SharedKernel.Dominio;

/// <summary>Valor monetário em centavos de real. Nunca use double/float para dinheiro.</summary>
public readonly record struct Dinheiro(long Centavos) : IComparable<Dinheiro>
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public static readonly Dinheiro Zero = new(0);

    public static Dinheiro DeReais(decimal reais) =>
        new((long)decimal.Round(reais * 100m, 0, MidpointRounding.AwayFromZero));

    public decimal EmReais => Centavos / 100m;

    public bool EhNegativo => Centavos < 0;

    public Dinheiro Absoluto() => new(Math.Abs(Centavos));

    public static Dinheiro operator +(Dinheiro a, Dinheiro b) => new(checked(a.Centavos + b.Centavos));
    public static Dinheiro operator -(Dinheiro a, Dinheiro b) => new(checked(a.Centavos - b.Centavos));
    public static Dinheiro operator -(Dinheiro a) => new(checked(-a.Centavos));
    public static bool operator <(Dinheiro a, Dinheiro b) => a.Centavos < b.Centavos;
    public static bool operator >(Dinheiro a, Dinheiro b) => a.Centavos > b.Centavos;
    public static bool operator <=(Dinheiro a, Dinheiro b) => a.Centavos <= b.Centavos;
    public static bool operator >=(Dinheiro a, Dinheiro b) => a.Centavos >= b.Centavos;

    public int CompareTo(Dinheiro other) => Centavos.CompareTo(other.Centavos);

    /// <summary>Formato brasileiro: "R$ 1.234,56" (negativo: "-R$ 1.234,56").</summary>
    public override string ToString() => EmReais.ToString("C2", PtBr);
}
