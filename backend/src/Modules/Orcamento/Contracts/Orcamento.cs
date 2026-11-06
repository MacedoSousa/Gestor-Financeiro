namespace GestorFinanceiro.Modules.Orcamento.Contracts;

/// <param name="Percentual">Teto sobre a receita prevista, com 1 casa; nulo sem receita, com receita 0 ou sem teto.</param>
public sealed record FatiaDto(long? TetoCentavos, long UsadoCentavos, decimal? Percentual);

public sealed record FatiaDeInvestirDto(long? TetoCentavos, long UsadoCentavos, long AportesCentavos, decimal? Percentual);

public sealed record FatiasDto(FatiaDeInvestirDto Investir, FatiaDto Dividas, FatiaDto TetoDeGastos);

public sealed record TetoDaCategoriaDto(
    Guid CategoriaId, string Nome, string? Papel, bool Arquivada,
    long? TetoCentavos, long UsadoCentavos, long? RestanteCentavos);

public sealed record OrcamentoDoMesDto(
    string Mes, long? ReceitaPrevistaCentavos, long ReceitaSugeridaCentavos, FatiasDto Fatias,
    long? FolgaCentavos, IReadOnlyList<TetoDaCategoriaDto> Categorias, string? MesParaCopiar);

/// <summary>Corpo dos dois PUT (teto e receita prevista).</summary>
public sealed record ValorDoMesDto(long ValorCentavos);

public sealed record CopiaDeTetosDto(int Copiados, string? Origem);
