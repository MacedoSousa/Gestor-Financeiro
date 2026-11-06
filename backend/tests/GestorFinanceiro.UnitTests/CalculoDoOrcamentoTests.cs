using GestorFinanceiro.Modules.Lancamentos.Contracts;
using GestorFinanceiro.Modules.Orcamento.Contracts;
using GestorFinanceiro.Modules.Orcamento.Dominio;

namespace GestorFinanceiro.UnitTests;

public class CalculoDoOrcamentoTests
{
    private static readonly MesDoOrcamento Mes = new(new DateOnly(2026, 11, 1));
    private static readonly Guid Inv = Guid.NewGuid(), Div = Guid.NewGuid(), Mer = Guid.NewGuid(), Laz = Guid.NewGuid(), Sau = Guid.NewGuid();

    private static List<CategoriaDeDespesa> Categorias() =>
    [
        new(Sau, "Saúde", null, false),
        new(Laz, "Lazer", null, false),
        new(Mer, "Mercado", null, false),
        new(Div, "Dívidas", PapelDeCategoria.Dividas, false),
        new(Inv, "Investimentos", PapelDeCategoria.Investimento, false),
    ];

    private static Dictionary<Guid, long> Tetos() => new()
    {
        [Inv] = 680_000, [Div] = 390_000, [Mer] = 160_000, [Laz] = 50_000,
    };

    private static OrcamentoDoMesDto Calc(
        long? receita = 1_850_000, List<CategoriaDeDespesa>? cats = null,
        Dictionary<Guid, long>? tetos = null, Dictionary<Guid, long>? uso = null, long aportes = 0) =>
        CalculoDoOrcamento.Calcular(Mes, receita, 0, cats ?? Categorias(), tetos ?? Tetos(), uso ?? [], aportes, null);

    [Fact]
    public void Fatias_percentuais_e_folga()
    {
        var o = Calc();
        Assert.Equal(210_000, o.Fatias.TetoDeGastos.TetoCentavos);
        Assert.Equal(570_000, o.FolgaCentavos);
        Assert.Equal(36.8m, o.Fatias.Investir.Percentual);
        Assert.Equal(21.1m, o.Fatias.Dividas.Percentual);
        Assert.Equal(11.4m, o.Fatias.TetoDeGastos.Percentual);
        Assert.Equal(["Investimentos", "Dívidas", "Lazer", "Mercado", "Saúde"], o.Categorias.Select(c => c.Nome));
    }

    [Fact]
    public void Teto_zero_e_diferente_de_sem_teto()
    {
        var tetos = Tetos(); tetos[Laz] = 0;
        var o = Calc(tetos: tetos, uso: new() { [Laz] = 7_000, [Sau] = 3_000 });
        var lazer = o.Categorias.Single(c => c.CategoriaId == Laz);
        Assert.Equal(0, lazer.TetoCentavos);
        Assert.Equal(-7_000, lazer.RestanteCentavos);
        var saude = o.Categorias.Single(c => c.CategoriaId == Sau);
        Assert.Null(saude.TetoCentavos);
        Assert.Null(saude.RestanteCentavos);
    }

    [Fact]
    public void Teto_de_gastos_soma_so_categorias_sem_papel()
    {
        var o = Calc(uso: new() { [Inv] = 100_000, [Div] = 50_000, [Mer] = 10_000, [Sau] = 5_000 });
        Assert.Equal(15_000, o.Fatias.TetoDeGastos.UsadoCentavos);
        Assert.Equal(100_000, o.Fatias.Investir.UsadoCentavos);
        Assert.Equal(50_000, o.Fatias.Dividas.UsadoCentavos);
    }

    [Fact]
    public void Folga_negativa()
    {
        Assert.Equal(-780_000, Calc(receita: 500_000).FolgaCentavos);
    }

    [Fact]
    public void Sem_receita_nao_ha_folga_nem_percentual()
    {
        var o = Calc(receita: null);
        Assert.Null(o.FolgaCentavos);
        Assert.Null(o.Fatias.Investir.Percentual);
        Assert.Null(o.Fatias.Dividas.Percentual);
        Assert.Null(o.Fatias.TetoDeGastos.Percentual);
        Assert.Null(Calc(receita: 0).Fatias.Investir.Percentual);
    }

    [Fact]
    public void Usado_negativo_aparece_como_esta()
    {
        var o = Calc(uso: new() { [Mer] = -2_500 });
        var mercado = o.Categorias.Single(c => c.CategoriaId == Mer);
        Assert.Equal(-2_500, mercado.UsadoCentavos);
        Assert.Equal(162_500, mercado.RestanteCentavos);
    }

    [Fact]
    public void Arquivada_so_aparece_com_teto_ou_uso()
    {
        var velha = Guid.NewGuid(); var comUso = Guid.NewGuid(); var comTeto = Guid.NewGuid();
        var cats = Categorias();
        cats.Add(new(velha, "Velha", null, true));
        cats.Add(new(comUso, "ComUso", null, true));
        cats.Add(new(comTeto, "ComTeto", null, true));
        var tetos = Tetos(); tetos[comTeto] = 0;
        var o = Calc(cats: cats, tetos: tetos, uso: new() { [comUso] = 100 });
        Assert.DoesNotContain(o.Categorias, c => c.CategoriaId == velha);
        Assert.Contains(o.Categorias, c => c.CategoriaId == comUso && c.Arquivada);
        Assert.Contains(o.Categorias, c => c.CategoriaId == comTeto);
    }

    [Fact]
    public void Fatia_sem_categoria_de_papel_mostra_so_os_aportes()
    {
        var cats = Categorias().Where(c => c.Papel is null).ToList();
        var o = Calc(cats: cats, aportes: 40_000);
        Assert.Null(o.Fatias.Investir.TetoCentavos);
        Assert.Equal(40_000, o.Fatias.Investir.UsadoCentavos);
        Assert.Equal(40_000, o.Fatias.Investir.AportesCentavos);
        Assert.Null(o.Fatias.Investir.Percentual);
        Assert.Null(o.Fatias.Dividas.TetoCentavos);
        Assert.Equal(0, o.Fatias.Dividas.UsadoCentavos);
        Assert.Equal(1_850_000 - 210_000, o.FolgaCentavos);
    }

    [Fact]
    public void Aportes_somam_ao_uso_da_categoria_de_investimento()
    {
        var o = Calc(uso: new() { [Inv] = 10_000 }, aportes: 40_000);
        Assert.Equal(50_000, o.Fatias.Investir.UsadoCentavos);
    }

    [Fact]
    public void Teto_de_gastos_sem_nenhum_teto_e_nulo()
    {
        var o = Calc(tetos: new() { [Inv] = 680_000, [Div] = 390_000 });
        Assert.Null(o.Fatias.TetoDeGastos.TetoCentavos);
        Assert.Null(o.Fatias.TetoDeGastos.Percentual);
        Assert.Equal(1_850_000 - 1_070_000, o.FolgaCentavos);
    }

    [Fact]
    public void Teto_de_gastos_com_um_teto_zero_e_zero()
    {
        var o = Calc(tetos: new() { [Laz] = 0 });
        Assert.Equal(0, o.Fatias.TetoDeGastos.TetoCentavos);
        Assert.Equal(0m, o.Fatias.TetoDeGastos.Percentual);
    }

    [Fact]
    public void Percentual_no_meio_arredonda_para_longe_do_zero()
    {
        var o = Calc(receita: 1_000_000, tetos: new() { [Mer] = 2_500 });
        Assert.Equal(0.3m, o.Fatias.TetoDeGastos.Percentual);
    }
}
