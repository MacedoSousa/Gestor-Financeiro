namespace GestorFinanceiro.Modules.Integracoes.Contracts;

/// <summary>
/// Resposta de POST /api/v1/integracoes/items/{id}/sincronizacao (Tarefa 4.5). Só contagens: nenhum valor, descrição ou id
/// do banco.
/// </summary>
/// <param name="ContasSincronizadas">Contas e cartões importados agora (arquivadas e "não importar" ficam de fora).</param>
/// <param name="ContasComFalha">Contas e cartões que o banco não entregou agora; a próxima sincronização tenta de novo.</param>
public sealed record SincronizacaoDto(int ContasSincronizadas, int ContasComFalha, int LancamentosNovos, int LancamentosAtualizados);

/// <summary>Uma conta ou cartão que vem deste banco, como "Conexões" mostra.</summary>
/// <param name="Tipo">CORRENTE, POUPANCA ou CARTAO.</param>
/// <param name="HistoricoDesde">Primeiro dia do histórico importado; nulo antes da primeira sincronização.</param>
/// <param name="DiferencaCentavos">
/// Só de conta (não de cartão): saldo do banco menos o do sistema, quando difere em 2 sincronizações seguidas; nada é
/// ajustado. Nulo quando bate.
/// </param>
/// <param name="FaturasDivergentes">Só de cartão: faturas do banco cujo total difere da soma das transações dela em 2 sincronizações seguidas.</param>
public sealed record ContaDoItemDto(Guid ContaId, string Apelido, string Tipo, bool Arquivada, DateOnly? HistoricoDesde, long? DiferencaCentavos,
    IReadOnlyList<FaturaDivergenteDto> FaturasDivergentes);

/// <param name="DiferencaCentavos">Total do banco menos a soma das transações com aquela fatura.</param>
public sealed record FaturaDivergenteDto(DateOnly Vencimento, long DiferencaCentavos);
