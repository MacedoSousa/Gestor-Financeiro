namespace GestorFinanceiro.Modules.Integracoes.Pluggy;

/// <summary>Chaves de um slot (D23). O <c>ToString</c> nunca mostra o id nem o segredo.</summary>
internal sealed record CredenciaisPluggy(int Slot, string ClientId, string ClientSecret)
{
    public override string ToString() => $"CredenciaisPluggy {{ Slot = {Slot} }}";
}

internal sealed record ItemPluggy(string Id, string Status, string? ExecutionStatus, DateTimeOffset? ConsentimentoVenceEm, DateTimeOffset? AtualizadoEm);

/// <param name="Tipo">BANK ou CREDIT.</param>
/// <param name="Subtipo">CHECKING_ACCOUNT, SAVINGS_ACCOUNT, CREDIT_CARD…</param>
/// <param name="AgenciaEConta">
/// <c>bankData.transferNumber</c> (ex.: "0001/12345-6"); só de conta bancária. A Pluggy não traz o código do banco (medição de
/// 11/11): a trava de conta repetida usa este campo normalizado (<see cref="ConversaoPluggy.TransferNumberNormalizado"/>).
/// </param>
/// <param name="SaldoCentavos"><c>balance</c> como a Pluggy manda (no cartão, o valor usado).</param>
internal sealed record ContaPluggy(string Id, string Tipo, string? Subtipo, string Nome, string? Numero, string? AgenciaEConta,
    long SaldoCentavos, long? LimiteCentavos, string? Portador, string? FinalDoCartao);

/// <param name="Data">Dia de São Paulo do instante recebido.</param>
/// <param name="ValorCentavos">Com o sinal que a Pluggy manda; o sinal do sistema é decidido na importação (etapa 4).</param>
/// <param name="Tipo">DEBIT ou CREDIT.</param>
/// <param name="Status">POSTED ou PENDING.</param>
internal sealed record TransacaoPluggy(string Id, DateOnly Data, long ValorCentavos, string Tipo, string Status,
    string? Descricao, string? CategoriaId, string? Categoria, int? Parcela, int? Parcelas, string? FaturaId);

/// <param name="Vencimento">Dia do calendário de <c>dueDate</c> (não convertido de fuso).</param>
internal sealed record FaturaPluggy(string Id, DateOnly Vencimento, DateOnly? Fechamento, long TotalCentavos);

/// <param name="SaldoCentavos">
/// <c>balance</c> (valor líquido atual). Nulo quando o banco não mandou um valor que o sistema aceite: ausente, nulo, não
/// numérico ou com mais de 2 casas (D21, item 4). A aplicação continua listada.
/// </param>
/// <param name="Status">
/// <c>status</c> da Pluggy: ACTIVE (aplicada), PENDING (em validação) ou TOTAL_WITHDRAWAL (resgatada). Só ACTIVE conta no
/// saldo do grupo (D22). Nulo quando o banco não manda.
/// </param>
internal sealed record InvestimentoPluggy(string Id, string Tipo, string? Subtipo, string Nome, long? SaldoCentavos, string? Status = "ACTIVE")
{
    /// <summary>A situação no sistema (coluna <c>situacao_banco</c>): ATIVA, PENDENTE, RESGATADA ou DESCONHECIDA.</summary>
    public string Situacao => SituacaoDaAplicacao.De(Status);

    /// <summary>Só a aplicação ACTIVE entra no saldo do grupo (D22).</summary>
    public bool Ativa => Situacao == SituacaoDaAplicacao.Ativa;

    public bool Resgatada => Situacao == SituacaoDaAplicacao.Resgatada;
}

/// <summary>Status da aplicação na Pluggy traduzido para o sistema (D22).</summary>
internal static class SituacaoDaAplicacao
{
    public const string Ativa = "ATIVA";
    public const string Pendente = "PENDENTE";
    public const string Resgatada = "RESGATADA";
    public const string Desconhecida = "DESCONHECIDA";

    public static string De(string? status) => status switch
    {
        "ACTIVE" => Ativa,
        "PENDING" => Pendente,
        "TOTAL_WITHDRAWAL" => Resgatada,
        _ => Desconhecida,
    };
}

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
