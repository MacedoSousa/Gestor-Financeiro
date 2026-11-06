namespace GestorFinanceiro.Modules.Investimentos.Contracts;

public sealed record NovaMetaDto(
    string? Nome,
    long? ValorAlvoCentavos,
    string? DataDesejada,
    long? AportePlanejadoCentavos,
    decimal? RendimentoMensalPercentual,
    IReadOnlyList<Guid>? ContaIds);

public sealed record ContaDaMetaDto(Guid ContaId, string Apelido, string Tipo, long SaldoCentavos, bool Arquivada);

public sealed record ProjecaoDto(
    long AporteMensalCentavos,
    string Situacao,
    int? MesesAteMeta,
    string? ChegaEm,
    int? DiferencaMeses,
    bool DataDesejadaPassou,
    long? AporteNecessarioCentavos);

public sealed record MetaDto(
    Guid Id,
    string Nome,
    long ValorAlvoCentavos,
    string? DataDesejada,
    long AportePlanejadoCentavos,
    decimal RendimentoMensalPercentual,
    IReadOnlyList<ContaDaMetaDto> Contas,
    long GuardadoCentavos,
    int ProgressoPercentual,
    long AporteDoMesCentavos,
    ProjecaoDto Projecao);

public sealed record ContaDeInvestimentoDto(
    Guid ContaId,
    string Apelido,
    string? Instituicao,
    string Tipo,
    bool Arquivada,
    long SaldoCentavos,
    Guid? MetaId,
    string? MetaNome);

public sealed record PainelDeMetasDto(
    string Mes,
    IReadOnlyList<MetaDto> Metas,
    IReadOnlyList<ContaDeInvestimentoDto> Contas);
