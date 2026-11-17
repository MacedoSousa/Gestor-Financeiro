using GestorFinanceiro.SharedKernel.Dominio;

namespace GestorFinanceiro.Modules.Lancamentos.Dominio;

/// <summary>Tipos de item da caixa "A conferir" (coluna <c>tipo</c>).</summary>
internal static class TipoDeItemAConferir
{
    public const string TransferenciaSemPar = "TRANSFERENCIA_SEM_PAR";
    public const string PendenteSumiu = "PENDENTE_SUMIU";
    public const string PendenteSemPar = "PENDENTE_SEM_PAR";
    public const string AporteOuResgate = "APORTE_OU_RESGATE";
    /// <summary>A diferença do grupo de investimento: valor no banco − saldo do sistema (CEO, D25). Um aberto por grupo.</summary>
    public const string DiferencaDoGrupo = "DIFERENCA_DO_GRUPO";
}

/// <summary>Resoluções (coluna <c>resolucao</c>). "Deixar para depois" não resolve: o item continua aberto.</summary>
internal static class ResolucaoDoItem
{
    public const string Transferencia = "TRANSFERENCIA";
    public const string Gasto = "GASTO";
    public const string Receita = "RECEITA";
    public const string Aporte = "APORTE";
    public const string Resgate = "RESGATE";
    public const string Ciente = "CIENTE";
    public const string PareadoAutomaticamente = "PAREADO_AUTOMATICAMENTE";
    public const string DeixarParaDepois = "DEIXAR_PARA_DEPOIS";
    // D25: as ações da diferença do grupo e o fechamento sozinho.
    public const string Rendimento = "RENDIMENTO";
    public const string IrIofTaxa = "IR_IOF_TAXA";
    /// <summary>Opção: parear um aporte ou resgate aberto com o grupo. Ao zerar a diferença, o item fecha como PAREADO_COM_MOVIMENTO.</summary>
    public const string ParearMovimento = "PAREAR_MOVIMENTO";
    public const string PareadoComMovimento = "PAREADO_COM_MOVIMENTO";
    public const string AjustarSaldoInicial = "AJUSTAR_SALDO_INICIAL";
    public const string ResolvidoPeloBanco = "RESOLVIDO_PELO_BANCO";
}

/// <summary>
/// Um item da caixa "A conferir" (Fase 2, D11, Tarefa 6.1): o que o sistema não decidiu sozinho. Criado ou atualizado pela
/// chave (idempotente); resolvido, fica RESOLVIDO com a resolução. Nunca é apagado (nada é descartado).
/// </summary>
internal sealed class ItemAConferir : Entidade, ICasalScoped
{
    public Guid CasalId { get; private set; }
    public string Tipo { get; private set; } = TipoDeItemAConferir.TransferenciaSemPar;
    public string Chave { get; private set; } = string.Empty;
    public Guid ContaId { get; private set; }
    public Guid? TransacaoId { get; private set; }
    public long ValorCentavos { get; private set; }
    public DateOnly Data { get; private set; }
    public string Estado { get; private set; } = "ABERTO";
    public string? Resolucao { get; private set; }
    /// <summary>Só na diferença do grupo: o valor no banco (só ACTIVE) da última leitura (D25).</summary>
    public long? ValorBancoCentavos { get; private set; }
    /// <summary>Só na diferença do grupo: o primeiro item do grupo, o único que oferece "ajustar o saldo inicial" (D25).</summary>
    public bool PrimeiroDoGrupo { get; private set; }

    private ItemAConferir() { }

    public bool Aberto => Estado == "ABERTO";

    public static ItemAConferir Criar(Guid casalId, string tipo, string chave, Guid contaId, Guid? transacaoId, long valorCentavos, DateOnly data) =>
        new() { CasalId = casalId, Tipo = tipo, Chave = chave, ContaId = contaId, TransacaoId = transacaoId, ValorCentavos = valorCentavos, Data = data };

    /// <summary>A diferença do grupo (D25): chave diferenca:&lt;conta&gt;:&lt;n&gt;; o primeiro do grupo é o n = 1.</summary>
    public static ItemAConferir CriarDiferencaDoGrupo(Guid casalId, Guid grupoId, int vez, long diferenca, long valorNoBanco, DateOnly dia) =>
        new()
        {
            CasalId = casalId, Tipo = TipoDeItemAConferir.DiferencaDoGrupo, Chave = ChaveDiferenca(grupoId, vez), ContaId = grupoId,
            ValorCentavos = diferenca, ValorBancoCentavos = valorNoBanco, Data = dia, PrimeiroDoGrupo = vez == 1,
        };

    /// <summary>A diferença aberta acompanha a última leitura (valor no banco, diferença e dia) ou o recálculo depois de uma ação.</summary>
    public void Recalcular(long diferenca, long? valorNoBanco = null, DateOnly? dia = null)
    {
        if (!Aberto || Tipo != TipoDeItemAConferir.DiferencaDoGrupo) throw new InvalidOperationException("Só a diferença aberta do grupo muda.");
        ValorCentavos = diferenca;
        ValorBancoCentavos = valorNoBanco ?? ValorBancoCentavos;
        Data = dia ?? Data;
    }

    public void Resolver(string resolucao)
    {
        if (!Aberto) throw new InvalidOperationException("Item já resolvido.");
        if (resolucao == ResolucaoDoItem.DeixarParaDepois) throw new ArgumentException("Deixar para depois não resolve.", nameof(resolucao));
        Estado = "RESOLVIDO";
        Resolucao = resolucao;
    }

    public static string ChavePendenteSumiu(Guid transacao) => $"pendente-sumiu:{transacao:D}";
    public static string ChavePendenteSemPar(Guid transacao) => $"pendente-sem-par:{transacao:D}";
    public static string ChaveSemPar(Guid transacao) => $"sem-par:{transacao:D}";
    public static string ChaveMovimento(Guid transacao) => $"movimento:{transacao:D}";
    public static string ChaveDiferenca(Guid grupo, int vez) => $"diferenca:{grupo:D}:{vez}";
}
