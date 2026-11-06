using GestorFinanceiro.SharedKernel.Dominio;
using GestorFinanceiro.SharedKernel.Seguranca;

namespace GestorFinanceiro.Modules.Lancamentos.Dominio;

internal enum NaturezaDaTransacao
{
    Despesa,
    Receita,
    Transferencia,
}

internal enum OrigemDaTransacao
{
    Manual,
    OpenFinance,
    Whatsapp,
}

/// <summary>
/// Lançamento. O valor tem sinal (negativo = saída). Descrição e contraparte só existem cifradas,
/// com AAD <c>lancamentos.transacoes|coluna|id</c>.
/// </summary>
internal sealed class Transacao : Entidade, ICasalScoped
{
    internal const string Tabela = "lancamentos.transacoes";
    internal const string ColunaDescricao = "descricao_cifrada";
    internal const string ColunaContraparte = "contraparte_cifrada";

    public Guid CasalId { get; private set; }
    public Guid ContaId { get; private set; }
    public Guid? CategoriaId { get; private set; }
    public DateOnly Data { get; private set; }
    public long ValorCentavos { get; private set; }
    public NaturezaDaTransacao Natureza { get; private set; }
    public byte[] DescricaoCifrada { get; private set; } = [];
    public byte[]? ContraparteCifrada { get; private set; }
    public OrigemDaTransacao Origem { get; private set; }
    public string IdExterno { get; private set; } = string.Empty;
    public Guid? AutorUsuarioId { get; private set; }
    public DateOnly? FaturaVencimento { get; private set; }
    public Guid? GrupoId { get; private set; }
    public short? Parcela { get; private set; }
    public short? Parcelas { get; private set; }

    public Dinheiro Valor => new(ValorCentavos);

    private Transacao() { }

    /// <summary>Despesa: recebe o valor positivo e grava negativo.</summary>
    public static Transacao CriarDespesa(Guid casalId, Guid contaId, Guid? categoriaId, DateOnly data, Dinheiro valor,
        string descricao, string? contraparte, OrigemDaTransacao origem, string idExterno, Guid? autorUsuarioId,
        DateOnly? faturaVencimento, ICofre cofre, Guid? grupoId = null, int? parcela = null, int? parcelas = null)
    {
        ExigirPositivo(valor);
        return Montar(casalId, contaId, categoriaId, data, -valor, NaturezaDaTransacao.Despesa, descricao, contraparte,
            origem, idExterno, autorUsuarioId, faturaVencimento, grupoId, parcela, parcelas, cofre);
    }

    /// <summary>Receita (e estorno): recebe o valor positivo e grava positivo.</summary>
    public static Transacao CriarReceita(Guid casalId, Guid contaId, Guid? categoriaId, DateOnly data, Dinheiro valor,
        string descricao, string? contraparte, OrigemDaTransacao origem, string idExterno, Guid? autorUsuarioId,
        DateOnly? faturaVencimento, ICofre cofre)
    {
        ExigirPositivo(valor);
        return Montar(casalId, contaId, categoriaId, data, valor, NaturezaDaTransacao.Receita, descricao, contraparte,
            origem, idExterno, autorUsuarioId, faturaVencimento, null, null, null, cofre);
    }

    /// <summary>Um lado da transferência: valor com sinal (negativo na origem, positivo no destino); nunca tem categoria.</summary>
    public static Transacao CriarPernaDeTransferencia(Guid casalId, Guid contaId, DateOnly data, Dinheiro valorComSinal,
        string descricao, string? contraparte, OrigemDaTransacao origem, string idExterno, Guid? autorUsuarioId,
        Guid grupoId, DateOnly? faturaVencimento, ICofre cofre)
    {
        if (valorComSinal.Centavos == 0) throw new ArgumentException("O valor não pode ser zero.", nameof(valorComSinal));
        return Montar(casalId, contaId, null, data, valorComSinal, NaturezaDaTransacao.Transferencia, descricao, contraparte,
            origem, idExterno, autorUsuarioId, faturaVencimento, grupoId, null, null, cofre);
    }

    /// <summary>
    /// Edita despesa ou receita (transferência muda pelos dois lados, em outro fluxo). Recebe o valor positivo;
    /// o sinal vem da natureza. Conta, origem, chave e parcela não mudam.
    /// </summary>
    public void Editar(NaturezaDaTransacao natureza, Dinheiro valor, DateOnly data, string descricao, string? contraparte,
        Guid categoriaId, DateOnly? faturaVencimento, ICofre cofre)
    {
        if (Natureza == NaturezaDaTransacao.Transferencia || natureza == NaturezaDaTransacao.Transferencia)
            throw new InvalidOperationException("Transferência não se edita por aqui.");
        ExigirPositivo(valor);
        ArgumentException.ThrowIfNullOrWhiteSpace(descricao);
        Natureza = natureza;
        ValorCentavos = natureza == NaturezaDaTransacao.Despesa ? -valor.Centavos : valor.Centavos;
        Data = data;
        CategoriaId = categoriaId;
        FaturaVencimento = faturaVencimento;
        DescricaoCifrada = cofre.Cifrar(descricao.Trim(), Cofre.Aad(Tabela, ColunaDescricao, Id));
        ContraparteCifrada = string.IsNullOrWhiteSpace(contraparte)
            ? null
            : cofre.Cifrar(contraparte.Trim(), Cofre.Aad(Tabela, ColunaContraparte, Id));
    }

    /// <summary>
    /// Edita uma perna de transferência: valor absoluto, data e descrição. O sinal da perna fica (saída continua
    /// negativa); quem chama aplica o mesmo às duas pernas do grupo.
    /// </summary>
    public void EditarTransferencia(Dinheiro valor, DateOnly data, string descricao, ICofre cofre)
    {
        if (Natureza != NaturezaDaTransacao.Transferencia) throw new InvalidOperationException("Não é transferência.");
        ExigirPositivo(valor);
        ArgumentException.ThrowIfNullOrWhiteSpace(descricao);
        ValorCentavos = ValorCentavos < 0 ? -valor.Centavos : valor.Centavos;
        Data = data;
        DescricaoCifrada = cofre.Cifrar(descricao.Trim(), Cofre.Aad(Tabela, ColunaDescricao, Id));
    }

    public string LerDescricao(ICofre cofre) => cofre.Decifrar(DescricaoCifrada, Cofre.Aad(Tabela, ColunaDescricao, Id));

    public string? LerContraparte(ICofre cofre) =>
        ContraparteCifrada is null ? null : cofre.Decifrar(ContraparteCifrada, Cofre.Aad(Tabela, ColunaContraparte, Id));

    private static void ExigirPositivo(Dinheiro valor)
    {
        if (valor.Centavos <= 0) throw new ArgumentException("Informe o valor positivo; o sinal vem da natureza.", nameof(valor));
    }

    private static Transacao Montar(Guid casalId, Guid contaId, Guid? categoriaId, DateOnly data, Dinheiro valorComSinal,
        NaturezaDaTransacao natureza, string descricao, string? contraparte, OrigemDaTransacao origem, string idExterno,
        Guid? autorUsuarioId, DateOnly? faturaVencimento, Guid? grupoId, int? parcela, int? parcelas, ICofre cofre)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descricao);
        ArgumentException.ThrowIfNullOrWhiteSpace(idExterno);
        var t = new Transacao
        {
            CasalId = casalId,
            ContaId = contaId,
            CategoriaId = categoriaId,
            Data = data,
            ValorCentavos = valorComSinal.Centavos,
            Natureza = natureza,
            Origem = origem,
            IdExterno = idExterno,
            AutorUsuarioId = autorUsuarioId,
            FaturaVencimento = faturaVencimento,
            GrupoId = grupoId,
            Parcela = (short?)parcela,
            Parcelas = (short?)parcelas,
        };
        t.DescricaoCifrada = cofre.Cifrar(descricao.Trim(), Cofre.Aad(Tabela, ColunaDescricao, t.Id));
        t.ContraparteCifrada = string.IsNullOrWhiteSpace(contraparte)
            ? null
            : cofre.Cifrar(contraparte.Trim(), Cofre.Aad(Tabela, ColunaContraparte, t.Id));
        return t;
    }
}
