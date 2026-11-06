using GestorFinanceiro.SharedKernel.Dominio;

namespace GestorFinanceiro.Modules.Investimentos.Dominio;

internal static class LimitesDaMeta
{
    public const int NomeMaximo = 60;
    public const long ValorMaximo = 100_000_000_000;
    public const decimal TaxaMaxima = 0.05m;
}

/// <summary>
/// Meta do casal (casa, carro…): valor alvo, data desejada opcional (dia 1), aporte planejado e rendimento líquido
/// estimado como fração ao mês. As contas da meta ficam em <see cref="MetaConta"/>.
/// </summary>
internal sealed class Meta : Entidade, ICasalScoped
{
    public Guid CasalId { get; private set; }
    public string Nome { get; private set; } = "";
    public long ValorAlvoCentavos { get; private set; }
    public DateOnly? DataDesejada { get; private set; }
    public long AportePlanejadoCentavos { get; private set; }

    /// <summary>Fração ao mês (0,85% = 0.0085), de 0 a 0.05.</summary>
    public decimal TaxaMensal { get; private set; }

    private Meta() { }

    public static Meta Criar(Guid casalId, string nome, long alvo, DateOnly? dataDesejada, long aportePlanejado, decimal taxaMensal)
    {
        var meta = new Meta { CasalId = casalId };
        meta.Definir(nome, alvo, dataDesejada, aportePlanejado, taxaMensal);
        return meta;
    }

    public void Definir(string nome, long alvo, DateOnly? dataDesejada, long aportePlanejado, decimal taxaMensal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nome);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nome.Length, LimitesDaMeta.NomeMaximo);
        if (nome != nome.Trim()) throw new ArgumentException("Nome não aparado.", nameof(nome));
        ArgumentOutOfRangeException.ThrowIfLessThan(alvo, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(alvo, LimitesDaMeta.ValorMaximo);
        if (dataDesejada is { Day: not 1 }) throw new ArgumentException("Data desejada fora do dia 1.", nameof(dataDesejada));
        ArgumentOutOfRangeException.ThrowIfNegative(aportePlanejado);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(aportePlanejado, LimitesDaMeta.ValorMaximo);
        ArgumentOutOfRangeException.ThrowIfNegative(taxaMensal);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(taxaMensal, LimitesDaMeta.TaxaMaxima);

        Nome = nome;
        ValorAlvoCentavos = alvo;
        DataDesejada = dataDesejada;
        AportePlanejadoCentavos = aportePlanejado;
        TaxaMensal = taxaMensal;
    }
}
