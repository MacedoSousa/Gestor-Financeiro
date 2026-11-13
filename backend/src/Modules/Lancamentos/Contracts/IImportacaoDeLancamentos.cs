namespace GestorFinanceiro.Modules.Lancamentos.Contracts;

/// <summary>Uma transação do banco, já no formato do sistema (Fase 2, Tarefa 4.3).</summary>
/// <param name="IdExterno">Id da transação na Pluggy, em claro (D7): a chave natural (OPEN_FINANCE, id).</param>
/// <param name="Data">Dia em São Paulo do instante que o banco mandou.</param>
/// <param name="ValorCentavos">Já no sinal do sistema (negativo = saída ou gasto; decisão 10). Zero é ignorado.</param>
/// <param name="Descricao">Nula ou vazia vira "(sem descrição)".</param>
/// <param name="AConfirmar">PENDING no banco.</param>
/// <param name="CategoriaExterna">Código da categoria da Pluggy (só guardado; as etapas 5 e 6 usam).</param>
/// <param name="FaturaIdExterno">billId da Pluggy, só para conferência; a fatura é a do calendário do sistema (D2).</param>
public sealed record LancamentoImportado(string IdExterno, DateOnly Data, long ValorCentavos, string? Descricao,
    bool AConfirmar, int? Parcela, int? Parcelas, string? CategoriaExterna, string? FaturaIdExterno)
{
    // Descrição e valor não vão para log nem para ToString (regra 5).
    public override string ToString() => "LancamentoImportado { }";
}

/// <param name="Criados">Linhas novas, todas "a revisar".</param>
/// <param name="Atualizados">Linhas que já existiam e mudaram (data, valor, confirmação, parcela ou, se "a revisar", descrição).</param>
/// <param name="Ignorados">Valor zero, ou chave de uma linha apagada (que não é recriada).</param>
/// <param name="ApagadosPeloBanco">Confirmadas da janela que o banco deixou de mandar: soft delete.</param>
/// <param name="PendentesPromovidas">Pendentes que sumiram e tinham uma definitiva única: ela herdou a revisão.</param>
/// <param name="PendentesSemPar">Pendentes que sumiram sem definitiva única: ficam como estão.</param>
public sealed record ResumoDaImportacao(int Criados, int Atualizados, int Ignorados, int ApagadosPeloBanco, int PendentesPromovidas,
    int PendentesSemPar);

/// <summary>
/// Importação do banco para o Lancamentos (Fase 2, D6, D17): "criar ou atualizar pela chave natural", idempotente.
/// </summary>
public interface IImportacaoDeLancamentos
{
    /// <summary>
    /// Cria ou atualiza pela chave (OPEN_FINANCE, IdExterno), numa transação, sob a trava do casal e da conta. A conta tem de
    /// ser do casal e ligada. Janela: as transações OPEN_FINANCE da conta com data a partir de <paramref name="desde"/> que
    /// não vieram no lote são tratadas como apagadas pelo banco (confirmadas → soft delete) ou como pendentes a promover (a
    /// confirmar → par único na definitiva). Qualquer linha inválida desfaz o lote inteiro.
    /// </summary>
    /// <param name="apagarAusentes">
    /// Obrigatório, para quem chama decidir: verdadeiro só quando o lote é completo (o item está atualizado no banco). Falso quando o banco pode ter mandado um lote incompleto (item ainda atualizando, por exemplo): nada some por ausência;
    /// o resto (criar, atualizar, promover pendente com par) segue. Lote vazio com a janela cheia também nunca apaga.
    /// </param>
    /// <exception cref="InvalidOperationException">A conta não existe no casal ou não é ligada.</exception>
    /// <exception cref="ArgumentException">Uma linha do lote é inválida (nada foi gravado).</exception>
    Task<ResumoDaImportacao> ImportarAsync(Guid contaId, DateOnly desde, IReadOnlyList<LancamentoImportado> lote, bool apagarAusentes,
        CancellationToken ct);
}
