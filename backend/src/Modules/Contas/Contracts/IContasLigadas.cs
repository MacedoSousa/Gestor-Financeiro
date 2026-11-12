namespace GestorFinanceiro.Modules.Contas.Contracts;

/// <summary>
/// Pedido de conta ligada ao banco (Fase 2, D5 e D17). O <paramref name="ContaId"/> vem de quem chama (o vínculo do
/// Integracoes é gravado antes com ele): repetir o pedido com o mesmo id não cria outra conta.
/// </summary>
/// <param name="Tipo">CORRENTE, POUPANCA, INVESTIMENTO ou CARTAO.</param>
/// <param name="Instituicao">Nome do banco, para exibição (o digitado na tela do item).</param>
/// <param name="TitularUsuarioId">Nulo = conta conjunta.</param>
public sealed record NovaContaLigada(Guid ContaId, string Tipo, string Apelido, string? Instituicao, Guid? TitularUsuarioId,
    long SaldoInicialCentavos, DateOnly SaldoInicialEm, int? DiaFechamento, int? DiaVencimento, long? LimiteCentavos);

public abstract record ResultadoDaContaLigada
{
    /// <summary>A conta existe com esse id: criada agora ou já existia.</summary>
    public sealed record Pronta(Guid ContaId) : ResultadoDaContaLigada;

    /// <summary>Já existe conta com esse apelido no casal; <paramref name="Sugestao"/> é o primeiro "(N)" livre (D4).</summary>
    public sealed record ApelidoRepetido(string Sugestao) : ResultadoDaContaLigada;

    public sealed record Invalida(IReadOnlyDictionary<string, string[]> Erros) : ResultadoDaContaLigada;

    /// <summary>
    /// O id já pertence a uma conta que não é esta conta ligada: manual, apagada ou de outro casal. Nada foi gravado; quem
    /// chama escolhe outro id.
    /// </summary>
    public sealed record IdEmUso : ResultadoDaContaLigada;
}

/// <summary>
/// Escrita de contas ligadas, para o Integracoes (D17). Cada chamada é uma transação do Contas, no casal da sessão (ou
/// do job), e é idempotente: a retomada depois de uma falha não duplica nada.
/// </summary>
public interface IContasLigadas
{
    /// <summary>
    /// Cria a conta já ligada. Idempotente pelo <see cref="NovaContaLigada.ContaId"/>: devolve <c>Pronta</c> só se a conta com
    /// esse id existe no casal e é ligada; qualquer outro dono do id dá <c>IdEmUso</c>.
    /// </summary>
    Task<ResultadoDaContaLigada> CriarAsync(NovaContaLigada pedido, CancellationToken ct);

    /// <summary>Marca como ligada uma conta que já existe. Falso se ela não existe no casal.</summary>
    Task<bool> MarcarLigadaAsync(Guid contaId, CancellationToken ct);

    /// <summary>Saldo inicial calculado pela importação (D3). Só conta ligada.</summary>
    /// <exception cref="InvalidOperationException">A conta não existe no casal ou não é ligada.</exception>
    Task DefinirSaldoInicialAsync(Guid contaId, long centavos, DateOnly em, CancellationToken ct);
}
