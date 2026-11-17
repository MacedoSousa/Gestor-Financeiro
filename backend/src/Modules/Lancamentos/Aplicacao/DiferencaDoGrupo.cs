using GestorFinanceiro.Modules.Contas.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Dominio;
using GestorFinanceiro.Modules.Lancamentos.Infra;
using GestorFinanceiro.SharedKernel.Aplicacao;
using Microsoft.EntityFrameworkCore;

namespace GestorFinanceiro.Modules.Lancamentos.Aplicacao;

/// <summary>
/// A diferença do grupo de investimento em "A conferir" (Fase 2, Tarefa 6.3, CEO, D25): valor no banco (só ACTIVE) − saldo
/// do sistema. No máximo um item aberto por grupo, recalculado a cada leitura e depois de cada ação que muda o saldo do
/// grupo; diferença zero fecha o item. Nada é lançado aqui: rendimento, IR/IOF, par e ajuste são ações do casal
/// (<see cref="AConferir"/>). Sob a trava da conta (a mesma do Contas). Nada pessoal em log.
/// </summary>
internal sealed class DiferencaDoGrupo(LancamentosDbContext db, IUsuarioAtual atual, IConsultaDeContas contas, ConsultaDeSaldosPorConta saldos,
    TimeProvider relogio)
{
    /// <summary>Leitura da sincronização: cria, atualiza ou fecha ("resolvido pelo banco") o item aberto do grupo.</summary>
    public async Task ConferirAsync(Guid grupoId, DateOnly dia, long valorNoBanco, CancellationToken ct)
    {
        var casalId = atual.CasalId ?? throw new InvalidOperationException("Diferença do grupo sem casal.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Casal → conta, a ordem do módulo: a leitura espera a resolução em "A conferir" (que segura a trava do casal, e no
            // "ajustar o saldo inicial" chama o Contas sem a da conta), e nunca troca o item no meio dela (revisão do E6b, achado 1).
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended('lancamentos.casal:' || {casalId}::text, 0))", ct);
            await TravaDeConta.TomarAsync(db, [grupoId], ct);
            var grupo = await contas.ObterAsync(grupoId, ct);
            if (grupo is not { Ligada: true, Tipo: "INVESTIMENTO" }) throw new InvalidOperationException("Diferença só de grupo de investimento ligado do casal.");
            if (grupo.Arquivada) return; // o item que houver fica como está; a ação dá 409

            var diferenca = checked(valorNoBanco - (await saldos.SaldosAsync([grupo], dia, ct))[grupo.Id]);
            var aberto = await AbertoAsync(grupoId, ct);
            if (aberto is not null)
            {
                aberto.Recalcular(diferenca, valorNoBanco, dia);
                if (diferenca == 0) aberto.Resolver(ResolucaoDoItem.ResolvidoPeloBanco); // sem lançamento; a auditoria é a da tabela
            }
            else if (diferenca != 0)
            {
                var vezes = await db.ItensAConferir.CountAsync(i => i.ContaId == grupoId && i.Tipo == TipoDeItemAConferir.DiferencaDoGrupo, ct);
                db.ItensAConferir.Add(ItemAConferir.CriarDiferencaDoGrupo(casalId, grupoId, vezes + 1, diferenca, valorNoBanco, dia));
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>A diferença de agora: o valor no banco da última leitura menos o saldo do sistema hoje (o que já foi gravado).</summary>
    public async Task<long> DeAgoraAsync(ItemAConferir item, CancellationToken ct) =>
        await DeAgoraAsync(item, await contas.ObterAsync(item.ContaId, ct) ?? throw new InvalidOperationException("Grupo não encontrado no casal."), ct);

    /// <summary>
    /// O mesmo, com o grupo já lido: quem troca o saldo inicial usa o mesmo retrato para a diferença e para o saldo esperado
    /// (revisão do E6b pela D25, rodada 2).
    /// </summary>
    public async Task<long> DeAgoraAsync(ItemAConferir item, ContaResumo grupo, CancellationToken ct)
    {
        if (item.ValorBancoCentavos is not { } noBanco) throw new InvalidOperationException("Só a diferença do grupo tem valor no banco.");
        if (grupo.Id != item.ContaId) throw new ArgumentException("O grupo é o do item.", nameof(grupo));
        return checked(noBanco - (await saldos.SaldosAsync([grupo], Brasilia.Hoje(relogio), ct))[grupo.Id]);
    }

    /// <summary>
    /// Depois de uma ação que mexe no saldo do grupo (um aporte ou resgate pareado com ele), o item aberto acompanha: grava o
    /// que está pendente, recalcula e, se zerou, fecha como pareado com o movimento. Quem chama tem a trava da conta do grupo.
    /// </summary>
    public async Task AcompanharAsync(Guid grupoId, CancellationToken ct)
    {
        if (await AbertoAsync(grupoId, ct) is not { } aberto) return;
        await db.SaveChangesAsync(ct);
        var agora = await DeAgoraAsync(aberto, ct);
        aberto.Recalcular(agora);
        if (agora == 0) aberto.Resolver(ResolucaoDoItem.PareadoComMovimento);
    }

    private Task<ItemAConferir?> AbertoAsync(Guid grupoId, CancellationToken ct) =>
        db.ItensAConferir.FirstOrDefaultAsync(i => i.ContaId == grupoId && i.Tipo == TipoDeItemAConferir.DiferencaDoGrupo && i.Estado == "ABERTO", ct);
}
