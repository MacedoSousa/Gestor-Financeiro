using GestorFinanceiro.SharedKernel.Dominio;

namespace GestorFinanceiro.Modules.Orcamento.Dominio;

/// <summary>Quanto o casal planeja gastar numa categoria de despesa num mês. Zero é um teto (diferente de não ter).</summary>
internal sealed class Teto : Entidade, ICasalScoped
{
    public Guid CasalId { get; private set; }

    /// <summary>Sempre o dia 1 do mês.</summary>
    public DateOnly Mes { get; private set; }

    public Guid CategoriaId { get; private set; }
    public long ValorCentavos { get; private set; }

    private Teto() { }

    public static Teto Criar(Guid casalId, DateOnly mes, Guid categoriaId, long valorCentavos)
    {
        var teto = new Teto { CasalId = casalId, Mes = mes, CategoriaId = categoriaId };
        teto.Definir(valorCentavos);
        return teto;
    }

    public void Definir(long valorCentavos)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(valorCentavos);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(valorCentavos, LimitesDoOrcamento.ValorMaximo);
        ValorCentavos = valorCentavos;
    }
}
