namespace GestorFinanceiro.Modules.Contas.Contracts;

/// <summary>O que outros módulos podem saber sobre as contas do casal. Respeita o filtro de casal.</summary>
public interface IConsultaDeContas
{
    /// <summary>Nulo quando a conta não existe ou é de outro casal.</summary>
    Task<ContaResumo?> ObterAsync(Guid contaId, CancellationToken ct = default);

    /// <summary>Todas as contas do casal, inclusive as arquivadas (o extrato precisa delas).</summary>
    Task<IReadOnlyList<ContaResumo>> ListarAsync(CancellationToken ct = default);

    /// <summary>Vencimento da fatura em que a compra cai; nulo se a conta não for cartão.</summary>
    DateOnly? VencimentoDaFatura(ContaResumo conta, DateOnly dataCompra);

    /// <summary>Data de fechamento da fatura que vence em <paramref name="vencimento"/>; nulo se a conta não for cartão.</summary>
    DateOnly? FechamentoDaFatura(ContaResumo conta, DateOnly vencimento);
}

/// <param name="Tipo">CORRENTE, POUPANCA, INVESTIMENTO, CARTAO ou DINHEIRO.</param>
/// <param name="TitularUsuarioId">Nulo = conta conjunta.</param>
/// <param name="Instituicao">Nome do banco ou corretora, em claro; nulo quando não informado.</param>
public sealed record ContaResumo(
    Guid Id, string Apelido, string Tipo, Guid? TitularUsuarioId, long SaldoInicialCentavos, DateOnly SaldoInicialEm,
    int? DiaFechamento, int? DiaVencimento, bool Arquivada = false, string? Instituicao = null);

/// <summary>
/// Quem sabe se a conta tem lançamentos (o módulo Lancamentos implementa). Contas só apaga conta sem uso;
/// sem nenhuma implementação registrada, a conta nunca está em uso.
/// </summary>
public interface IVerificadorDeUsoDaConta
{
    Task<bool> EstaEmUsoAsync(Guid contaId, CancellationToken ct);
}
