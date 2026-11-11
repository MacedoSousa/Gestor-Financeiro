using System.Text.Json;
using GestorFinanceiro.Modules.Integracoes.Pluggy;

namespace GestorFinanceiro.UnitTests;

/// <summary>Valores da Pluggy viram centavos exatos (sem double) e datas viram o dia de São Paulo.</summary>
public class ConversaoPluggyTests
{
    private static JsonElement Numero(string json) => JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData("10.1", 1010)]
    [InlineData("0.07", 7)]
    [InlineData("-25.5", -2550)]
    [InlineData("1234.56", 123456)]
    [InlineData("0", 0)]
    [InlineData("3000", 300000)]
    [InlineData("1e2", 10000)]
    public void Valor_decimal_vira_centavos_exatos(string json, long esperado) =>
        Assert.Equal(esperado, ConversaoPluggy.Centavos(Numero(json)));

    [Theory]
    [InlineData("1234.565")]
    [InlineData("0.001")]
    public void Mais_de_duas_casas_e_formato_invalido(string json) =>
        Assert.Throws<FormatException>(() => ConversaoPluggy.Centavos(Numero(json)));

    [Theory]
    [InlineData("\"10.1\"")]
    [InlineData("null")]
    public void Valor_que_nao_e_numero_e_formato_invalido(string json) =>
        Assert.Throws<FormatException>(() => ConversaoPluggy.Centavos(Numero(json)));

    [Theory]
    [InlineData("2026-02-01T02:30:00.000Z", "2026-01-31")]
    [InlineData("2026-03-10T12:00:00.000Z", "2026-03-10")]
    [InlineData("2026-01-01T02:59:59.999Z", "2025-12-31")]
    [InlineData("2026-01-01T03:00:00.000Z", "2026-01-01")]
    [InlineData("2026-05-20T10:00:00-03:00", "2026-05-20")]
    public void Data_usa_o_dia_de_Sao_Paulo(string instante, string esperado) =>
        Assert.Equal(DateOnly.Parse(esperado, System.Globalization.CultureInfo.InvariantCulture), ConversaoPluggy.DiaDeSaoPaulo(instante));

    [Theory]
    [InlineData("2026-10-10T00:00:00.000Z", "2026-10-10")]
    [InlineData("2026-10-10", "2026-10-10")]
    public void Vencimento_e_dia_do_calendario_sem_fuso(string data, string esperado) =>
        Assert.Equal(DateOnly.Parse(esperado, System.Globalization.CultureInfo.InvariantCulture), ConversaoPluggy.DiaDoCalendario(data));

    [Theory]
    [InlineData("2026-02-30T00:00:00Z")]
    [InlineData("ontem")]
    [InlineData("")]
    public void Data_invalida_e_formato_invalido(string instante) =>
        Assert.Throws<FormatException>(() => ConversaoPluggy.DiaDeSaoPaulo(instante));
}
