namespace GestorFinanceiro.Modules.Lancamentos.Contracts;

/// <param name="Papel">INVESTIMENTO, DIVIDAS ou nulo (<see cref="PapelDeCategoria"/>).</param>
public sealed record CategoriaDeDespesa(Guid Id, string Nome, string? Papel, bool Arquivada);

/// <summary>Categorias de despesa do casal para o orçamento.</summary>
public interface IConsultaDeCategorias
{
    /// <summary>Inclui as arquivadas, exclui as apagadas e cria as padrão do casal se ele ainda não tem nenhuma.</summary>
    Task<IReadOnlyList<CategoriaDeDespesa>> ListarDeDespesaAsync(CancellationToken ct = default);

    /// <summary>
    /// Verdadeiro só para categoria de receita do casal e não apagada. Serve para separar "categoria de receita" (400)
    /// de "categoria inexistente ou de outro casal" (404) quando um id não está em <see cref="ListarDeDespesaAsync"/>.
    /// </summary>
    Task<bool> EhDeReceitaAsync(Guid categoriaId, CancellationToken ct = default);
}

/// <param name="UsadoCentavos">Despesas menos estornos da categoria no mês; pode ser negativo se só houve estorno.</param>
public sealed record UsoDaCategoria(Guid CategoriaId, long UsadoCentavos);

/// <summary>
/// Quanto o casal usou no mês, somado no banco (nada é decifrado). <c>mes</c> é sempre o dia 1;
/// o período é <c>[mes, mes + 1 mês)</c> pela data do lançamento.
/// </summary>
public interface IConsultaDeUso
{
    /// <summary>Despesa entra negativa e estorno positivo no banco, então o estorno abate sozinho.</summary>
    Task<IReadOnlyList<UsoDaCategoria>> UsoPorCategoriaAsync(DateOnly mes, CancellationToken ct = default);

    /// <summary>Transferências de conta fora de poupança/investimento para dentro delas; resgates não abatem.</summary>
    Task<long> AportesAsync(DateOnly mes, CancellationToken ct = default);

    /// <summary>Receitas em categoria de receita; estorno (receita em categoria de despesa) não entra.</summary>
    Task<long> ReceitasAsync(DateOnly mes, CancellationToken ct = default);
}
