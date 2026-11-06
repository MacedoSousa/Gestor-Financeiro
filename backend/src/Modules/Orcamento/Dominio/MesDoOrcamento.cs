using System.Globalization;

namespace GestorFinanceiro.Modules.Orcamento.Dominio;

internal readonly record struct MesDoOrcamento(DateOnly Inicio)
{
    private static readonly DateOnly Minimo = new(2020, 1, 1);

    public string Texto => Inicio.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public MesDoOrcamento Anterior() => new(Inicio.AddMonths(-1));

    public static bool TryLer(string? texto, DateOnly hoje, out MesDoOrcamento mes, out string? erro)
    {
        var limite = new DateOnly(hoje.Year, hoje.Month, 1).AddMonths(24);
        mes = default;
        if (texto is null)
        {
            mes = new MesDoOrcamento(new DateOnly(hoje.Year, hoje.Month, 1));
            erro = null;
            return true;
        }
        if (texto.Length == 7
            && DateOnly.TryParseExact(texto + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var inicio)
            && inicio >= Minimo && inicio <= limite)
        {
            mes = new MesDoOrcamento(inicio);
            erro = null;
            return true;
        }
        erro = $"Mês inválido: use AAAA-MM entre 2020-01 e {limite.ToString("yyyy-MM", CultureInfo.InvariantCulture)}.";
        return false;
    }
}
