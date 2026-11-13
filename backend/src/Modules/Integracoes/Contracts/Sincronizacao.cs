namespace GestorFinanceiro.Modules.Integracoes.Contracts;

/// <summary>
/// Resposta de POST /api/v1/integracoes/items/{id}/sincronizacao (Tarefa 4.5). Só contagens: nenhum valor, descrição ou id
/// do banco.
/// </summary>
/// <param name="ContasSincronizadas">Contas e cartões importados agora (arquivadas e "não importar" ficam de fora).</param>
/// <param name="ContasComFalha">Contas e cartões que o banco não entregou agora; a próxima sincronização tenta de novo.</param>
public sealed record SincronizacaoDto(int ContasSincronizadas, int ContasComFalha, int LancamentosNovos, int LancamentosAtualizados);

/// <summary>Uma conta, cartão ou grupo de investimento que vem deste banco, como "Conexões" mostra.</summary>
/// <param name="Tipo">CORRENTE, POUPANCA, CARTAO ou INVESTIMENTO (um grupo de aplicações).</param>
/// <param name="HistoricoDesde">Primeiro dia do histórico importado; nulo antes da primeira sincronização (e em grupo).</param>
/// <param name="DiferencaCentavos">
/// Conta: saldo do banco menos o do sistema, quando difere em 2 sincronizações seguidas. Grupo (D21): valor no banco menos
/// o saldo do sistema, quando difere na última sincronização. Nada é ajustado. Nulo quando bate ou não deu para conferir.
/// </param>
/// <param name="FaturasDivergentes">Só de cartão: faturas do banco cujo total difere da soma das transações dela em 2 sincronizações seguidas.</param>
/// <param name="ValorNoBancoCentavos">
/// Só de grupo (D21): soma do valor líquido (balance) das aplicações na última sincronização; nulo antes dela ou se alguma
/// aplicação veio sem valor.
/// </param>
public sealed record ContaDoItemDto(Guid ContaId, string Apelido, string Tipo, bool Arquivada, DateOnly? HistoricoDesde, long? DiferencaCentavos,
    IReadOnlyList<FaturaDivergenteDto> FaturasDivergentes, long? ValorNoBancoCentavos = null);

/// <summary>
/// Um grupo de investimento (conta INVESTIMENTO ligada) e o que o banco diz dele hoje (D21), para a tela de Contas. Nada é
/// lançado: o rendimento entra no sistema na E6.
/// </summary>
/// <param name="ValorNoBancoCentavos">Soma do balance das aplicações na última sincronização; nulo se não deu para conferir.</param>
/// <param name="DiferencaCentavos">Valor no banco menos o saldo do sistema; nulo quando bate ou não deu para conferir.</param>
public sealed record GrupoNoBancoDto(Guid ContaId, long? ValorNoBancoCentavos, long? DiferencaCentavos);

/// <param name="DiferencaCentavos">Total do banco menos a soma das transações com aquela fatura.</param>
public sealed record FaturaDivergenteDto(DateOnly Vencimento, long DiferencaCentavos);
