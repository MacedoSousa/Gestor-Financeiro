using GestorFinanceiro.SharedKernel.Dominio;
using GestorFinanceiro.SharedKernel.Seguranca;

namespace GestorFinanceiro.Modules.Integracoes.Dominio;

/// <summary>Estados de um item (coluna <c>estado</c>).</summary>
internal static class EstadoDoItem
{
    public const string Ativo = "ATIVO";
    public const string ConsentimentoVencendo = "CONSENTIMENTO_VENCENDO";
    public const string ConsentimentoVencido = "CONSENTIMENTO_VENCIDO";
    public const string Erro = "ERRO";

    /// <summary>Aviso de consentimento: 15 dias antes de <c>consentExpiresAt</c> (spec, seção 6).</summary>
    public static readonly TimeSpan AvisoDeConsentimento = TimeSpan.FromDays(15);

    /// <summary>Status da Pluggy em que o item está bem: atualizado ou atualizando.</summary>
    private static readonly HashSet<string> StatusBons = new(StringComparer.Ordinal) { "UPDATED", "UPDATING" };

    /// <summary>
    /// Estado e motivo a partir do que o banco disse do item. Ordem: consentimento vencido, erro do item, consentimento
    /// vencendo, ativo. O motivo é só o código do status (ex.: LOGIN_ERROR), nunca texto livre da resposta (D26).
    /// </summary>
    public static (string Estado, string? Motivo) Do(string status, DateTimeOffset? consentimentoVenceEm, DateTimeOffset agora)
    {
        if (consentimentoVenceEm is { } vence && vence <= agora) return (ConsentimentoVencido, null);
        if (!StatusBons.Contains(status)) return (Erro, CodigoSeguro(status));
        if (consentimentoVenceEm is { } v && v - agora <= AvisoDeConsentimento) return (ConsentimentoVencendo, null);
        return (Ativo, null);
    }

    /// <summary>Só A–Z e sublinhado, até 60 caracteres; qualquer outra coisa vira DESCONHECIDO.</summary>
    private static string CodigoSeguro(string status) =>
        status.Length is > 0 and <= Item.MotivoMaximo && status.All(ch => ch is (>= 'A' and <= 'Z') or '_') ? status : "DESCONHECIDO";
}

/// <summary>
/// Um banco dentro de uma conexão do Meu Pluggy (cada banco é um item). O <c>itemId</c> da Pluggy só existe cifrado
/// (AAD <c>integracoes.items|item_id_cifrado|&lt;id&gt;</c>) e é único no casal pelo HMAC (D7). Nunca vai para log,
/// exceção ou resposta.
/// </summary>
internal sealed class Item : Entidade, ICasalScoped
{
    public const string Tabela = "integracoes.items";
    public const string PropositoItemId = "integracoes.item";
    public const int BancoMaximo = 60;
    public const int MotivoMaximo = 60;
    public const int ItemIdMaximo = 100;

    public Guid CasalId { get; private set; }
    public Guid ConexaoId { get; private set; }
    public byte[] ItemIdCifrado { get; private set; } = [];
    public byte[] ItemIdHmac { get; private set; } = [];
    public string Banco { get; private set; } = "";
    public string Estado { get; private set; } = EstadoDoItem.Ativo;
    public string? MotivoErro { get; private set; }
    public DateTimeOffset? ConsentimentoVenceEm { get; private set; }
    public DateTimeOffset? UltimaSincronizacaoEm { get; private set; }
    public DateTimeOffset? UltimaTentativaEm { get; private set; }

    private Item() { }

    /// <summary>itemId sem espaços em volta e em minúsculas (a Pluggy usa UUID); é o que se cifra e o que se indexa.</summary>
    public static string NormalizarItemId(string itemId) => itemId.Trim().ToLowerInvariant();

    public static byte[] Indice(string itemId, ICofre cofre) => cofre.IndiceCego(NormalizarItemId(itemId), PropositoItemId);

    public static Item Criar(Guid casalId, Guid conexaoId, string itemId, string banco, string estado, string? motivo,
        DateTimeOffset? consentimentoVenceEm, ICofre cofre)
    {
        if (casalId == Guid.Empty) throw new ArgumentException("Casal vazio.", nameof(casalId));
        if (conexaoId == Guid.Empty) throw new ArgumentException("Conexão vazia.", nameof(conexaoId));
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        var normalizado = NormalizarItemId(itemId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(normalizado.Length, ItemIdMaximo);

        var item = new Item { CasalId = casalId, ConexaoId = conexaoId };
        item.DefinirBanco(banco);
        item.DefinirEstado(estado, motivo, consentimentoVenceEm);
        item.ItemIdCifrado = cofre.Cifrar(normalizado, Cofre.Aad(Tabela, "item_id_cifrado", item.Id));
        item.ItemIdHmac = cofre.IndiceCego(normalizado, PropositoItemId);
        return item;
    }

    public string LerItemId(ICofre cofre) => cofre.Decifrar(ItemIdCifrado, Cofre.Aad(Tabela, "item_id_cifrado", Id));

    private void DefinirBanco(string banco)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(banco);
        if (banco != banco.Trim()) throw new ArgumentException("Banco não aparado.", nameof(banco));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(banco.Length, BancoMaximo);
        Banco = banco;
    }

    private void DefinirEstado(string estado, string? motivo, DateTimeOffset? consentimentoVenceEm)
    {
        if (estado is not (EstadoDoItem.Ativo or EstadoDoItem.ConsentimentoVencendo or EstadoDoItem.ConsentimentoVencido or EstadoDoItem.Erro))
            throw new ArgumentOutOfRangeException(nameof(estado));
        if (motivo is not null) ArgumentOutOfRangeException.ThrowIfGreaterThan(motivo.Length, MotivoMaximo);
        Estado = estado;
        MotivoErro = estado == EstadoDoItem.Erro ? motivo : null;
        ConsentimentoVenceEm = consentimentoVenceEm;
    }
}
