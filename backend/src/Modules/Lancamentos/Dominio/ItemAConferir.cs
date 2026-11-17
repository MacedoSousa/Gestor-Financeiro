using GestorFinanceiro.SharedKernel.Dominio;

namespace GestorFinanceiro.Modules.Lancamentos.Dominio;

/// <summary>Tipos de item da caixa "A conferir" (coluna <c>tipo</c>).</summary>
internal static class TipoDeItemAConferir
{
    public const string TransferenciaSemPar = "TRANSFERENCIA_SEM_PAR";
    public const string PendenteSumiu = "PENDENTE_SUMIU";
    public const string PendenteSemPar = "PENDENTE_SEM_PAR";
    public const string AporteOuResgate = "APORTE_OU_RESGATE";
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

    private ItemAConferir() { }

    public bool Aberto => Estado == "ABERTO";

    public static ItemAConferir Criar(Guid casalId, string tipo, string chave, Guid contaId, Guid? transacaoId, long valorCentavos, DateOnly data) =>
        new() { CasalId = casalId, Tipo = tipo, Chave = chave, ContaId = contaId, TransacaoId = transacaoId, ValorCentavos = valorCentavos, Data = data };

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
}
