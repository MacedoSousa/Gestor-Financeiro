using System.Globalization;
using System.Text;
using GestorFinanceiro.Modules.Integracoes.Pluggy;
using GestorFinanceiro.SharedKernel.Dominio;
using GestorFinanceiro.SharedKernel.Seguranca;

namespace GestorFinanceiro.Modules.Integracoes.Dominio;

/// <summary>Tipos de vínculo (coluna <c>tipo</c>).</summary>
internal static class TipoDeVinculo
{
    public const string Conta = "CONTA";
    public const string Cartao = "CARTAO";
    public const string Investimento = "INVESTIMENTO";
}

/// <summary>
/// Liga uma conta, cartão ou aplicação do banco (pelo id opaco da Pluggy, em claro, D7) a uma conta do sistema, dentro de
/// um item (D18). Sem conta: aplicação aguardando grupo, ou conta marcada "não importar" (decisão 16). A identidade
/// (<see cref="IdentidadeDe"/>) impede a mesma conta física de vir por dois items do casal.
/// </summary>
internal sealed class Vinculo : Entidade, ICasalScoped
{
    public const string PropositoIdentidade = "integracoes.identidade";
    public const int IdExternoMaximo = 100;

    public Guid CasalId { get; private set; }
    public Guid ItemId { get; private set; }
    public string Tipo { get; private set; } = TipoDeVinculo.Conta;
    public string IdExterno { get; private set; } = "";
    public Guid? ContaId { get; private set; }
    public byte[]? IdentidadeHmac { get; private set; }
    public DateOnly? HistoricoDesde { get; private set; }
    public DateOnly? ImportadoAte { get; private set; }
    public long? DiferencaCentavos { get; private set; }
    public short DiferencasSeguidas { get; private set; }
    public string? FaturasDivergentes { get; private set; }
    public DateTimeOffset? NaoImportarEm { get; private set; }

    private Vinculo() { }

    public static Vinculo Criar(Guid casalId, Guid itemId, string tipo, string idExterno, Guid? contaId, byte[]? identidadeHmac)
    {
        if (casalId == Guid.Empty) throw new ArgumentException("Casal vazio.", nameof(casalId));
        if (itemId == Guid.Empty) throw new ArgumentException("Item vazio.", nameof(itemId));
        if (tipo is not (TipoDeVinculo.Conta or TipoDeVinculo.Cartao or TipoDeVinculo.Investimento))
            throw new ArgumentOutOfRangeException(nameof(tipo));
        ArgumentException.ThrowIfNullOrWhiteSpace(idExterno);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(idExterno.Length, IdExternoMaximo);
        if (tipo == TipoDeVinculo.Investimento && identidadeHmac is not null)
            throw new ArgumentException("Aplicação não tem identidade.", nameof(identidadeHmac));
        return new Vinculo { CasalId = casalId, ItemId = itemId, Tipo = tipo, IdExterno = idExterno, ContaId = contaId, IdentidadeHmac = identidadeHmac };
    }

    /// <summary>"Não importar" (decisão 16): fica registrada, sem conta, e não é perguntada de novo.</summary>
    public static Vinculo NaoImportar(Guid casalId, Guid itemId, string tipo, string idExterno, byte[]? identidadeHmac, DateTimeOffset agora)
    {
        var v = Criar(casalId, itemId, tipo, idExterno, null, identidadeHmac);
        v.NaoImportarEm = agora;
        return v;
    }

    public bool NaoImportada => NaoImportarEm is not null;

    /// <summary>Incluir depois uma conta "não importada", ou dar conta a uma aplicação que aguardava grupo.</summary>
    public void DefinirConta(Guid contaId)
    {
        if (ContaId is { } atual && atual != contaId) throw new InvalidOperationException("O vínculo já tem conta.");
        ContaId = contaId;
        NaoImportarEm = null;
    }

    /// <summary>
    /// Troca (ou limpa) a conta de um vínculo cuja conta nunca chegou a existir no casal: o id ficou indisponível, o LIGAR
    /// mudou de alvo antes de concluir, ou o grupo foi desfeito. Recusa se a conta mudou desde que quem chama a leu.
    /// </summary>
    public void TrocarConta(Guid? esperada, Guid? nova)
    {
        if (ContaId != esperada) throw new InvalidOperationException("A conta do vínculo mudou no meio.");
        ContaId = nova;
        if (nova is not null) NaoImportarEm = null;
    }

    /// <summary>CONTA para conta bancária, CARTAO para cartão de crédito.</summary>
    public static string TipoDe(ContaPluggy conta) =>
        conta.Tipo == "CREDIT" ? TipoDeVinculo.Cartao : TipoDeVinculo.Conta;

    /// <summary>Tipo da conta no sistema: CARTAO, POUPANCA ou CORRENTE.</summary>
    public static string TipoDaConta(ContaPluggy conta) => conta switch
    {
        { Tipo: "CREDIT" } => "CARTAO",
        { Subtipo: "SAVINGS_ACCOUNT" } => "POUPANCA",
        _ => "CORRENTE",
    };

    /// <summary>
    /// Índice cego da conta física: agência e conta só com os dígitos (decisão 11); cartão, o final e o portador
    /// normalizado (decisão 6). Nulo quando o banco não manda o que identifica a conta.
    /// </summary>
    public static byte[]? IdentidadeDe(ContaPluggy conta, ICofre cofre)
    {
        ArgumentNullException.ThrowIfNull(conta);
        ArgumentNullException.ThrowIfNull(cofre);
        var texto = TipoDe(conta) == TipoDeVinculo.Cartao
            ? (string.IsNullOrWhiteSpace(conta.FinalDoCartao) ? null : $"{conta.FinalDoCartao.Trim()}|{PortadorNormalizado(conta.Portador)}")
            : ConversaoPluggy.TransferNumberNormalizado(conta.AgenciaEConta);
        return texto is null ? null : cofre.IndiceCego(texto, PropositoIdentidade);
    }

    /// <summary>Maiúsculas, sem acento e com um espaço só entre as palavras.</summary>
    public static string PortadorNormalizado(string? portador)
    {
        var semAcento = new StringBuilder();
        foreach (var ch in (portador ?? "").Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) semAcento.Append(ch);
        return string.Join(' ', semAcento.ToString().ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
