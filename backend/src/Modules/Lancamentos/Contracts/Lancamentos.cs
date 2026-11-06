namespace GestorFinanceiro.Modules.Lancamentos.Contracts;

/// <param name="Tipo">DESPESA ou RECEITA.</param>
/// <param name="Arquivada">Arquivada continua na lista (o extrato mostra "(arquivada)"), mas não é oferecida para novos lançamentos.</param>
/// <param name="Papel">INVESTIMENTO, DIVIDAS ou nulo; só leitura (as categorias padrão nascem com papel).</param>
public sealed record CategoriaDto(Guid Id, string Nome, string Tipo, bool Arquivada, string? Papel);

/// <summary>Corpo de POST e PUT /api/v1/categorias.</summary>
/// <param name="Tipo">DESPESA ou RECEITA.</param>
/// <param name="Arquivada">Só vale no PUT: nulo mantém o estado atual. A categoria nasce ativa.</param>
public sealed record NovaCategoriaDto(string Nome, string Tipo, bool? Arquivada = null);

/// <summary>Total de uma fatura de cartão: despesas e estornos com aquele vencimento, positivo quando há gasto.</summary>
public sealed record FaturaDto(DateOnly Vencimento, DateOnly Fechamento, long TotalCentavos);

/// <summary>Saldo de uma conta; as faturas só vêm em cartão.</summary>
public sealed record SaldoDaContaDto(Guid ContaId, long SaldoCentavos, FaturaDto? FaturaAberta, FaturaDto? FaturaFechada);

/// <summary>Corpo de POST /api/v1/lancamentos. O casal vem da sessão.</summary>
/// <param name="ChaveIdempotente">UUID gerado pelo front a cada formulário aberto; repetir o POST devolve o lançamento já criado.</param>
/// <param name="Natureza">DESPESA ou RECEITA (transferência tem rota própria).</param>
/// <param name="ValorCentavos">Positivo; o sinal vem da natureza. Parcelado: o total da compra.</param>
/// <param name="Parcelas">1 a 24; mais de 1 só em cartão e em despesa.</param>
public sealed record NovoLancamentoDto(
    Guid ChaveIdempotente,
    Guid ContaId,
    string Natureza,
    long ValorCentavos,
    DateOnly Data,
    string Descricao,
    string? Contraparte,
    Guid CategoriaId,
    int Parcelas = 1)
{
    // Descrição e contraparte não vão para log nem para ToString.
    public override string ToString() => $"NovoLancamentoDto {{ ContaId = {ContaId}, Natureza = {Natureza}, Parcelas = {Parcelas} }}";
}

/// <summary>Corpo de PUT /api/v1/lancamentos/{id}: os campos do lançamento menos a conta e as parcelas.</summary>
/// <param name="Natureza">DESPESA ou RECEITA; TRANSFERENCIA só numa perna de transferência (que não muda de natureza).</param>
/// <param name="ValorCentavos">Positivo; em parcelado, o valor de cada parcela alterada; em transferência, o das duas pernas.</param>
/// <param name="Contraparte">Transferência não tem contraparte.</param>
/// <param name="CategoriaId">Obrigatória em despesa e receita; transferência não tem categoria.</param>
public sealed record EditarLancamentoDto(
    string Natureza,
    long ValorCentavos,
    DateOnly Data,
    string Descricao,
    string? Contraparte = null,
    Guid? CategoriaId = null)
{
    public override string ToString() => $"EditarLancamentoDto {{ Natureza = {Natureza} }}";
}

public sealed record CategoriaDoLancamentoDto(Guid Id, string Nome, bool Arquivada);

/// <param name="Tipo">CORRENTE, POUPANCA, CARTAO ou DINHEIRO.</param>
public sealed record ContaDoLancamentoDto(Guid Id, string Apelido, string Tipo);

/// <summary>Um lançamento como o extrato mostra.</summary>
/// <param name="Categoria">Nula em transferência (ou se a categoria não existe mais).</param>
/// <param name="Quem">Apelido do titular da conta, ou "Conjunta".</param>
/// <param name="Origem">MANUAL, OPEN_FINANCE ou WHATSAPP.</param>
/// <param name="Natureza">DESPESA, RECEITA ou TRANSFERENCIA.</param>
/// <param name="ValorCentavos">Com sinal: negativo é saída.</param>
public sealed record LancamentoDto(
    Guid Id,
    DateOnly Data,
    string Descricao,
    string? Contraparte,
    CategoriaDoLancamentoDto? Categoria,
    ContaDoLancamentoDto Conta,
    string Quem,
    string Origem,
    string Natureza,
    long ValorCentavos,
    int? Parcela,
    int? Parcelas,
    Guid? GrupoId,
    DateOnly? FaturaVencimento)
{
    public override string ToString() => $"LancamentoDto {{ Id = {Id} }}";
}

/// <summary>Resposta de GET /api/v1/lancamentos: os lançamentos do mês, por data decrescente.</summary>
public sealed record ExtratoDto(int Total, IReadOnlyList<LancamentoDto> Lancamentos);

/// <summary>Corpo de POST /api/v1/transferencias. Pagar a fatura é uma transferência com destino ao cartão.</summary>
/// <param name="ChaveIdempotente">UUID gerado pelo front a cada formulário aberto; repetir o POST devolve a transferência já criada.</param>
/// <param name="ValorCentavos">Positivo: sai da origem e entra no destino.</param>
public sealed record NovaTransferenciaDto(
    Guid ChaveIdempotente,
    Guid ContaOrigemId,
    Guid ContaDestinoId,
    long ValorCentavos,
    DateOnly Data,
    string Descricao)
{
    public override string ToString() => $"NovaTransferenciaDto {{ ContaOrigemId = {ContaOrigemId}, ContaDestinoId = {ContaDestinoId} }}";
}

/// <summary>As duas pernas de uma transferência, ligadas pelo mesmo grupo.</summary>
/// <param name="Saida">Perna negativa, na conta de origem.</param>
/// <param name="Entrada">Perna positiva, na conta de destino.</param>
public sealed record TransferenciaDto(Guid GrupoId, LancamentoDto Saida, LancamentoDto Entrada);
