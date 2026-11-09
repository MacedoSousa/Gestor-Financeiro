using GestorFinanceiro.Financas;
using GestorFinanceiro.Modules.Investimentos.Dominio;

namespace GestorFinanceiro.UnitTests;

public class CalculoDaMetaTests
{
    private static readonly DateOnly Mes = new(2026, 11, 1);
    private const decimal Taxa = 0.0085m;

    [Fact]
    public void Progresso_arredonda_para_baixo() => Assert.Equal(99, CalculoDaMeta.Progresso(9_999, 10_000));

    [Fact]
    public void Progresso_limitado_a_100() => Assert.Equal(100, CalculoDaMeta.Progresso(15_000, 10_000));

    [Fact]
    public void Progresso_negativo_vira_zero() => Assert.Equal(0, CalculoDaMeta.Progresso(-500, 10_000));

    [Fact]
    public void Alcancada_so_com_guardado_maior_ou_igual()
    {
        var quase = CalculoDaMeta.Projetar(Mes, 9_999, 10_000, 1_000, 0, 0m, null);
        Assert.Equal("CHEGA", quase.Situacao);

        var feita = CalculoDaMeta.Projetar(Mes, 10_000, 10_000, 1_000, 0, 0m, new DateOnly(2027, 1, 1));
        Assert.Equal("ALCANCADA", feita.Situacao);
        Assert.Null(feita.ChegaEm);
        Assert.Null(feita.AporteNecessarioCentavos);
        Assert.False(feita.DataDesejadaPassou);
    }

    [Fact]
    public void Nao_chega_sem_aporte_nem_taxa()
    {
        var p = CalculoDaMeta.Projetar(Mes, 1_000, 10_000, 0, 0, 0m, null);
        Assert.Equal("NAO_CHEGA", p.Situacao);
        Assert.Null(p.MesesAteMeta);
        Assert.Null(p.ChegaEm);
    }

    [Fact]
    public void ChegaEm_conta_o_mes_corrente_como_primeiro()
    {
        var p = CalculoDaMeta.Projetar(Mes, 9_000, 10_000, 1_000, 0, 0m, null);
        Assert.Equal(1, p.MesesAteMeta);
        Assert.Equal("2026-11", p.ChegaEm);
    }

    [Fact]
    public void Data_no_mes_corrente_nao_passou()
    {
        var p = CalculoDaMeta.Projetar(Mes, 9_000, 10_000, 0, 0, 0m, new DateOnly(2026, 11, 1));
        Assert.False(p.DataDesejadaPassou);
        Assert.Equal(FinanceEngine.AporteNecessario(9_000, 10_000, 1, 0m, 0), p.AporteNecessarioCentavos);
        Assert.Equal(1_000, p.AporteNecessarioCentavos);
    }

    [Theory]
    [InlineData(6, "2027-04", -2)]
    [InlineData(8, "2027-06", 0)]
    [InlineData(11, "2027-09", 3)]
    public void Diferenca_antes_na_data_depois(int n, string chegaEm, int diferenca)
    {
        // Mês 2026-11 conta como 1º aporte: chega em M + (n-1).
        var guardado = 10_000L - n * 1_000L;
        var p = CalculoDaMeta.Projetar(Mes, guardado, 10_000, 1_000, 0, 0m, new DateOnly(2027, 6, 1));
        Assert.Equal(n, p.MesesAteMeta);
        Assert.Equal(chegaEm, p.ChegaEm);
        Assert.Equal(diferenca, p.DiferencaMeses);
        Assert.False(p.DataDesejadaPassou);
    }

    [Fact]
    public void Data_passada()
    {
        var p = CalculoDaMeta.Projetar(Mes, 1_000, 10_000, 500, 0, 0m, new DateOnly(2026, 10, 1));
        Assert.True(p.DataDesejadaPassou);
        Assert.Null(p.AporteNecessarioCentavos);
    }

    [Fact]
    public void Sem_data_so_chega_em()
    {
        var p = CalculoDaMeta.Projetar(Mes, 1_000, 10_000, 500, 0, 0m, null);
        Assert.NotNull(p.ChegaEm);
        Assert.Null(p.DiferencaMeses);
        Assert.Null(p.AporteNecessarioCentavos);
        Assert.False(p.DataDesejadaPassou);
    }

    [Fact]
    public void Aporte_do_mes_reduz_so_o_primeiro_mes()
    {
        var p = CalculoDaMeta.Projetar(Mes, 100_000, 5_000_000, 500_000, 200_000, Taxa, null);
        var esperado = FinanceEngine.MesesAteMeta(100_000, 5_000_000, 300_000, 500_000, Taxa);
        Assert.NotNull(esperado);
        Assert.Equal(esperado, p.MesesAteMeta);
    }

    [Fact]
    public void Aporte_do_mes_ja_feito_adia_o_primeiro_mes_com_valores_fixos()
    {
        // Falta 1.000; o aporte do mês (1.000) já está em "guardado" e reduz o 1º mês a 0: chega no mês seguinte.
        var p = CalculoDaMeta.Projetar(Mes, 9_000, 10_000, 1_000, 1_000, 0m, null);
        Assert.Equal(2, p.MesesAteMeta);
        Assert.Equal("2026-12", p.ChegaEm);
    }

    [Fact]
    public void Aporte_necessario_desconta_o_aporte_do_mes_ja_feito()
    {
        // 9.000 + P − 400 ≥ 10.000 em 1 mês => P = 1.400.
        var p = CalculoDaMeta.Projetar(Mes, 9_000, 10_000, 1_000, 400, 0m, new DateOnly(2026, 11, 1));
        Assert.Equal(1_400, p.AporteNecessarioCentavos);
    }

    [Fact]
    public void Percentual_e_fracao()
    {
        Assert.Equal(0.0085m, CalculoDaMeta.FracaoDePercentual(0.85m));
        Assert.Equal(0.85m, CalculoDaMeta.PercentualDeFracao(0.0085m));
        Assert.Equal(0.001234m, CalculoDaMeta.FracaoDePercentual(0.1234m));
    }
}
