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

    /// <summary>Criado pelo sistema em conta ligada (rendimento, ajuste, lado manual de um par; D12).</summary>
    Sistema,
}

/// <summary>Coluna <c>revisao</c> (D14, D15): o que veio do banco fica "a revisar" até o casal conferir.</summary>
internal enum RevisaoDaTransacao
{
    ARevisar,
    Revisada,
}

/// <summary>Coluna <c>confirmacao</c>: pendente no banco (PENDING) é "a confirmar" e conta no orçamento (D14).</summary>
internal enum ConfirmacaoDaTransacao
{
    AConfirmar,
    Confirmada,
}

/// <summary>Coluna <c>apagada_por</c> (D19): só a apagada por ausência no banco volta quando o banco a manda de novo.</summary>
internal enum MotivoDoApagamento
{
    Casal,
    AusenciaNoBanco,
    PendentePromovida,
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
    internal const string ColunaDescricaoDoBanco = "descricao_banco_cifrada";

    public Guid CasalId { get; private set; }
    public Guid ContaId { get; private set; }
    public Guid? CategoriaId { get; private set; }
    public DateOnly Data { get; private set; }
    public long ValorCentavos { get; private set; }
    public NaturezaDaTransacao Natureza { get; private set; }
    public byte[] DescricaoCifrada { get; private set; } = [];
    public byte[]? ContraparteCifrada { get; private set; }

    /// <summary>A descrição como o banco mandou (só OPEN_FINANCE); a <see cref="DescricaoCifrada"/> passa a ser do casal na revisão.</summary>
    public byte[]? DescricaoBancoCifrada { get; private set; }
    public OrigemDaTransacao Origem { get; private set; }
    public string IdExterno { get; private set; } = string.Empty;
    public Guid? AutorUsuarioId { get; private set; }
    public DateOnly? FaturaVencimento { get; private set; }
    public Guid? GrupoId { get; private set; }
    public short? Parcela { get; private set; }
    public short? Parcelas { get; private set; }
    public RevisaoDaTransacao Revisao { get; private set; } = RevisaoDaTransacao.Revisada;
    public ConfirmacaoDaTransacao Confirmacao { get; private set; } = ConfirmacaoDaTransacao.Confirmada;

    /// <summary>Código da categoria da Pluggy (etapas 5 e 6); id opaco do banco.</summary>
    public string? CategoriaExterna { get; private set; }

    /// <summary>billId da Pluggy, só para conferência: a fatura do sistema é a do calendário (D2).</summary>
    public string? FaturaIdExterno { get; private set; }

    /// <summary>Nome da categoria da Pluggy, junto do código (etapa 5, botão "sempre que o banco disser").</summary>
    public string? CategoriaExternaNome { get; private set; }

    /// <summary>Sincronizações completas seguidas que vieram sem esta linha do banco (D19).</summary>
    public short Ausencias { get; private set; }

    /// <summary>Por que foi apagada (D19). Nulo em linha ativa, ou apagada antes desta regra (nunca volta).</summary>
    public MotivoDoApagamento? ApagadaPor { get; private set; }

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

    internal const string SemDescricao = "(sem descrição)";

    /// <summary>
    /// Linha do banco (Fase 2, Tarefa 4.3): origem OPEN_FINANCE, "a revisar" e sem categoria (D15); natureza pelo sinal.
    /// Parcela só quando o banco manda as duas partes e mais de uma parcela (D13: soltas, sem o limite de 24).
    /// </summary>
    public static Transacao Importar(Guid casalId, Guid contaId, DateOnly data, long valorComSinal, string? descricao, string idExterno,
        bool aConfirmar, int? parcela, int? parcelas, string? categoriaExterna, string? faturaIdExterno, DateOnly? faturaVencimento, ICofre cofre,
        string? nomeDaCategoriaExterna = null)
    {
        if (valorComSinal == 0) throw new ArgumentException("Valor zero não se importa.", nameof(valorComSinal));
        var (n, total) = ParcelaDoBanco(parcela, parcelas);
        var t = Montar(casalId, contaId, null, data, new Dinheiro(valorComSinal), NaturezaPeloSinal(valorComSinal), DescricaoDoBanco(descricao),
            null, OrigemDaTransacao.OpenFinance, idExterno, null, faturaVencimento, null, n, total, cofre);
        t.Revisao = RevisaoDaTransacao.ARevisar;
        t.Confirmacao = aConfirmar ? ConfirmacaoDaTransacao.AConfirmar : ConfirmacaoDaTransacao.Confirmada;
        t.CategoriaExterna = CodigoExterno(categoriaExterna, 20, nameof(categoriaExterna));
        t.FaturaIdExterno = CodigoExterno(faturaIdExterno, 100, nameof(faturaIdExterno));
        t.CategoriaExternaNome = t.CategoriaExterna is null ? null : NomeExterno(nomeDaCategoriaExterna);
        t.DescricaoBancoCifrada = cofre.Cifrar(DescricaoDoBanco(descricao), Cofre.Aad(Tabela, ColunaDescricaoDoBanco, t.Id));
        return t;
    }

    /// <summary>
    /// O banco mandou a mesma chave de novo: data, valor, confirmação, parcela, fatura e os códigos do banco acompanham; a
    /// descrição só enquanto "a revisar" (depois é do casal, decisão 5); a categoria nunca muda aqui. Transferência (par
    /// da etapa 6) não muda de natureza. Devolve se algo mudou (nada muda = nada é regravado nem auditado).
    /// </summary>
    public bool AtualizarDoBanco(DateOnly data, long valorComSinal, string? descricao, bool aConfirmar, int? parcela, int? parcelas,
        string? categoriaExterna, string? faturaIdExterno, DateOnly? faturaVencimento, ICofre cofre, string? nomeDaCategoriaExterna = null)
    {
        if (Origem != OrigemDaTransacao.OpenFinance) throw new InvalidOperationException("Só linha do banco se atualiza pelo banco.");
        if (valorComSinal == 0) throw new ArgumentException("Valor zero não se importa.", nameof(valorComSinal));
        var (n, total) = ParcelaDoBanco(parcela, parcelas);
        var confirmacao = aConfirmar ? ConfirmacaoDaTransacao.AConfirmar : ConfirmacaoDaTransacao.Confirmada;
        var natureza = Natureza == NaturezaDaTransacao.Transferencia ? Natureza : NaturezaPeloSinal(valorComSinal);
        var categoria = CodigoExterno(categoriaExterna, 20, nameof(categoriaExterna));
        var nomeExterno = categoria is null ? null : NomeExterno(nomeDaCategoriaExterna);
        var fatura = CodigoExterno(faturaIdExterno, 100, nameof(faturaIdExterno));
        var doBanco = DescricaoDoBanco(descricao);
        var bancoMudou = doBanco != LerDescricaoDoBanco(cofre);
        var descricaoMudou = Revisao == RevisaoDaTransacao.ARevisar && doBanco != LerDescricao(cofre);

        var mudou = Data != data || ValorCentavos != valorComSinal || Natureza != natureza || Confirmacao != confirmacao
                    || Parcela != n || Parcelas != total || CategoriaExterna != categoria || CategoriaExternaNome != nomeExterno || FaturaIdExterno != fatura
                    || FaturaVencimento != faturaVencimento || descricaoMudou || bancoMudou;
        if (!mudou) return false;

        // Receita com categoria de despesa é estorno (permitido); a categoria do casal fica como está.
        Data = data;
        ValorCentavos = valorComSinal;
        Natureza = natureza;
        Confirmacao = confirmacao;
        Parcela = (short?)n;
        Parcelas = (short?)total;
        CategoriaExterna = categoria;
        CategoriaExternaNome = nomeExterno;
        FaturaIdExterno = fatura;
        FaturaVencimento = faturaVencimento;
        if (descricaoMudou) DescricaoCifrada = cofre.Cifrar(doBanco, Cofre.Aad(Tabela, ColunaDescricao, Id));
        if (bancoMudou) DescricaoBancoCifrada = cofre.Cifrar(doBanco, Cofre.Aad(Tabela, ColunaDescricaoDoBanco, Id));
        return true;
    }

    /// <summary>
    /// A pendente sumiu e esta é a definitiva dela: herda a revisão. Se o casal já tinha revisado a pendente, a categoria e
    /// a descrição dele passam para cá (decisão 5); a definitiva continua com data, valor e chave do banco.
    /// </summary>
    public void HerdarRevisao(Transacao pendente, ICofre cofre)
    {
        ArgumentNullException.ThrowIfNull(pendente);
        if (pendente.Revisao != RevisaoDaTransacao.Revisada) return;
        Revisao = RevisaoDaTransacao.Revisada;
        CategoriaId = pendente.CategoriaId;
        DescricaoCifrada = cofre.Cifrar(pendente.LerDescricao(cofre), Cofre.Aad(Tabela, ColunaDescricao, Id));
    }

    /// <summary>Apagar com 2 sincronizações completas seguidas sem a linha (D19).</summary>
    internal const short AusenciasParaApagar = 2;

    /// <summary>
    /// Mais uma sincronização completa sem esta linha do banco. Devolve se chegou a hora de apagar; quem chama apaga (o
    /// motivo já fica marcado).
    /// </summary>
    public bool RegistrarAusencia()
    {
        if (Origem != OrigemDaTransacao.OpenFinance) throw new InvalidOperationException("Só linha do banco some do banco.");
        Ausencias++;
        if (Ausencias < AusenciasParaApagar) return false;
        ApagadaPor = MotivoDoApagamento.AusenciaNoBanco;
        return true;
    }

    /// <summary>O banco mandou a linha: a contagem de ausências recomeça. Devolve se mudou.</summary>
    public bool ZerarAusencias()
    {
        if (Ausencias == 0) return false;
        Ausencias = 0;
        return true;
    }

    /// <summary>Marca o motivo antes do <c>Remove</c> (o SoftDeleteInterceptor grava is_deleted no mesmo UPDATE).</summary>
    public void MarcarApagamento(MotivoDoApagamento motivo) => ApagadaPor = motivo;

    /// <summary>Só a apagada por ausência no banco volta (D19); a apagada pelo casal ou promovida, nunca.</summary>
    public bool PodeVoltarDoBanco => IsDeleted && ApagadaPor == MotivoDoApagamento.AusenciaNoBanco;

    /// <summary>
    /// O banco mandou de novo a linha que tinha sumido: ela volta como estava (categoria, descrição e revisão do casal). A
    /// auditoria do banco grava RESTORE.
    /// </summary>
    public void VoltarDoBanco()
    {
        if (!PodeVoltarDoBanco) throw new InvalidOperationException("Só a linha apagada por ausência no banco volta.");
        IsDeleted = false;
        DeletedAt = null;
        DeletedBy = null;
        ApagadaPor = null;
        Ausencias = 0;
    }

    /// <summary>
    /// Regra ou mapa (etapa 5): preenche a categoria de uma linha do banco "a revisar" e sem categoria. Continua "a revisar"
    /// (fora do orçamento, D14); nunca mexe no que o casal revisou.
    /// </summary>
    public void SugerirCategoria(Guid categoriaId)
    {
        if (Revisao != RevisaoDaTransacao.ARevisar || CategoriaId is not null) throw new InvalidOperationException("Só linha a revisar sem categoria recebe sugestão.");
        CategoriaId = categoriaId;
    }

    /// <summary>Revisão em lote (Tarefa 5.2): o casal escolhe a categoria e a linha passa a ser dele (REVISADA).</summary>
    public void Revisar(Guid categoriaId)
    {
        if (Revisao != RevisaoDaTransacao.ARevisar) throw new InvalidOperationException("Só linha a revisar é revisada em lote.");
        CategoriaId = categoriaId;
        Revisao = RevisaoDaTransacao.Revisada;
    }

    private static string? NomeExterno(string? nome) =>
        string.IsNullOrWhiteSpace(nome) ? null : nome.Trim() is var t && t.Length <= 60 ? t : t[..60];

    private static NaturezaDaTransacao NaturezaPeloSinal(long valor) =>
        valor < 0 ? NaturezaDaTransacao.Despesa : NaturezaDaTransacao.Receita;

    private static string DescricaoDoBanco(string? descricao) =>
        string.IsNullOrWhiteSpace(descricao) ? SemDescricao : descricao.Trim();

    private static (int? Parcela, int? Parcelas) ParcelaDoBanco(int? parcela, int? parcelas)
    {
        if (parcela is not { } n || parcelas is not { } total || total <= 1) return (null, null);
        if (n < 1 || n > total || total > short.MaxValue) throw new ArgumentException("Parcela do banco incoerente.", nameof(parcela));
        return (n, total);
    }

    private static string? CodigoExterno(string? codigo, int maximo, string campo)
    {
        if (string.IsNullOrWhiteSpace(codigo)) return null;
        var aparado = codigo.Trim();
        return aparado.Length <= maximo ? aparado : throw new ArgumentException("Código do banco longo demais.", campo);
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
        Revisao = RevisaoDaTransacao.Revisada; // Quem edita e escolhe a categoria revisou (decisão 5).
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

    /// <summary>O texto do banco; nas linhas importadas antes da V0016 (não há) ou fora do OPEN_FINANCE, a própria descrição.</summary>
    public string LerDescricaoDoBanco(ICofre cofre) => DescricaoBancoCifrada is null
        ? LerDescricao(cofre)
        : cofre.Decifrar(DescricaoBancoCifrada, Cofre.Aad(Tabela, ColunaDescricaoDoBanco, Id));

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
