using GestorFinanceiro.Modules.Contas.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Dominio;
using GestorFinanceiro.Modules.Lancamentos.Infra;
using GestorFinanceiro.SharedKernel.Aplicacao;
using GestorFinanceiro.SharedKernel.Seguranca;
using Microsoft.EntityFrameworkCore;

namespace GestorFinanceiro.Modules.Lancamentos.Aplicacao;

/// <summary>
/// A caixa "A conferir" (Fase 2, D11, Tarefa 6.1). Cada resolução deixa o item RESOLVIDO; nada é apagado. Resolver de novo
/// um item resolvido não faz nada (não duplica). "Deixar para depois" não muda nada. Sob a trava do casal (a mesma do
/// reconhecimento e da importação) e a das contas envolvidas. Mensagens sem dado do lançamento (regra 5).
/// </summary>
internal sealed class AConferir(LancamentosDbContext db, IUsuarioAtual atual, IConsultaDeContas contas, Pareamento pareamento, ICofre cofre,
    DiferencaDoGrupo diferencaDoGrupo, IContasLigadas contasLigadas, ServicoDeCategorias categorias, TimeProvider relogio)
{
    internal const string TituloJaResolvido = "Este item já foi resolvido. Atualize a página.";
    internal const string TituloGrupoIndisponivel = "O grupo foi arquivado ou não vem mais do banco. Atualize a página.";
    internal const string TituloDiferencaMudou = "A diferença deste grupo mudou desde que a tela foi aberta. Confira de novo.";
    internal const string DescricaoDoRendimento = "Rendimento do investimento";
    internal const string DescricaoDoAjuste = "IR, IOF ou taxa do investimento";

    /// <summary>D25: o 409 do IR/IOF diz a categoria do ajuste do dia e como trocá-la (o nome é do próprio casal).</summary>
    internal static string TituloAjusteEmOutraCategoria(string categoria) =>
        $"O ajuste deste dia já está em “{categoria}”. Escolha a mesma, ou mude a categoria do ajuste em Lançamentos.";
    internal const string TituloSemOutroLado = "O outro lado ainda não veio do banco nessa conta. Tente depois da próxima sincronização.";
    internal const string TituloOutroLadoAmbiguo = "Há mais de um lançamento que pode ser o outro lado nessa conta. Resolva pelo extrato.";
    private const int DiasDoOutroLado = 5;

    /// <summary>
    /// As opções do item. A linha que o banco tirou, ou que já virou transferência por outro caminho, não tem mais o que
    /// decidir: só "Ciente" (revisão do E6a, D1; antes, toda opção dava 409 e o item ficava aberto para sempre).
    /// </summary>
    /// <param name="linhaIndisponivel">
    /// A linha do item sumiu ou já é transferência; na diferença do grupo, o grupo foi arquivado ou deixou de ser ligado: só
    /// "Ciente" (testador do E6b pela D25, Importante 2).
    /// </param>
    /// <param name="haMovimento">
    /// Há aporte ou resgate aberto em "A conferir", de data a partir do saldo inicial do grupo: a diferença pode ser pareada com
    /// ele (D25; o de antes já está no saldo inicial, testador do E6b pela D25, Importante 3).
    /// </param>
    internal static IReadOnlyList<string> Opcoes(ItemAConferir item, bool linhaIndisponivel = false, bool haMovimento = false) => linhaIndisponivel
        ? [ResolucaoDoItem.Ciente]
        : item.Tipo == TipoDeItemAConferir.DiferencaDoGrupo ? OpcoesDaDiferenca(item, haMovimento)
        : item.Tipo switch
    {
        TipoDeItemAConferir.TransferenciaSemPar =>
            [ResolucaoDoItem.Transferencia, item.ValorCentavos < 0 ? ResolucaoDoItem.Gasto : ResolucaoDoItem.Receita, ResolucaoDoItem.DeixarParaDepois],
        TipoDeItemAConferir.AporteOuResgate =>
            [item.ValorCentavos < 0 ? ResolucaoDoItem.Aporte : ResolucaoDoItem.Resgate, item.ValorCentavos < 0 ? ResolucaoDoItem.Gasto : ResolucaoDoItem.Receita,
             ResolucaoDoItem.DeixarParaDepois],
        _ => [ResolucaoDoItem.Ciente],
    };

    /// <summary>
    /// D25, 2: positiva → é rendimento ou parear com aporte/resgate; negativa → IR/IOF ou taxa, parear ou deixar para depois;
    /// só no primeiro item do grupo → ajustar o saldo inicial. Nada é lançado sem a ação do casal.
    /// </summary>
    private static List<string> OpcoesDaDiferenca(ItemAConferir item, bool haMovimento)
    {
        var positiva = item.ValorCentavos > 0;
        List<string> opcoes = [positiva ? ResolucaoDoItem.Rendimento : ResolucaoDoItem.IrIofTaxa];
        if (haMovimento) opcoes.Add(ResolucaoDoItem.ParearMovimento);
        if (item.PrimeiroDoGrupo) opcoes.Add(ResolucaoDoItem.AjustarSaldoInicial);
        if (!positiva) opcoes.Add(ResolucaoDoItem.DeixarParaDepois);
        return opcoes;
    }

    private static bool GrupoDisponivel(ContaResumo? grupo) => grupo is { Ligada: true, Arquivada: false, Tipo: "INVESTIMENTO" };

    /// <summary>O aporte ou resgate que pode explicar a diferença do grupo: de data a partir do saldo inicial dele.</summary>
    private static bool ServeAoGrupo(ItemAConferir movimento, ContaResumo grupo) => movimento.Data >= grupo.SaldoInicialEm;

    private static IReadOnlyList<string> OpcoesDoGrupo(ItemAConferir item, ContaResumo? grupo, IEnumerable<ItemAConferir> movimentos) =>
        GrupoDisponivel(grupo)
            ? Opcoes(item, haMovimento: movimentos.Any(m => ServeAoGrupo(m, grupo!)))
            : Opcoes(item, linhaIndisponivel: true);

    public async Task<IReadOnlyList<ItemAConferirDto>?> ListarAsync(CancellationToken ct)
    {
        if (atual.CasalId is null) return null;
        var itens = await db.ItensAConferir.AsNoTracking().Where(i => i.Estado == "ABERTO").OrderBy(i => i.Data).ThenBy(i => i.Id).ToListAsync(ct);
        if (itens.Count == 0) return [];
        var ids = itens.Where(i => i.TransacaoId is not null).Select(i => i.TransacaoId!.Value).ToList();
        // Sob o filtro SoftDelete: a pendente que sumiu (apagada) não mostra descrição (regra 9).
        var transacoes = await db.Transacoes.AsNoTracking().Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        var doCasal = (await contas.ListarAsync(ct)).ToDictionary(c => c.Id);
        var movimentos = itens.Where(i => i.Tipo == TipoDeItemAConferir.AporteOuResgate).ToList();
        return itens.Select(i =>
            {
                var linha = i.TransacaoId is { } t ? transacoes.GetValueOrDefault(t) : null;
                return new ItemAConferirDto(i.Id, i.Tipo, i.ContaId, doCasal.GetValueOrDefault(i.ContaId)?.Apelido ?? "Conta removida", i.Data,
                    i.ValorCentavos, linha?.LerDescricaoDoBanco(cofre),
                    i.Tipo == TipoDeItemAConferir.DiferencaDoGrupo
                        ? OpcoesDoGrupo(i, doCasal.GetValueOrDefault(i.ContaId), movimentos)
                        : Opcoes(i, Indisponivel(i, linha)));
            })
            .ToList();
    }

    public async Task<ResultadoDeLancamento> ResolverAsync(Guid id, ResolucaoDto pedido, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pedido);
        if (atual.CasalId is not { } casalId) return new ResultadoDeLancamento.NaoEncontrado();
        // Fora da transação (ela abre a própria): a categoria de papel Rendimentos nasce com as padrão.
        if (pedido.Opcao == ResolucaoDoItem.Rendimento) await categorias.GarantirPadraoAsync(casalId, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended('lancamentos.casal:' || {casalId}::text, 0))", ct);
            var item = await db.ItensAConferir.FirstOrDefaultAsync(i => i.Id == id, ct);
            if (item is null) return new ResultadoDeLancamento.NaoEncontrado();
            // Repetir a mesma resolução não duplica (204); outra opção num item já resolvido (pelo banco, pelo outro cônjuge, numa
            // tela velha) é 409, para a tela não dizer que fez o que não fez.
            if (!item.Aberto)
                return pedido.Opcao == item.Resolucao ? new ResultadoDeLancamento.Feito() : new ResultadoDeLancamento.Recusado(TituloJaResolvido, 409);
            var linhaDoItem = item.TransacaoId is { } idDaLinha ? await db.Transacoes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == idDaLinha, ct) : null;
            IReadOnlyList<string> opcoes;
            if (item.Tipo == TipoDeItemAConferir.DiferencaDoGrupo)
            {
                var grupo = await contas.ObterAsync(item.ContaId, ct);
                // Grupo arquivado ou que deixou de ser ligado: "Ciente" fecha o item sem lançar; o resto é tela velha (409).
                if (!GrupoDisponivel(grupo))
                {
                    if (pedido.Opcao != ResolucaoDoItem.Ciente) return new ResultadoDeLancamento.Recusado(TituloGrupoIndisponivel, 409);
                    item.Resolver(ResolucaoDoItem.Ciente);
                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    return new ResultadoDeLancamento.Feito();
                }
                var movimentos = await db.ItensAConferir.AsNoTracking()
                    .Where(i => i.Tipo == TipoDeItemAConferir.AporteOuResgate && i.Estado == "ABERTO").ToListAsync(ct);
                opcoes = OpcoesDoGrupo(item, grupo, movimentos);
            }
            else opcoes = Opcoes(item, Indisponivel(item, linhaDoItem));
            if (pedido.Opcao is not { } opcao || !opcoes.Contains(opcao, StringComparer.Ordinal))
                return Invalido("opcao", "Escolha uma das opções deste item.");
            if (opcao == ResolucaoDoItem.DeixarParaDepois) return new ResultadoDeLancamento.Feito();
            if (item.Tipo == TipoDeItemAConferir.DiferencaDoGrupo)
            {
                var (resultado, gravar) = await ResolverDiferencaAsync(casalId, item, opcao, pedido, ct);
                if (gravar)
                {
                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                }
                return resultado;
            }

            var recusa = opcao switch
            {
                ResolucaoDoItem.Ciente => null,
                ResolucaoDoItem.Gasto or ResolucaoDoItem.Receita => await DecidirCategoriaAsync(item, pedido.CategoriaId, ct),
                ResolucaoDoItem.Transferencia or ResolucaoDoItem.Aporte or ResolucaoDoItem.Resgate => await ParearAsync(item, opcao, pedido.ContaId, ct),
                _ => Invalido("opcao", "Escolha uma das opções deste item."),
            };
            if (recusa is not null) return recusa;

            item.Resolver(opcao);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new ResultadoDeLancamento.Feito();
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>A linha do item sumiu (apagada pelo banco, fora do filtro SoftDelete) ou já é transferência.</summary>
    private static bool Indisponivel(ItemAConferir item, Transacao? linha) =>
        item.TransacaoId is not null && item.Tipo != TipoDeItemAConferir.PendenteSumiu
        && (linha is null || linha.Natureza == NaturezaDaTransacao.Transferencia);

    private async Task<Transacao?> LinhaAsync(ItemAConferir item, CancellationToken ct)
    {
        if (item.TransacaoId is not { } id) return null;
        await TravaDeConta.TomarAsync(db, [item.ContaId], ct);
        return await db.Transacoes.FirstOrDefaultAsync(t => t.Id == id, ct);
    }

    private async Task<ResultadoDeLancamento?> DecidirCategoriaAsync(ItemAConferir item, Guid? categoriaId, CancellationToken ct)
    {
        if (categoriaId is not { } id) return Invalido("categoriaId", "Escolha a categoria.");
        if (await LinhaAsync(item, ct) is not { } linha || linha.Natureza == NaturezaDaTransacao.Transferencia)
            return new ResultadoDeLancamento.Recusado("Este lançamento já não está disponível. Atualize a página.", 409);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended('lancamentos.categoria:' || {id}::text, 0))", ct);
        var categoria = await db.Categorias.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (categoria is null) return Invalido("categoriaId", "Categoria não encontrada.");
        if (categoria.Arquivada) return new ResultadoDeLancamento.Recusado(Revisao.TituloCategoriaArquivada, 400);
        if (!SugestaoDeCategoria.Serve(categoria, linha.Natureza)) return Invalido("categoriaId", "A categoria não é do tipo do lançamento.");
        linha.DecidirCategoria(id);
        return null;
    }

    /// <summary>
    /// Transferência, aporte ou resgate com a conta escolhida: se ela é ligada e o outro lado veio do banco (valor oposto, até 5
    /// dias, único), as duas pernas do banco viram o par; se não veio e a conta é manual, ou é o grupo de investimento, o
    /// sistema cria o lado (par:&lt;id&gt;, D9, D10, D12).
    /// </summary>
    private async Task<ResultadoDeLancamento?> ParearAsync(ItemAConferir item, string opcao, Guid? contaId, CancellationToken ct)
    {
        if (contaId is not { } destinoId) return Invalido("contaId", "Escolha a outra conta.");
        var destino = await contas.ObterAsync(destinoId, ct);
        if (destino is null || destino.Arquivada) return Invalido("contaId", "Conta não encontrada.");
        if (destino.Id == item.ContaId) return Invalido("contaId", "Escolha outra conta.");
        if (opcao != ResolucaoDoItem.Transferencia && destino.Tipo is not ("POUPANCA" or "INVESTIMENTO"))
            return Invalido("contaId", "Aporte e resgate são com uma conta de poupança ou investimento.");

        await TravaDeConta.TomarAsync(db, [item.ContaId, destino.Id], ct);
        if (item.TransacaoId is not { } linhaId || await db.Transacoes.FirstOrDefaultAsync(t => t.Id == linhaId, ct) is not { } linha
                                                 || linha.Natureza == NaturezaDaTransacao.Transferencia)
            return new ResultadoDeLancamento.Recusado("Este lançamento já não está disponível. Atualize a página.", 409);

        var outros = await db.Transacoes
            .Where(t => t.ContaId == destino.Id && t.Origem == OrigemDaTransacao.OpenFinance && t.Natureza != NaturezaDaTransacao.Transferencia
                        && t.Parcela == null && t.Confirmacao == ConfirmacaoDaTransacao.Confirmada
                        && t.ValorCentavos == -linha.ValorCentavos && t.Data >= linha.Data.AddDays(-DiasDoOutroLado)
                        && t.Data <= linha.Data.AddDays(DiasDoOutroLado))
            .ToListAsync(ct);
        // Como no reconhecimento: só linha ainda não decidida pelo casal (a revisar, ou com código da E6 e sem item resolvido
        // como gasto ou receita); a que ele decidiu não vira perna de transferência por baixo (revisão do E6a, N1).
        var idsDosOutros = outros.Select(t => t.Id).ToList();
        var decididas = await db.ItensAConferir.AsNoTracking()
            .Where(i => i.TransacaoId != null && idsDosOutros.Contains(i.TransacaoId.Value) && i.Estado == "RESOLVIDO"
                        && (i.Resolucao == ResolucaoDoItem.Gasto || i.Resolucao == ResolucaoDoItem.Receita))
            .Select(i => i.TransacaoId!.Value).ToListAsync(ct);
        outros = outros.Where(t => (t.Revisao == RevisaoDaTransacao.ARevisar || CategoriasDoBanco.ReconhecidoNaProximaEtapa(t.CategoriaExterna))
                                   && !decididas.Contains(t.Id)).ToList();
        switch (outros)
        {
            case [var outro]:
                if (linha.ValorCentavos < 0) Pareamento.Parear(linha, outro);
                else Pareamento.Parear(outro, linha);
                // Os itens abertos do outro lado fecham junto, como pareados (revisão do E6a, D1 e rodada 2, N2).
                foreach (var doOutro in await db.ItensAConferir.Where(i => i.TransacaoId == outro.Id && i.Estado == "ABERTO").ToListAsync(ct))
                    doOutro.Resolver(ResolucaoDoItem.PareadoAutomaticamente);
                return null;
            case [_, _, ..]:
                return new ResultadoDeLancamento.Recusado(TituloOutroLadoAmbiguo, 409);
        }
        // Nada veio do banco: conta ligada que não é o grupo só recebe o lado quando ele vier do banco (opção B).
        if (destino.Ligada && destino.Tipo != "INVESTIMENTO") return new ResultadoDeLancamento.Recusado(TituloSemOutroLado, 409);
        await pareamento.CriarParManualAsync(linha, destino, ct);
        // O aporte ou resgate entrou no grupo: a diferença aberta dele acompanha (D25), e "é rendimento" nunca conta o aporte.
        if (destino.Tipo == "INVESTIMENTO") await diferencaDoGrupo.AcompanharAsync(destino.Id, ct);
        return null;
    }

    /// <summary>
    /// As ações da diferença do grupo (D25, 2). O valor usado é sempre o de agora (valor no banco da última leitura − saldo do
    /// sistema hoje), e tem de ser o que a tela mostrou (pedido.ValorCentavos): se mudou, o item é atualizado e nada é lançado
    /// (409); se zerou, o item fecha. A leitura da sincronização espera a trava do casal, que quem chama segura. Devolve se a
    /// transação deve ser gravada.
    /// </summary>
    private async Task<(ResultadoDeLancamento Resultado, bool Gravar)> ResolverDiferencaAsync(Guid casalId, ItemAConferir item, string opcao,
        ResolucaoDto pedido, CancellationToken ct)
    {
        if (await contas.ObterAsync(item.ContaId, ct) is not { Ligada: true, Arquivada: false, Tipo: "INVESTIMENTO" })
            return (new ResultadoDeLancamento.Recusado(TituloGrupoIndisponivel, 409), false);
        if (pedido.ValorCentavos is not { } visto) return (Invalido("valorCentavos", "Atualize a página e tente de novo."), false);

        ItemAConferir? movimento = null;
        if (opcao == ResolucaoDoItem.ParearMovimento)
        {
            movimento = pedido.MovimentoId is { } idDoMovimento
                ? await db.ItensAConferir.FirstOrDefaultAsync(i => i.Id == idDoMovimento && i.Tipo == TipoDeItemAConferir.AporteOuResgate
                                                                   && i.Estado == "ABERTO", ct)
                : null;
            if (movimento is null || !ServeAoGrupo(movimento, (await contas.ObterAsync(item.ContaId, ct))!))
                return (Invalido("movimentoId", "Escolha um aporte ou resgate em aberto, de depois do saldo inicial do grupo."), false);
        }

        // "Ajustar o saldo inicial" chama o Contas, que grava noutra conexão com a trava da conta (e a auditoria do casal): esta
        // transação não pode tê-las nem ter gravado nada antes. As outras ações lançam aqui, na trava da conta (a sincronização
        // atualiza o item só sob ela).
        if (opcao != ResolucaoDoItem.AjustarSaldoInicial)
            await TravaDeConta.TomarAsync(db, movimento is null ? [item.ContaId] : [item.ContaId, movimento.ContaId], ct);
        await db.Entry(item).ReloadAsync(ct);
        if (!item.Aberto)
            return (opcao == item.Resolucao ? new ResultadoDeLancamento.Feito() : new ResultadoDeLancamento.Recusado(TituloJaResolvido, 409), false);

        // Um retrato só do grupo, lido depois da trava do casal: a diferença de agora e o saldo inicial esperado da troca vêm
        // dele (revisão do E6b pela D25, rodada 2); a correção da D22, sob outra trava, que gravar depois faz a troca dar 409.
        var grupo = await contas.ObterAsync(item.ContaId, ct) ?? throw new InvalidOperationException("Grupo não encontrado no casal.");
        var agora = await diferencaDoGrupo.DeAgoraAsync(item, grupo, ct);
        if (agora == 0)
        {
            // Já bate (o ajuste foi gravado e o item não fechou, ou o saldo mudou por outro caminho): fecha, sem lançar de novo.
            item.Recalcular(0);
            var ajuste = opcao == ResolucaoDoItem.AjustarSaldoInicial;
            item.Resolver(ajuste ? ResolucaoDoItem.AjustarSaldoInicial : ResolucaoDoItem.ResolvidoPeloBanco);
            return (ajuste ? new ResultadoDeLancamento.Feito() : new ResultadoDeLancamento.Recusado(TituloDiferencaMudou, 409), true);
        }
        if (agora != item.ValorCentavos || agora != visto)
        {
            var mudou = agora != item.ValorCentavos;
            if (mudou) item.Recalcular(agora);
            return (new ResultadoDeLancamento.Recusado(TituloDiferencaMudou, 409), mudou);
        }

        var hoje = Brasilia.Hoje(relogio);
        switch (opcao)
        {
            case ResolucaoDoItem.Rendimento:
                await LancarRendimentoAsync(casalId, item, hoje, ct);
                break;
            case ResolucaoDoItem.IrIofTaxa:
                if (await LancarAjusteAsync(casalId, item, pedido.CategoriaId, hoje, ct) is { } recusa) return (recusa, false);
                break;
            case ResolucaoDoItem.AjustarSaldoInicial:
                // Compara e troca na trava da conta: se a correção da D22 (sob outra trava) mudou o saldo inicial depois da
                // leitura acima, nada é gravado (409). Repetir depois de uma queda recalcula zero acima e só fecha.
                if (!await contasLigadas.TrocarSaldoInicialAsync(grupo.Id, grupo.SaldoInicialCentavos, checked(grupo.SaldoInicialCentavos + agora), ct))
                    return (new ResultadoDeLancamento.Recusado(TituloDiferencaMudou, 409), false);
                break;
            case ResolucaoDoItem.ParearMovimento:
                var opcaoDoMovimento = movimento!.ValorCentavos < 0 ? ResolucaoDoItem.Aporte : ResolucaoDoItem.Resgate;
                if (await ParearAsync(movimento, opcaoDoMovimento, grupo.Id, ct) is { } recusaDoPar) return (recusaDoPar, false);
                movimento.Resolver(opcaoDoMovimento);
                // O ParearAsync já recalculou este item (AcompanharAsync): zerou, fechou; sobrou, fica aberto com o resto.
                return (new ResultadoDeLancamento.Feito(), true);
            default:
                return (Invalido("opcao", "Escolha uma das opções deste item."), false);
        }
        item.Resolver(opcao);
        return (new ResultadoDeLancamento.Feito(), true);
    }

    /// <summary>"É rendimento" (D25): receita na categoria de papel Rendimentos, de hoje, rendimento:&lt;conta&gt;:&lt;dia&gt;; soma no dia.</summary>
    private async Task LancarRendimentoAsync(Guid casalId, ItemAConferir item, DateOnly hoje, CancellationToken ct)
    {
        var chave = $"rendimento:{item.ContaId:D}:{hoje:yyyy-MM-dd}";
        var existente = await db.Transacoes.FirstOrDefaultAsync(t => t.Origem == OrigemDaTransacao.Sistema && t.IdExterno == chave, ct);
        if (existente is not null)
        {
            existente.SomarNoDia(item.ValorCentavos);
            return;
        }
        var rendimentos = await ServicoDeConciliacao.CategoriaDeRendimentosAsync(db, casalId, ct);
        db.Transacoes.Add(Transacao.CriarDoSistema(casalId, item.ContaId, rendimentos, hoje, item.ValorCentavos, NaturezaDaTransacao.Receita,
            DescricaoDoRendimento, chave, null, null, cofre));
    }

    /// <summary>"IR/IOF ou taxa" (D25, decisão 4): despesa na categoria escolhida, de hoje, ajuste-investimento:&lt;conta&gt;:&lt;dia&gt;; soma no dia.</summary>
    private async Task<ResultadoDeLancamento?> LancarAjusteAsync(Guid casalId, ItemAConferir item, Guid? categoriaId, DateOnly hoje, CancellationToken ct)
    {
        if (categoriaId is not { } id) return Invalido("categoriaId", "Escolha a categoria.");
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended('lancamentos.categoria:' || {id}::text, 0))", ct);
        var categoria = await db.Categorias.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (categoria is null) return Invalido("categoriaId", "Categoria não encontrada.");
        if (categoria.Arquivada) return new ResultadoDeLancamento.Recusado(Revisao.TituloCategoriaArquivada, 400);
        if (categoria.Tipo != TipoDeCategoria.Despesa) return Invalido("categoriaId", "Escolha uma categoria de despesa.");

        var chave = $"ajuste-investimento:{item.ContaId:D}:{hoje:yyyy-MM-dd}";
        var existente = await db.Transacoes.FirstOrDefaultAsync(t => t.Origem == OrigemDaTransacao.Sistema && t.IdExterno == chave, ct);
        if (existente is null)
        {
            db.Transacoes.Add(Transacao.CriarDoSistema(casalId, item.ContaId, id, hoje, item.ValorCentavos, NaturezaDaTransacao.Despesa,
                DescricaoDoAjuste, chave, null, null, cofre));
            return null;
        }
        // Uma categoria só por ajuste do dia: outra é 409, com o nome da que está (do próprio casal).
        if (existente.CategoriaId != id)
        {
            var nome = await db.Categorias.AsNoTracking().Where(c => c.Id == existente.CategoriaId).Select(c => c.Nome).FirstOrDefaultAsync(ct);
            return new ResultadoDeLancamento.Recusado(TituloAjusteEmOutraCategoria(nome ?? "outra categoria"), 409);
        }
        existente.SomarNoDia(item.ValorCentavos);
        return null;
    }

    private static ResultadoDeLancamento.Invalido Invalido(string campo, string mensagem) =>
        new(new Dictionary<string, string[]> { [campo] = [mensagem] });
}
