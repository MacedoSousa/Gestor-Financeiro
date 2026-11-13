using GestorFinanceiro.Modules.Contas.Contracts;
using GestorFinanceiro.Modules.Integracoes.Contracts;
using GestorFinanceiro.Modules.Integracoes.Dominio;
using GestorFinanceiro.Modules.Integracoes.Infra;
using GestorFinanceiro.Modules.Integracoes.Pluggy;
using GestorFinanceiro.Modules.Lancamentos.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GestorFinanceiro.Modules.Integracoes.Aplicacao;

internal abstract record ResultadoDaSincronizacao
{
    public sealed record Feita(SincronizacaoDto Dto) : ResultadoDaSincronizacao;
    public sealed record Recusada(ResultadoDeNascimento Motivo) : ResultadoDaSincronizacao;
}

/// <summary>
/// Sincronização de um item (Tarefa 4.5). Lê o banco uma vez; reconcilia as contas ligadas órfãs do casal (D18) e registra as
/// aplicações novas (aguardando grupo); depois importa cada conta e cada cartão nascidos, cada um por si (a falha num não
/// desfaz o outro), pelos contratos dos módulos donos (D17):
/// <list type="bullet">
/// <item>primeira vez: desde 2026-01-01 (ou a data mais antiga que o banco entregar), e o saldo inicial (D3, decisão 12, CEO
/// 11/11 21h50); depois, desde <c>importado_ate − 10 dias</c>;</item>
/// <item>conta arquivada (decisão 7) e "não importar" (decisão 16) não são lidas;</item>
/// <item>conferência de saldo (só conta) e de fatura (só cartão), que aparecem só depois de 2 sincronizações seguidas. Nada é
/// ajustado.</item>
/// </list>
/// Log só com ids do sistema, slot e tipo do erro; nunca valor, descrição ou id do banco (regra 5).
/// </summary>
internal sealed partial class Sincronizacao(
    IntegracoesDbContext db,
    Descoberta descoberta,
    IClientePluggy pluggy,
    IImportacaoDeLancamentos importacao,
    IContasLigadas contasLigadas,
    IConsultaDeContas contas,
    IConsultaDeSaldos saldos,
    IEnumerable<IVerificadorDeUsoDaConta> verificadoresDeUso,
    TimeProvider relogio,
    ILogger<Sincronizacao> log)
{
    /// <summary>Início do histórico importado (spec: desde 2026-01-01).</summary>
    internal static readonly DateOnly InicioDoHistorico = Nascimento.InicioDoHistorico;

    /// <summary>A sincronização seguinte relê os últimos 10 dias (pendentes, ajustes e apagadas do banco).</summary>
    internal const int DiasDaJanela = 10;

    /// <summary>O que uma conta (ou cartão) rendeu na importação.</summary>
    private sealed record Importada(int Novos, int Atualizados);

    public async Task<ResultadoDaSincronizacao> SincronizarAsync(Guid itemId, CancellationToken ct)
    {
        var (acesso, recusa) = await descoberta.AcessoAsync(itemId, ct);
        if (recusa is not null) return new ResultadoDaSincronizacao.Recusada(recusa);

        // Uma sincronização por item de cada vez (revisão da E4, achado 5): a segunda espera a primeira terminar e então vê
        // importado_ate e as conferências já gravados. Trava de sessão, numa conexão aberta só para isso.
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_lock(hashtextextended('integracoes.sincronizacao:' || {itemId}::text, 0))", ct);
            try
            {
                return await SincronizarNaTravaAsync(acesso!, ct);
            }
            finally
            {
                await db.Database.ExecuteSqlAsync(
                    $"SELECT pg_advisory_unlock(hashtextextended('integracoes.sincronizacao:' || {itemId}::text, 0))", CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private async Task<ResultadoDaSincronizacao> SincronizarNaTravaAsync(AcessoAoItem a, CancellationToken ct)
    {
        // Lote completo só com o item atualizado no banco; atualizando ou com erro, nada some por ausência (achado 4).
        bool atualizado;
        try
        {
            atualizado = (await pluggy.ObterItemAsync(a.Chaves, a.ItemPluggy, ct)).Status == "UPDATED";
        }
        catch (Exception e) when (e is ErroDaPluggy or HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            atualizado = false; // a leitura das contas, logo abaixo, decide se a sincronização segue
        }

        var (leitura, falha) = await descoberta.LerAsync(a, ct);
        if (falha is not null)
        {
            await RegistrarNoItemAsync(a.Item.Id, sucesso: false, ct);
            return new ResultadoDaSincronizacao.Recusada(falha);
        }

        await ReconciliarAsync(a, leitura!, ct);

        var hoje = ConversaoPluggy.DiaDeSaoPaulo(relogio.GetUtcNow());
        var doCasal = (await contas.ListarAsync(ct)).ToDictionary(c => c.Id);
        var vinculos = await db.Vinculos.AsNoTracking()
            .Where(v => v.ItemId == a.Item.Id && v.ContaId != null && v.NaoImportarEm == null
                        && (v.Tipo == TipoDeVinculo.Conta || v.Tipo == TipoDeVinculo.Cartao))
            .OrderBy(v => v.Id)
            .ToListAsync(ct);

        int sincronizadas = 0, comFalha = 0, novos = 0, atualizados = 0;
        foreach (var vinculo in vinculos)
        {
            // Conta que não existe (ou ainda não é ligada: nascimento pela metade) ou arquivada não é lida (decisão 7).
            if (!doCasal.TryGetValue(vinculo.ContaId!.Value, out var conta) || !conta.Ligada || conta.Arquivada) continue;
            try
            {
                var r = await ImportarAsync(a, leitura!, vinculo.Id, conta, hoje, atualizado, ct);
                sincronizadas++;
                novos += r.Novos;
                atualizados += r.Atualizados;
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // A importação de uma conta é uma transação do Lancamentos: o que falhou não ficou pela metade.
                db.ChangeTracker.Clear();
                comFalha++;
                ContaFalhou(log, a.Chaves.Slot, vinculo.Id, e is ErroDaPluggy p ? p.Status : 0, e.GetType().Name);
            }
        }

        try
        {
            await ConferirInvestimentosAsync(a, leitura!, doCasal, hoje, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Conferência só informa: a falha dela não desfaz nem marca como falha o que as contas importaram.
            db.ChangeTracker.Clear();
            InvestimentosFalharam(log, a.Chaves.Slot, a.Item.Id, e.GetType().Name);
        }

        await RegistrarNoItemAsync(a.Item.Id, sucesso: comFalha == 0, ct);
        return new ResultadoDaSincronizacao.Feita(new SincronizacaoDto(sincronizadas, comFalha, novos, atualizados));
    }

    /// <summary>Uma conta ou cartão: lê, importa, define o saldo inicial (só na primeira vez), confere e avança o vínculo.</summary>
    private async Task<Importada> ImportarAsync(AcessoAoItem a, LeituraDoBanco leitura, Guid vinculoId, ContaResumo conta, DateOnly hoje,
        bool apagarAusentes, CancellationToken ct)
    {
        var vinculo = await db.Vinculos.FirstAsync(v => v.Id == vinculoId, ct);
        var doBanco = leitura.Contas.FirstOrDefault(c => c.Id == vinculo.IdExterno)
            ?? throw new InvalidOperationException("A conta não veio do banco nesta leitura.");
        var cartao = vinculo.Tipo == TipoDeVinculo.Cartao;
        var primeira = vinculo.ImportadoAte is null;
        var janela = primeira ? InicioDoHistorico : Maior(vinculo.HistoricoDesde ?? InicioDoHistorico, vinculo.ImportadoAte!.Value.AddDays(-DiasDaJanela));
        // O cartão lê desde o início do histórico: a conferência soma todas as transações de cada fatura do banco.
        var leituraDesde = cartao ? vinculo.HistoricoDesde ?? InicioDoHistorico : janela;

        var transacoes = new List<TransacaoPluggy>();
        await foreach (var t in pluggy.ListarTransacoesAsync(a.Chaves, vinculo.IdExterno, leituraDesde, ct)) transacoes.Add(t);

        var historico = vinculo.HistoricoDesde ?? InicioDe(transacoes);
        if (primeira) janela = historico;

        var lote = transacoes
            .Where(t => t.Data >= janela)
            .Select(t => new LancamentoImportado(t.Id, t.Data, ConversaoPluggy.ValorNoSistema(doBanco.Tipo, t.ValorCentavos), t.Descricao,
                t.Status == "PENDING", t.Parcela, t.Parcelas, t.CategoriaId, t.FaturaId, t.Categoria))
            .ToList();
        var resumo = await importacao.ImportarAsync(conta.Id, janela, lote, apagarAusentes, ct);

        if (primeira)
            await contasLigadas.DefinirSaldoInicialAsync(conta.Id, SaldoInicial(cartao, doBanco, lote, leitura, historico, hoje), historico, ct);

        if (cartao)
        {
            var faturas = leitura.FaturasPorCartao.GetValueOrDefault(doBanco.Id) ?? [];
            vinculo.ConferirFaturas(ConferenciaDasFaturas.Diferencas(faturas, transacoes, historico));
        }
        else
        {
            // Esperado no sistema: o saldo do banco mais as pendentes (o banco não as soma; o sistema, sim).
            var pendentes = lote.Where(l => l.AConfirmar && l.Data <= hoje).Sum(l => l.ValorCentavos);
            var sistema = (await saldos.SaldosAsync(hoje, ct)).FirstOrDefault(s => s.ContaId == conta.Id)?.SaldoCentavos ?? 0;
            vinculo.ConferirSaldo(doBanco.SaldoCentavos + pendentes - sistema);
        }

        vinculo.RegistrarImportacao(historico, hoje);
        await db.SaveChangesAsync(ct);
        return new Importada(resumo.Criados, resumo.Atualizados);
    }

    /// <summary>
    /// D21 e D22: cada aplicação nascida (num grupo) guarda o balance e a situação que o banco mandou agora; cada grupo com
    /// aplicação deste item guarda a diferença entre a soma das aplicações ATIVA dele (de qualquer item) e o saldo do
    /// sistema. Grupo arquivado, ou com aplicação de situação desconhecida, ou ativa sem valor, fica sem conferência. Nada é
    /// lançado: o rendimento entra no sistema na E6. Grupo marcado pela V0019 refaz o saldo inicial uma vez (D22).
    /// </summary>
    private async Task ConferirInvestimentosAsync(AcessoAoItem a, LeituraDoBanco leitura, IReadOnlyDictionary<Guid, ContaResumo> doCasal,
        DateOnly hoje, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        // O saldo é do Lancamentos (outra conexão, sem trava): lido antes, para não segurar a trava do casal durante ele.
        var sistema = (await saldos.SaldosAsync(hoje, ct)).ToDictionary(s => s.ContaId, s => s.SaldoCentavos);
        // Na trava do casal (a mesma do nascimento e da reconciliação): a sincronização de outro item com aplicação no mesmo
        // grupo, ou um agrupamento novo, espera; a diferença gravada bate com os valores gravados (revisão da D21, achado 3).
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended('integracoes.casal:' || {a.Item.CasalId}::text, 0))", ct);
        var desteItem = await db.Vinculos
            .Where(v => v.ItemId == a.Item.Id && v.Tipo == TipoDeVinculo.Investimento && v.ContaId != null)
            .ToListAsync(ct);
        if (desteItem.Count == 0) return;

        var doBanco = leitura.Aplicacoes.GroupBy(ap => ap.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var v in desteItem)
        {
            // Aplicação que não veio na leitura: situação desconhecida (o grupo fica sem conferência).
            var ap = doBanco.GetValueOrDefault(v.IdExterno);
            v.RegistrarValorNoBanco(ap?.SaldoCentavos, ap?.Situacao ?? SituacaoDaAplicacao.Desconhecida);
        }

        var grupos = desteItem.Select(v => v.ContaId!.Value).Distinct().ToList();
        var deOutrosItems = await db.Vinculos
            .Where(v => v.ItemId != a.Item.Id && v.Tipo == TipoDeVinculo.Investimento && v.ContaId != null && grupos.Contains(v.ContaId.Value))
            .ToListAsync(ct);
        foreach (var membros in desteItem.Concat(deOutrosItems).GroupBy(v => v.ContaId!.Value))
        {
            var lista = membros.ToList();
            var valor = Vinculo.ValorDoGrupo(lista);
            if (!doCasal.TryGetValue(membros.Key, out var grupo) || !grupo.Ligada)
            {
                foreach (var m in lista) m.ConferirGrupo(null);
                continue;
            }

            var saldoDoSistema = sistema.GetValueOrDefault(membros.Key);
            var arquivado = grupo.Arquivada;
            // D22: grupo nascido antes da regra "só ACTIVE" (marcado pela V0019) refaz o saldo inicial com as ativas, uma vez,
            // pelo contrato do Contas (auditado); sem nenhuma ativa, saldo 0 e arquivado. Sem saber a situação de todas, espera.
            // O Contas grava noutra conexão e confirma sozinho: nada do Integracoes pode ter sido gravado antes disto nesta
            // transação (a trava de auditoria do casal ficaria presa aqui, e a conexão do Contas esperaria por ela).
            if (lista.Any(m => m.RefazerSaldoInicial) && valor is { } ativas)
            {
                if (await EmUsoAsync(grupo.Id, ct))
                {
                    // Grupo com lançamento (rendimento da E6): refazer o saldo inicial contaria o rendimento duas vezes.
                    GrupoNaoRefeito(log, grupo.Id);
                }
                else
                {
                    await contasLigadas.DefinirSaldoInicialAsync(grupo.Id, ativas, grupo.SaldoInicialEm, ct);
                    saldoDoSistema += ativas - grupo.SaldoInicialCentavos;
                    if (ativas == 0 && !lista.Any(m => m.SituacaoBanco == SituacaoDaAplicacao.Ativa))
                    {
                        await contasLigadas.ArquivarAsync(grupo.Id, ct);
                        arquivado = true;
                    }
                    GrupoRefeito(log, grupo.Id, arquivado);
                }
                foreach (var m in lista) m.SaldoInicialRefeito();
            }

            foreach (var m in lista) m.ConferirGrupo(!arquivado && valor is { } v ? v - saldoDoSistema : null);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Conta: o saldo do banco menos as confirmadas importadas até hoje, no início do histórico (D3). Cartão (CEO 11/11
    /// 21h50): −(total da fatura do banco que fechou antes do início do histórico e vence depois dele), ou 0 sem ela; o saldo
    /// do banco não entra (decisão 12).
    /// </summary>
    private static long SaldoInicial(bool cartao, ContaPluggy doBanco, IReadOnlyList<LancamentoImportado> lote, LeituraDoBanco leitura,
        DateOnly historico, DateOnly hoje)
    {
        if (!cartao)
            return doBanco.SaldoCentavos - lote.Where(l => !l.AConfirmar && l.Data >= historico && l.Data <= hoje).Sum(l => l.ValorCentavos);
        var faturas = leitura.FaturasPorCartao.GetValueOrDefault(doBanco.Id) ?? [];
        return -faturas.Where(f => f.Fechamento is { } fechou && fechou < historico && f.Vencimento >= historico).Sum(f => f.TotalCentavos);
    }

    /// <summary>
    /// Na trava do casal (a mesma do nascimento): (1) conta ligada não arquivada que nenhum vínculo do casal aponta deixa de
    /// ser ligada (D18, E3 nota H: duas abas confirmando juntas): sem lançamentos, é arquivada; com, volta a ser manual (D19,
    /// item 3c); (2) aplicação do banco sem vínculo neste item vira vínculo
    /// sem conta, aguardando grupo em Conexões.
    /// </summary>
    private async Task ReconciliarAsync(AcessoAoItem a, LeituraDoBanco leitura, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended('integracoes.casal:' || {a.Item.CasalId}::text, 0))", ct);

        var apontadas = (await db.Vinculos.Where(v => v.ContaId != null).Select(v => v.ContaId!.Value).ToListAsync(ct)).ToHashSet();
        foreach (var orfa in (await contas.ListarAsync(ct)).Where(c => c.Ligada && !c.Arquivada && !apontadas.Contains(c.Id)))
            if (await contasLigadas.DesligarAsync(orfa.Id, ct) is var feito and not ResultadoDoDesligar.NaoEraLigada)
                ContaOrfaDesligada(log, orfa.Id, feito);

        var aplicacoes = (await db.Vinculos.Where(v => v.ItemId == a.Item.Id && v.Tipo == TipoDeVinculo.Investimento)
            .Select(v => v.IdExterno).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        // Resgatada nova não fica aguardando grupo (D22).
        foreach (var nova in leitura.Aplicacoes.Where(ap => !ap.Resgatada && !aplicacoes.Contains(ap.Id)))
            db.Vinculos.Add(Vinculo.Criar(a.Item.CasalId, a.Item.Id, TipoDeVinculo.Investimento, nova.Id, null, null));

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task RegistrarNoItemAsync(Guid itemId, bool sucesso, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var item = await db.Items.FirstAsync(i => i.Id == itemId, ct);
        var agora = relogio.GetUtcNow();
        if (sucesso) item.RegistrarSincronizacao(agora);
        else item.RegistrarTentativa(agora);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// "Desde 2026-01-01, ou a data mais antiga recebida se o banco entregar menos." Conta pouco usada (ou cartão sem compra
    /// no começo de janeiro) não é "o banco entregou menos": só conta como menos quando a transação mais antiga vem mais de
    /// <see cref="ToleranciaDoHistorico"/> dias depois do início. Nunca antes do início.
    /// </summary>
    internal static DateOnly InicioDe(IReadOnlyCollection<TransacaoPluggy> transacoes)
    {
        if (transacoes.Count == 0) return InicioDoHistorico;
        var maisAntiga = transacoes.Min(t => t.Data);
        return maisAntiga > InicioDoHistorico.AddDays(ToleranciaDoHistorico) ? maisAntiga : InicioDoHistorico;
    }

    /// <summary>Um mês: a primeira transação até 01/02 ainda é histórico completo.</summary>
    internal const int ToleranciaDoHistorico = 31;

    private async Task<bool> EmUsoAsync(Guid contaId, CancellationToken ct)
    {
        foreach (var verificador in verificadoresDeUso)
            if (await verificador.EstaEmUsoAsync(contaId, ct))
                return true;
        return false;
    }

    private static DateOnly Maior(DateOnly a, DateOnly b) => a > b ? a : b;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sincronização: a conta do vínculo {Vinculo} falhou (slot {Slot}, status {Status}, {Tipo}).")]
    private static partial void ContaFalhou(ILogger logger, int slot, Guid vinculo, int status, string tipo);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sincronização: a conferência dos investimentos do item {Item} falhou (slot {Slot}, {Tipo}).")]
    private static partial void InvestimentosFalharam(ILogger logger, int slot, Guid item, string tipo);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sincronização: o grupo {Conta} já tem lançamentos; o saldo inicial não foi refeito (D22).")]
    private static partial void GrupoNaoRefeito(ILogger logger, Guid conta);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sincronização: o saldo inicial do grupo {Conta} foi refeito só com as aplicações ativas (arquivado: {Arquivado}; D22).")]
    private static partial void GrupoRefeito(ILogger logger, Guid conta, bool arquivado);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sincronização: conta ligada {Conta} sem vínculo foi desligada ({Resultado}; D18, D19).")]
    private static partial void ContaOrfaDesligada(ILogger logger, Guid conta, ResultadoDoDesligar resultado);
}
