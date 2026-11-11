namespace GestorFinanceiro.Modules.Integracoes.Pluggy;

/// <summary>Chaves de um slot (D23). O <c>ToString</c> nunca mostra o id nem o segredo.</summary>
internal sealed record CredenciaisPluggy(int Slot, string ClientId, string ClientSecret)
{
    public override string ToString() => $"CredenciaisPluggy {{ Slot = {Slot} }}";
}

internal sealed record ItemPluggy(string Id, string Status, string? ExecutionStatus, DateTimeOffset? ConsentimentoVenceEm, DateTimeOffset? AtualizadoEm);

/// <param name="Tipo">BANK ou CREDIT.</param>
/// <param name="Subtipo">CHECKING_ACCOUNT, SAVINGS_ACCOUNT, CREDIT_CARD…</param>
/// <param name="AgenciaEConta"><c>bankData.transferNumber</c> (ex.: "0001/12345-6"); só de conta bancária.</param>
/// <param name="CodigoDoBanco">
/// Código do banco (3 dígitos), do campo que a medição da Tarefa 1.0 apontar; nulo até lá e quando a Pluggy não trouxer.
/// </param>
/// <param name="SaldoCentavos"><c>balance</c> como a Pluggy manda (no cartão, o valor usado).</param>
internal sealed record ContaPluggy(string Id, string Tipo, string? Subtipo, string Nome, string? Numero, string? AgenciaEConta,
    string? CodigoDoBanco, long SaldoCentavos, long? LimiteCentavos, string? Portador, string? FinalDoCartao);

/// <param name="Data">Dia de São Paulo do instante recebido.</param>
/// <param name="ValorCentavos">Com o sinal que a Pluggy manda; o sinal do sistema é decidido na importação (etapa 4).</param>
/// <param name="Tipo">DEBIT ou CREDIT.</param>
/// <param name="Status">POSTED ou PENDING.</param>
internal sealed record TransacaoPluggy(string Id, DateOnly Data, long ValorCentavos, string Tipo, string Status,
    string? Descricao, string? CategoriaId, string? Categoria, int? Parcela, int? Parcelas, string? FaturaId);

/// <param name="Vencimento">Dia do calendário de <c>dueDate</c> (não convertido de fuso).</param>
internal sealed record FaturaPluggy(string Id, DateOnly Vencimento, DateOnly? Fechamento, long TotalCentavos);

internal sealed record InvestimentoPluggy(string Id, string Tipo, string? Subtipo, string Nome, long SaldoCentavos);

/// <summary>
/// Resposta de erro da Pluggy. Guarda só o status HTTP, o campo <c>code</c> e o <c>Retry-After</c>; nunca o corpo (D26).
/// </summary>
internal sealed class ErroDaPluggy(int status, string? codigo, TimeSpan? tentarDepois)
    : Exception($"A Pluggy respondeu {status}{(codigo is null ? "" : $" ({codigo})")}.")
{
    public int Status { get; } = status;
    public string? Codigo { get; } = codigo;
    public TimeSpan? TentarDepois { get; } = tentarDepois;
}
