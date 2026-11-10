using GestorFinanceiro.Modules.Lancamentos.Contracts;

namespace GestorFinanceiro.Modules.Painel.Contracts;

/// <summary>Uma seção da Visão geral: ou vem com dados ou vem com o erro (texto fixo, nunca o da exceção).</summary>
public sealed record SecaoDto<T>(T? Dados, string? Erro);

/// <param name="Situacao">COM_TETO, ESGOTADO, PASSOU ou SEM_TETO.</param>
public sealed record AindaDaParaGastarDto(
    string Situacao, long? TetoCentavos, long UsadoCentavos, long? RestanteCentavos,
    long? PorDiaCentavos, int? PercentualUsado, DateOnly FimDoMes);

public sealed record ContaNoSaldoDto(Guid ContaId, string Apelido, long SaldoCentavos);

public sealed record SaldoDasContasDto(long TotalCentavos, IReadOnlyList<ContaNoSaldoDto> Contas, int OutrasContas);

public sealed record AporteDaMetaDto(Guid MetaId, string Nome, long AportePlanejadoCentavos);

public sealed record AportePlanejadoDto(long TotalCentavos, long JaAportadoCentavos, IReadOnlyList<AporteDaMetaDto> Metas);

public sealed record PatrimonioDto(long TotalCentavos, long? VariacaoCentavos, decimal? VariacaoPercentual);

public sealed record AvisoDeFaturaDto(Guid ContaId, string Texto);

/// <param name="Mes">AAAA-MM.</param>
/// <param name="Rotulo">Mês abreviado em minúsculas: jan, fev...</param>
public sealed record PontoDoPatrimonioDto(string Mes, string Rotulo, long? TotalCentavos);

public sealed record PatrimonioEm12MesesDto(string Intervalo, IReadOnlyList<PontoDoPatrimonioDto> Pontos);

/// <param name="Situacao">Situação da projeção da meta (CHEGA, ALCANCADA, NAO_CHEGA).</param>
/// <param name="ChegaEm">AAAA-MM, vindo da projeção.</param>
public sealed record MetaDoPainelDto(
    Guid Id, string Nome, int ProgressoPercentual, long GuardadoCentavos, long ValorAlvoCentavos,
    string Situacao, string? ChegaEm);

public sealed record MetasDoPainelDto(IReadOnlyList<MetaDoPainelDto> Metas, int Total);

/// <param name="Estado">NORMAL ou PASSOU.</param>
public sealed record CategoriaDoPainelDto(
    Guid CategoriaId, string Nome, long TetoCentavos, long UsadoCentavos, int PercentualUsado, string Estado);

public sealed record OrcamentoDoPainelDto(IReadOnlyList<CategoriaDoPainelDto> Categorias, int OutrasCategorias);

/// <param name="Estado">FECHADA, COM_ATRASO ou ABERTA.</param>
public sealed record VencimentoDto(
    Guid ContaId, string Apelido, DateOnly Vencimento, DateOnly Fechamento, string Estado,
    long ValorCentavos, long AtrasoCentavos);

public sealed record VisaoGeralDto(
    string Mes, DateOnly Hoje, int DiaDoMes, int DiasNoMes, string Apelido,
    SecaoDto<AindaDaParaGastarDto> AindaDaParaGastar, SecaoDto<SaldoDasContasDto> SaldoDasContas,
    SecaoDto<AportePlanejadoDto> AportePlanejado, SecaoDto<PatrimonioDto> Patrimonio,
    SecaoDto<IReadOnlyList<AvisoDeFaturaDto>> AvisosDeFatura, SecaoDto<PatrimonioEm12MesesDto> PatrimonioEm12Meses,
    SecaoDto<MetasDoPainelDto> Metas, SecaoDto<OrcamentoDoPainelDto> Orcamento,
    SecaoDto<IReadOnlyList<VencimentoDto>> Vencimentos, SecaoDto<IReadOnlyList<LancamentoDto>> UltimosLancamentos);
