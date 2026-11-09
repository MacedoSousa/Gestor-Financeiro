using GestorFinanceiro.Financas;

namespace GestorFinanceiro.UnitTests;

public class FinanceEngineTests
{
    private const decimal Taxa = 0.0085m;

    // Laço de referência, em decimal e centavos: juros antes do aporte.
    private static int Referencia(long atual, long alvo, long primeiro, long mensal, decimal taxa)
    {
        var saldo = (decimal)atual;
        for (var n = 1; n <= 1200; n++)
        {
            saldo = saldo * (1 + taxa) + (n == 1 ? primeiro : mensal);
            if (saldo >= alvo) return n;
        }
        throw new InvalidOperationException("não chega");
    }

    [Theory]
    [InlineData(6_500_000, 30_000_000, 500_000, 37)]   // casa: plano atual
    [InlineData(6_500_000, 30_000_000, 600_000, 32)]
    [InlineData(6_686_000, 30_000_000, 500_000, 36)]
    [InlineData(3_840_000, 8_000_000, 180_000, 19)]    // carro
    public void Casos_historicos_continuam_iguais(long atual, long alvo, long aporte, int meses) =>
        Assert.Equal(meses, FinanceEngine.MesesAteMeta(atual, alvo, aporte, aporte, Taxa));

    [Fact]
    public void Primeiro_mes_nada_aportado() =>
        Assert.Equal(Referencia(6_500_000, 30_000_000, 500_000, 500_000, Taxa),
            FinanceEngine.MesesAteMeta(6_500_000, 30_000_000, 500_000, 500_000, Taxa));

    [Fact]
    public void Primeiro_mes_aporte_parcial() =>
        Assert.Equal(Referencia(6_500_000, 30_000_000, 300_000, 500_000, Taxa),
            FinanceEngine.MesesAteMeta(6_500_000, 30_000_000, 300_000, 500_000, Taxa));

    [Fact]
    public void Primeiro_mes_aporte_completo() =>
        Assert.Equal(Referencia(6_500_000, 30_000_000, 0, 500_000, Taxa),
            FinanceEngine.MesesAteMeta(6_500_000, 30_000_000, 0, 500_000, Taxa));

    [Fact]
    public void Primeiro_mes_menor_nao_chega_antes()
    {
        var cheio = FinanceEngine.MesesAteMeta(6_500_000, 30_000_000, 500_000, 500_000, Taxa);
        var zero = FinanceEngine.MesesAteMeta(6_500_000, 30_000_000, 0, 500_000, Taxa);
        Assert.True(zero >= cheio);
    }

    [Fact]
    public void Ja_alcancada_da_zero() =>
        Assert.Equal(0, FinanceEngine.MesesAteMeta(1_000, 1_000, 0, 0, Taxa));

    [Fact]
    public void Sem_aporte_e_sem_taxa_nunca_chega() =>
        Assert.Null(FinanceEngine.MesesAteMeta(0, 1_000, 0, 0, 0m));

    [Fact]
    public void Sem_aporte_e_sem_saldo_nunca_chega() =>
        Assert.Null(FinanceEngine.MesesAteMeta(0, 1_000, 0, 0, Taxa));

    [Fact]
    public void Taxa_zero_so_aportes() =>
        Assert.Equal(10, FinanceEngine.MesesAteMeta(0, 1_000, 100, 100, 0m));

    [Fact]
    public void Mais_de_1200_meses_e_nulo() =>
        Assert.Null(FinanceEngine.MesesAteMeta(0, 1_000_000, 1, 1, 0m));

    [Theory]
    [InlineData(6_500_000, 30_000_000, 36, 0, 0)]
    [InlineData(6_500_000, 30_000_000, 36, 0, 200_000)]
    [InlineData(6_500_000, 30_000_000, 12, 0, 200_000)]
    [InlineData(0, 1_000_000, 10, 0, 0)]
    public void AporteNecessario_ida_e_volta(long atual, long alvo, int meses, decimal taxaExtra, long jaAportado)
    {
        var taxa = Taxa + taxaExtra;
        var p = FinanceEngine.AporteNecessario(atual, alvo, meses, taxa, jaAportado);
        Assert.NotNull(p);
        Assert.True(p > 0);
        Assert.True(FinanceEngine.MesesAteMeta(atual, alvo, Math.Max(0, p.Value - jaAportado), p.Value, taxa) <= meses);
        var anterior = FinanceEngine.MesesAteMeta(atual, alvo, Math.Max(0, p.Value - 1 - jaAportado), p.Value - 1, taxa);
        Assert.True((anterior ?? int.MaxValue) > meses);
    }

    [Fact]
    public void AporteNecessario_meses_zero_e_nulo() =>
        Assert.Null(FinanceEngine.AporteNecessario(0, 1_000, 0, Taxa, 0));

    [Fact]
    public void AporteNecessario_meses_negativo_e_nulo() =>
        Assert.Null(FinanceEngine.AporteNecessario(0, 1_000, -3, Taxa, 0));

    [Fact]
    public void AporteNecessario_ja_alcancada_e_zero() =>
        Assert.Equal(0L, FinanceEngine.AporteNecessario(2_000, 1_000, 12, Taxa, 0));

    [Fact]
    public void AporteNecessario_alem_do_limite_e_nulo() =>
        Assert.Null(FinanceEngine.AporteNecessario(0, 100_000_000_001, 1, 0m, 0));

    [Fact]
    public void Taxa_de_poupanca_de_outubro() =>
        Assert.Equal(0.3676m, FinanceEngine.TaxaDePoupanca(1_850_000, 680_000));

    [Fact]
    public void Taxa_de_poupanca_sem_receita_e_zero() =>
        Assert.Equal(0m, FinanceEngine.TaxaDePoupanca(0, 100));

    [Fact]
    public void Melhor_dia_de_compra_e_o_dia_seguinte_ao_fechamento() =>
        Assert.Equal(new DateOnly(2026, 3, 1), FinanceEngine.MelhorDiaDeCompra(new DateOnly(2026, 2, 28)));
}
