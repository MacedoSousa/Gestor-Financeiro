namespace GestorFinanceiro.Modules.Contas.Contracts;

/// <summary>Corpo de POST e PUT /api/v1/contas.</summary>
/// <param name="Tipo">CORRENTE, POUPANCA, CARTAO ou DINHEIRO. Não muda depois de criada.</param>
/// <param name="TitularUsuarioId">Cônjuge titular; nulo = conta conjunta.</param>
/// <param name="Agencia">No PUT: nulo mantém o que está guardado, texto vazio apaga.</param>
/// <param name="Numero">No PUT: nulo mantém o que está guardado, texto vazio apaga.</param>
/// <param name="DiaFechamento">Obrigatório (1 a 31) em cartão; proibido nos demais.</param>
/// <param name="DiaVencimento">Obrigatório (1 a 31) em cartão; proibido nos demais.</param>
/// <param name="Arquivada">Só vale no PUT; a conta nasce ativa.</param>
public sealed record NovaContaDto(
    string Tipo,
    string Apelido,
    string? Instituicao,
    Guid? TitularUsuarioId,
    string? Agencia,
    string? Numero,
    long SaldoInicialCentavos,
    DateOnly SaldoInicialEm,
    int? DiaFechamento,
    int? DiaVencimento,
    long? LimiteCentavos,
    bool Arquivada = false)
{
    // Agência e número em claro não vão para log nem para ToString.
    public override string ToString() => $"NovaContaDto {{ Tipo = {Tipo} }}";
}

public sealed record TitularDto(Guid UsuarioId, string Apelido);

/// <summary>Conta como a API devolve: agência e número só mascarados.</summary>
public sealed record ContaDto(
    Guid Id,
    string Tipo,
    string Apelido,
    string? Instituicao,
    TitularDto? Titular,
    string? AgenciaMascarada,
    string? NumeroMascarado,
    long SaldoInicialCentavos,
    DateOnly SaldoInicialEm,
    int? DiaFechamento,
    int? DiaVencimento,
    long? LimiteCentavos,
    bool Arquivada);
