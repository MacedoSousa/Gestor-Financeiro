using GestorFinanceiro.Modules.Lancamentos.Contracts;

namespace GestorFinanceiro.UnitTests;

/// <summary>
/// Códigos de categoria da Pluggy que o reconhecimento automático usa (medição de 11/11; CEO 11/11 21h, decisão 13).
/// Só o código exato conta; subcategoria não listada e transação sem categoria não têm papel ("A conferir" é a reserva).
/// </summary>
public class CategoriasDoBancoTests
{
    [Theory]
    [InlineData("05100000", PapelDaCategoriaDoBanco.PagamentoDeFatura)]   // Credit card payment
    [InlineData("03000000", PapelDaCategoriaDoBanco.Aplicacao)]           // Investments
    [InlineData("03020000", PapelDaCategoriaDoBanco.Aplicacao)]           // Fixed income
    [InlineData("03060000", PapelDaCategoriaDoBanco.ResgateOuRendimento)] // Proceeds interests and dividends
    [InlineData("04000000", PapelDaCategoriaDoBanco.MesmaTitularidade)]   // Same person transfer
    [InlineData("05010000", PapelDaCategoriaDoBanco.Boleto)]              // Transfer - Bank Slip
    [InlineData("15030000", PapelDaCategoriaDoBanco.Iof)]                 // Tax on financial operations
    [InlineData("15010000", PapelDaCategoriaDoBanco.ImpostoDeRenda)]      // Income taxes
    public void Codigo_medido_tem_papel(string codigo, PapelDaCategoriaDoBanco esperado) =>
        Assert.Equal(esperado, CategoriasDoBanco.Papel(codigo));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("01000000")] // Groceries
    [InlineData("03010000")] // outra subcategoria de Investments, não medida
    [InlineData("0510000")]
    [InlineData(" 05100000")]
    public void Sem_categoria_ou_codigo_nao_listado_nao_tem_papel(string? codigo) =>
        Assert.Equal(PapelDaCategoriaDoBanco.Nenhum, CategoriasDoBanco.Papel(codigo));

    [Fact]
    public void Cada_papel_tem_ao_menos_um_codigo()
    {
        var comCodigo = CategoriasDoBanco.Codigos.Values.ToHashSet();
        var papeis = Enum.GetValues<PapelDaCategoriaDoBanco>().Where(p => p != PapelDaCategoriaDoBanco.Nenhum);

        Assert.All(papeis, p => Assert.Contains(p, comCodigo));
    }

    /// <summary>D23: fatura, mesma titularidade e investimento esperam a etapa 6; boleto, IOF, IR e o resto, não.</summary>
    [Theory]
    [InlineData("05100000", true)]
    [InlineData("04000000", true)]
    [InlineData("03000000", true)]
    [InlineData("03020000", true)]
    [InlineData("03060000", true)]
    [InlineData("05010000", false)]
    [InlineData("15030000", false)]
    [InlineData("15010000", false)]
    [InlineData("01010000", false)]
    [InlineData(null, false)]
    public void Reconhecido_na_proxima_etapa(string? codigo, bool esperado) =>
        Assert.Equal(esperado, CategoriasDoBanco.ReconhecidoNaProximaEtapa(codigo));
}
