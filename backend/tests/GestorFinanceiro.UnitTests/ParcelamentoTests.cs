using GestorFinanceiro.Modules.Lancamentos.Dominio;
using GestorFinanceiro.SharedKernel.Dominio;

namespace GestorFinanceiro.UnitTests;

public class ParcelamentoTests
{
    [Theory]
    [InlineData(10000, 3, new long[] { 3334, 3333, 3333 })]
    [InlineData(100, 3, new long[] { 34, 33, 33 })]
    [InlineData(900, 3, new long[] { 300, 300, 300 })]
    [InlineData(500, 1, new long[] { 500 })]
    public void Divide_com_a_sobra_na_primeira(long total, int parcelas, long[] esperado)
    {
        var partes = Parcelamento.Dividir(new Dinheiro(total), parcelas);
        Assert.Equal(esperado, partes.Select(p => p.Centavos));
        Assert.Equal(total, partes.Sum(p => p.Centavos));
    }

    [Fact]
    public void Parcela_de_zero_centavos_lanca() =>
        Assert.ThrowsAny<ArgumentException>(() => Parcelamento.Dividir(new Dinheiro(1), 2));

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(-1)]
    public void Parcelas_fora_de_1_a_24_lancam(int parcelas) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Parcelamento.Dividir(new Dinheiro(100_000), parcelas));

    [Fact]
    public void Total_nao_positivo_lanca() =>
        Assert.ThrowsAny<ArgumentException>(() => Parcelamento.Dividir(new Dinheiro(0), 2));
}
