using GestorFinanceiro.Modules.Contas.Dominio;

namespace GestorFinanceiro.UnitTests;

public class CalendarioDaFaturaTests
{
    private static DateOnly Data(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("2026-11-05", 5, 12, "2026-11-12")]
    [InlineData("2026-11-06", 5, 12, "2026-12-12")]
    [InlineData("2026-02-28", 31, 10, "2026-03-10")]
    [InlineData("2026-03-01", 31, 10, "2026-04-10")]
    [InlineData("2026-12-20", 25, 5, "2027-01-05")]
    [InlineData("2026-12-26", 25, 5, "2027-02-05")]
    [InlineData("2026-01-30", 30, 30, "2026-02-28")]
    public void Vencimento_da_fatura_de_uma_compra(string compra, int fechamento, int vencimento, string esperado) =>
        Assert.Equal(Data(esperado), CalendarioDaFatura.VencimentoDaFatura(Data(compra), fechamento, vencimento));

    [Theory]
    [InlineData("2026-11-12", 5, 12, "2026-11-05")]
    [InlineData("2026-12-12", 5, 12, "2026-12-05")]
    [InlineData("2026-03-10", 31, 10, "2026-02-28")]
    [InlineData("2026-04-10", 31, 10, "2026-03-31")]
    [InlineData("2027-01-05", 25, 5, "2026-12-25")]
    [InlineData("2027-02-05", 25, 5, "2027-01-25")]
    [InlineData("2026-02-28", 30, 30, "2026-01-30")]
    public void Fechamento_da_fatura_e_o_inverso(string vencimentoDaFatura, int fechamento, int vencimento, string esperado) =>
        Assert.Equal(Data(esperado), CalendarioDaFatura.FechamentoDaFatura(Data(vencimentoDaFatura), fechamento, vencimento));

    [Fact]
    public void Compra_no_dia_do_fechamento_entra_na_fatura_que_fecha()
    {
        var venc = CalendarioDaFatura.VencimentoDaFatura(new DateOnly(2026, 11, 5), 5, 12);
        Assert.Equal(new DateOnly(2026, 11, 5), CalendarioDaFatura.FechamentoDaFatura(venc, 5, 12));
    }
}
