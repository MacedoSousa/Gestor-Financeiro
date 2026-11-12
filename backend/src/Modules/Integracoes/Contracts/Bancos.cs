namespace GestorFinanceiro.Modules.Integracoes.Contracts;

/// <param name="Codigo">Código COMPE, 3 dígitos.</param>
public sealed record BancoDto(string Codigo, string Nome);

/// <summary>
/// Bancos que a pessoa escolhe ao cadastrar um item (decisão do CEO, D18). Fora da lista, "Outro": código de 3 dígitos e o
/// nome digitado. O código entra na identidade da conta física.
/// </summary>
public static class BancosConhecidos
{
    public static IReadOnlyList<BancoDto> Lista { get; } =
    [
        new("001", "Banco do Brasil"),
        new("033", "Santander"),
        new("077", "Inter"),
        new("104", "Caixa"),
        new("208", "BTG Pactual"),
        new("212", "Original"),
        new("237", "Bradesco"),
        new("260", "Nubank"),
        new("290", "PagBank"),
        new("323", "Mercado Pago"),
        new("336", "C6"),
        new("341", "Itaú"),
        new("380", "PicPay"),
        new("422", "Safra"),
        new("748", "Sicredi"),
        new("756", "Sicoob"),
    ];

    /// <summary>Nome do banco da lista; nulo se o código não está nela ("Outro").</summary>
    public static string? NomeDe(string codigo) => Lista.FirstOrDefault(b => b.Codigo == codigo)?.Nome;
}
