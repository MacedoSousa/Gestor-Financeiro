using System.Globalization;
using System.Text;
using System.Text.Json;
using GestorFinanceiro.Modules.Integracoes.Pluggy;
using GestorFinanceiro.SharedKernel.Dominio;
using GestorFinanceiro.SharedKernel.Seguranca;

namespace GestorFinanceiro.Modules.Integracoes.Dominio;

/// <summary>Uma fatura do banco que não bate com a soma das transações dela (coluna <c>faturas_divergentes</c>).</summary>
internal sealed record FaturaDivergente(DateOnly Vencimento, long DiferencaCentavos, int Seguidas);

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

    /// <summary>Só aplicação: o balance da última sincronização; nulo se o banco não mandou um valor válido (D21).</summary>
    public long? ValorBancoCentavos { get; private set; }

    /// <summary>Só aplicação: ATIVA, PENDENTE, RESGATADA ou DESCONHECIDA na última leitura; nulo antes dela (D22).</summary>
    public string? SituacaoBanco { get; private set; }

    /// <summary>Grupo nascido antes da regra "só ACTIVE": a próxima sincronização refaz o saldo inicial dele (D22, V0019).</summary>
    public bool RefazerSaldoInicial { get; private set; }

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

    /// <summary>Diferenças (de saldo ou de fatura) só aparecem na tela depois de 2 sincronizações seguidas (spec).</summary>
    public const int SeguidasParaMostrar = 2;

    /// <summary>
    /// Importação concluída até <paramref name="hoje"/>. O início do histórico é gravado uma vez (a primeira importação) e
    /// não muda depois.
    /// </summary>
    public void RegistrarImportacao(DateOnly historicoDesde, DateOnly hoje)
    {
        HistoricoDesde ??= historicoDesde;
        ImportadoAte = hoje;
    }

    /// <summary>Aplicação: o valor que o banco mandou agora (nulo = sem valor válido). Nada é lançado (D21).</summary>
    public void RegistrarValorNoBanco(long? centavos, string situacao)
    {
        if (Tipo != TipoDeVinculo.Investimento) throw new InvalidOperationException("Só aplicação guarda o valor no banco.");
        ValorBancoCentavos = centavos;
        SituacaoBanco = situacao;
    }

    /// <summary>
    /// Valor no banco de um grupo (as aplicações dele, de qualquer item): a soma das ATIVA (D22). Nulo quando não dá para
    /// saber: alguma aplicação ainda não lida, com situação desconhecida, ou ativa sem valor. Sem nenhuma ativa, 0.
    /// </summary>
    public static long? ValorDoGrupo(IEnumerable<Vinculo> membros)
    {
        var lista = membros.ToList();
        if (lista.Any(m => m.SituacaoBanco is null or SituacaoDaAplicacao.Desconhecida)) return null;
        var ativas = lista.Where(m => m.SituacaoBanco == SituacaoDaAplicacao.Ativa).ToList();
        return ativas.Any(m => m.ValorBancoCentavos is null) ? null : ativas.Sum(m => m.ValorBancoCentavos!.Value);
    }

    /// <summary>O saldo inicial do grupo já foi refeito só com as aplicações ativas (D22).</summary>
    public void SaldoInicialRefeito() => RefazerSaldoInicial = false;

    /// <summary>
    /// Aplicação de um grupo: a diferença do grupo (soma dos valores no banco menos o saldo do sistema), a mesma em todas as
    /// aplicações dele. Nula quando não dá para conferir (alguma aplicação sem valor): limpa a conferência.
    /// </summary>
    public void ConferirGrupo(long? diferencaCentavos)
    {
        if (Tipo != TipoDeVinculo.Investimento) throw new InvalidOperationException("Só aplicação confere grupo.");
        ConferirSaldo(diferencaCentavos ?? 0);
    }

    /// <summary>
    /// Conferência de saldo (conta; e grupo de investimento, por <see cref="ConferirGrupo"/>): diferença zero zera a
    /// contagem; outra soma uma sincronização seguida.
    /// </summary>
    public void ConferirSaldo(long diferencaCentavos)
    {
        if (diferencaCentavos == 0)
        {
            DiferencaCentavos = null;
            DiferencasSeguidas = 0;
            return;
        }
        DiferencaCentavos = diferencaCentavos;
        DiferencasSeguidas = (short)Math.Min(DiferencasSeguidas + 1, short.MaxValue);
    }

    /// <summary>A diferença de saldo, só depois de 2 sincronizações seguidas com diferença.</summary>
    public long? DiferencaVisivel => DiferencasSeguidas >= SeguidasParaMostrar ? DiferencaCentavos : null;

    /// <summary>
    /// Conferência das faturas do cartão (decisão 12): guarda, por fatura do banco (billId, id opaco, D7), a diferença e
    /// quantas sincronizações seguidas ela aparece. Fatura que bateu (ou saiu da lista) some.
    /// </summary>
    public void ConferirFaturas(IReadOnlyList<DiferencaDeFatura> diferencas)
    {
        ArgumentNullException.ThrowIfNull(diferencas);
        var antes = LerFaturas();
        var agora = diferencas.ToDictionary(d => d.FaturaId, d => new FaturaDivergente(d.Vencimento, d.DiferencaCentavos,
            (antes.TryGetValue(d.FaturaId, out var a) ? a.Seguidas : 0) + 1), StringComparer.Ordinal);
        FaturasDivergentes = agora.Count == 0 ? null : JsonSerializer.Serialize(agora);
    }

    /// <summary>As faturas com diferença em 2 sincronizações seguidas ou mais, por vencimento.</summary>
    public IReadOnlyList<FaturaDivergente> FaturasVisiveis =>
        LerFaturas().Values.Where(f => f.Seguidas >= SeguidasParaMostrar).OrderBy(f => f.Vencimento).ToList();

    private Dictionary<string, FaturaDivergente> LerFaturas() =>
        FaturasDivergentes is null
            ? new Dictionary<string, FaturaDivergente>(StringComparer.Ordinal)
            : JsonSerializer.Deserialize<Dictionary<string, FaturaDivergente>>(FaturasDivergentes)!;

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
    /// Índice cego da conta física, sempre com o código COMPE do banco do item (D18): conta, o código e a agência e conta só
    /// com os dígitos (decisão 11); cartão, o código, o final e o portador normalizado (decisão 6). Nulo quando o banco não
    /// manda o que identifica a conta.
    /// </summary>
    public static byte[]? IdentidadeDe(ContaPluggy conta, string codigoBanco, ICofre cofre)
    {
        ArgumentNullException.ThrowIfNull(conta);
        ArgumentNullException.ThrowIfNull(cofre);
        if (!Item.CodigoValido(codigoBanco)) throw new ArgumentException("Código de banco com 3 dígitos.", nameof(codigoBanco));
        var texto = TipoDe(conta) == TipoDeVinculo.Cartao
            ? (string.IsNullOrWhiteSpace(conta.FinalDoCartao) ? null : $"{conta.FinalDoCartao.Trim()}|{PortadorNormalizado(conta.Portador)}")
            : ConversaoPluggy.TransferNumberNormalizado(conta.AgenciaEConta);
        return texto is null ? null : cofre.IndiceCego($"{codigoBanco}|{texto}", PropositoIdentidade);
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
