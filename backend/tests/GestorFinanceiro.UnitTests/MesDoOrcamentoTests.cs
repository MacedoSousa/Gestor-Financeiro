using GestorFinanceiro.Modules.Orcamento.Dominio;

namespace GestorFinanceiro.UnitTests;

public class MesDoOrcamentoTests
{
    private static readonly DateOnly Hoje = new(2026, 11, 5);

    [Theory]
    [InlineData("2019-12", false)]
    [InlineData("2020-01", true)]
    [InlineData("2028-11", true)]
    [InlineData("2028-12", false)]
    [InlineData("2026-13", false)]
    [InlineData("nov", false)]
    public void Valida_o_intervalo(string texto, bool valido)
    {
        var ok = MesDoOrcamento.TryLer(texto, Hoje, out var mes, out var erro);
        Assert.Equal(valido, ok);
        if (valido) { Assert.Null(erro); Assert.Equal(texto, mes.Texto); }
        else Assert.Equal("Mês inválido: use AAAA-MM entre 2020-01 e 2028-11.", erro);
    }

    [Fact]
    public void Nulo_vira_o_mes_de_hoje()
    {
        Assert.True(MesDoOrcamento.TryLer(null, Hoje, out var mes, out _));
        Assert.Equal(new DateOnly(2026, 11, 1), mes.Inicio);
    }

    [Fact]
    public void Anterior_de_2020_01_e_2019_12_sem_validar()
    {
        var anterior = new MesDoOrcamento(new DateOnly(2020, 1, 1)).Anterior();
        Assert.Equal(new DateOnly(2019, 12, 1), anterior.Inicio);
    }
}
