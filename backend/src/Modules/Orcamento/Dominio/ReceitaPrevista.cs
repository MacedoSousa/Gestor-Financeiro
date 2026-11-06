using GestorFinanceiro.SharedKernel.Dominio;

namespace GestorFinanceiro.Modules.Orcamento.Dominio;

/// <summary>Quanto o casal espera receber no mês; base dos percentuais e da folga.</summary>
internal sealed class ReceitaPrevista : Entidade, ICasalScoped
{
    public Guid CasalId { get; private set; }

    /// <summary>Sempre o dia 1 do mês.</summary>
    public DateOnly Mes { get; private set; }

    public long ValorCentavos { get; private set; }

    private ReceitaPrevista() { }

    public static ReceitaPrevista Criar(Guid casalId, DateOnly mes, long valorCentavos)
    {
        var receita = new ReceitaPrevista { CasalId = casalId, Mes = mes };
        receita.Definir(valorCentavos);
        return receita;
    }

    public void Definir(long valorCentavos)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(valorCentavos);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(valorCentavos, LimitesDoOrcamento.ValorMaximo);
        ValorCentavos = valorCentavos;
    }
}

internal static class LimitesDoOrcamento
{
    /// <summary>R$ 1 bilhão, o mesmo CHECK da V0008.</summary>
    public const long ValorMaximo = 100_000_000_000;
}
