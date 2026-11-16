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
internal sealed class AConferir(LancamentosDbContext db, IUsuarioAtual atual, IConsultaDeContas contas, Pareamento pareamento, ICofre cofre)
{
    internal const string TituloSemOutroLado = "O outro lado ainda não veio do banco nessa conta. Tente depois da próxima sincronização.";
    internal const string TituloOutroLadoAmbiguo = "Há mais de um lançamento que pode ser o outro lado nessa conta. Resolva pelo extrato.";
    private const int DiasDoOutroLado = 5;

    /// <summary>
    /// As opções do item. A linha que o banco tirou, ou que já virou transferência por outro caminho, não tem mais o que
    /// decidir: só "Ciente" (revisão do E6a, D1; antes, toda opção dava 409 e o item ficava aberto para sempre).
    /// </summary>
    internal static IReadOnlyList<string> Opcoes(ItemAConferir item, bool linhaIndisponivel = false) => linhaIndisponivel
        ? [ResolucaoDoItem.Ciente]
        : item.Tipo switch
    {
        TipoDeItemAConferir.TransferenciaSemPar =>
            [ResolucaoDoItem.Transferencia, item.ValorCentavos < 0 ? ResolucaoDoItem.Gasto : ResolucaoDoItem.Receita, ResolucaoDoItem.DeixarParaDepois],
        TipoDeItemAConferir.AporteOuResgate =>
            [item.ValorCentavos < 0 ? ResolucaoDoItem.Aporte : ResolucaoDoItem.Resgate, item.ValorCentavos < 0 ? ResolucaoDoItem.Gasto : ResolucaoDoItem.Receita,
             ResolucaoDoItem.DeixarParaDepois],
        _ => [ResolucaoDoItem.Ciente],
    };

    public async Task<IReadOnlyList<ItemAConferirDto>?> ListarAsync(CancellationToken ct)
    {
        if (atual.CasalId is null) return null;
        var itens = await db.ItensAConferir.AsNoTracking().Where(i => i.Estado == "ABERTO").OrderBy(i => i.Data).ThenBy(i => i.Id).ToListAsync(ct);
        if (itens.Count == 0) return [];
        var ids = itens.Where(i => i.TransacaoId is not null).Select(i => i.TransacaoId!.Value).ToList();
        // Sob o filtro SoftDelete: a pendente que sumiu (apagada) não mostra descrição (regra 9).
        var transacoes = await db.Transacoes.AsNoTracking().Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        var doCasal = (await contas.ListarAsync(ct)).ToDictionary(c => c.Id);
        return itens.Select(i =>
            {
                var linha = i.TransacaoId is { } t ? transacoes.GetValueOrDefault(t) : null;
                return new ItemAConferirDto(i.Id, i.Tipo, i.ContaId, doCasal.GetValueOrDefault(i.ContaId)?.Apelido ?? "Conta removida", i.Data,
                    i.ValorCentavos, linha?.LerDescricaoDoBanco(cofre), Opcoes(i, Indisponivel(i, linha)));
            })
            .ToList();
    }

    public async Task<ResultadoDeLancamento> ResolverAsync(Guid id, ResolucaoDto pedido, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pedido);
        if (atual.CasalId is not { } casalId) return new ResultadoDeLancamento.NaoEncontrado();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended('lancamentos.casal:' || {casalId}::text, 0))", ct);
            var item = await db.ItensAConferir.FirstOrDefaultAsync(i => i.Id == id, ct);
            if (item is null) return new ResultadoDeLancamento.NaoEncontrado();
            if (!item.Aberto) return new ResultadoDeLancamento.Feito(); // repetir não duplica
            var linhaDoItem = item.TransacaoId is { } idDaLinha ? await db.Transacoes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == idDaLinha, ct) : null;
            if (pedido.Opcao is not { } opcao || !Opcoes(item, Indisponivel(item, linhaDoItem)).Contains(opcao, StringComparer.Ordinal))
                return Invalido("opcao", "Escolha uma das opções deste item.");
            if (opcao == ResolucaoDoItem.DeixarParaDepois) return new ResultadoDeLancamento.Feito();

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
                // O item aberto do outro lado fecha junto (revisão do E6a, D1).
                var doOutro = await db.ItensAConferir.FirstOrDefaultAsync(i => i.TransacaoId == outro.Id && i.Estado == "ABERTO", ct);
                doOutro?.Resolver(opcao);
                return null;
            case [_, _, ..]:
                return new ResultadoDeLancamento.Recusado(TituloOutroLadoAmbiguo, 409);
        }
        // Nada veio do banco: conta ligada que não é o grupo só recebe o lado quando ele vier do banco (opção B).
        if (destino.Ligada && destino.Tipo != "INVESTIMENTO") return new ResultadoDeLancamento.Recusado(TituloSemOutroLado, 409);
        await pareamento.CriarParManualAsync(linha, destino, ct);
        return null;
    }

    private static ResultadoDeLancamento.Invalido Invalido(string campo, string mensagem) =>
        new(new Dictionary<string, string[]> { [campo] = [mensagem] });
}
