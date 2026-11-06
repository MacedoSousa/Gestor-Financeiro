using System.ComponentModel.DataAnnotations;

namespace GestorFinanceiro.Modules.Investimentos.Contracts;

/// <summary>Corpo de POST e PUT /api/v1/metas.</summary>
/// <param name="Nome">Aparado antes de validar e de comparar; 1 a 60 caracteres.</param>
/// <param name="ValorAlvoCentavos">Obrigatório: 1 a 100.000.000.000. Corpo sem o campo dá 400 (nunca vira zero).</param>
/// <param name="DataDesejada">Opcional, AAAA-MM, a partir do mês atual quando criada ou alterada.</param>
/// <param name="AportePlanejadoCentavos">Obrigatório: 0 a 100.000.000.000.</param>
/// <param name="RendimentoMensalPercentual">Obrigatório: % ao mês, 0 a 5, até 4 casas (0.85 = 0,85%).</param>
/// <param name="ContaIds">Ao menos uma; no PUT substitui o conjunto.</param>
public sealed record NovaMetaDto(
    [property: Required] string? Nome,
    [property: Required] long? ValorAlvoCentavos,
    string? DataDesejada,
    [property: Required] long? AportePlanejadoCentavos,
    [property: Required] decimal? RendimentoMensalPercentual,
    [property: Required] IReadOnlyList<Guid>? ContaIds);

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
