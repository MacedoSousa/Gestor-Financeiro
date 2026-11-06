namespace GestorFinanceiro.Modules.Lancamentos.Contracts;

/// <param name="Tipo">DESPESA ou RECEITA.</param>
/// <param name="Arquivada">Arquivada continua na lista (o extrato mostra "(arquivada)"), mas não é oferecida para novos lançamentos.</param>
public sealed record CategoriaDto(Guid Id, string Nome, string Tipo, bool Arquivada);

/// <summary>Corpo de POST e PUT /api/v1/categorias.</summary>
/// <param name="Tipo">DESPESA ou RECEITA.</param>
/// <param name="Arquivada">Só vale no PUT: nulo mantém o estado atual. A categoria nasce ativa.</param>
public sealed record NovaCategoriaDto(string Nome, string Tipo, bool? Arquivada = null);

/// <summary>Total de uma fatura de cartão: despesas e estornos com aquele vencimento, positivo quando há gasto.</summary>
public sealed record FaturaDto(DateOnly Vencimento, DateOnly Fechamento, long TotalCentavos);

/// <summary>Saldo de uma conta; as faturas só vêm em cartão.</summary>
public sealed record SaldoDaContaDto(Guid ContaId, long SaldoCentavos, FaturaDto? FaturaAberta, FaturaDto? FaturaFechada);
