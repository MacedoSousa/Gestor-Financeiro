using GestorFinanceiro.Modules.Contas.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Dominio;
using GestorFinanceiro.Modules.Lancamentos.Infra;
using GestorFinanceiro.SharedKernel.Aplicacao;
using Microsoft.EntityFrameworkCore;

namespace GestorFinanceiro.Modules.Lancamentos.Aplicacao;

/// <summary>
/// Reconhecimento de transferência e de pagamento de fatura (Fase 2, Tarefa 6.2), por casal (decisão 15: entre items e
/// conexões diferentes). Sob a trava do casal (a mesma da importação) e a das contas ligadas.
/// <list type="bullet">
/// <item>Candidatas: linhas do banco, confirmadas, ainda não transferência, que o casal não decidiu (a revisar, ou com código
/// da E6 que o casal revisou antes da D23, cuja categoria sai ao parear) e sem item resolvido pelo casal.</item>
/// <item>Par: saída e entrada do mesmo valor, em contas diferentes, até 1 dia útil, únicas dos dois lados. O cartão só recebe
/// (pagamento de fatura) e só com o código de fatura de um dos lados. Boleto, IOF e IR não pareiam.</item>
/// <item>Fatura e mesma titularidade sem par: com dois ou mais candidatos, "A conferir" já; sem nenhum, só depois de 2 dias
/// úteis (o outro banco pode sincronizar depois). Aplicação e resgate na corrente: "A conferir" (D12).</item>
/// <item>Item sem par que ganha par único é resolvido sozinho ("pareado automaticamente").</item>
/// </list>
/// </summary>
internal sealed class Reconhecimento(LancamentosDbContext db, IUsuarioAtual atual, IConsultaDeContas contas, TimeProvider relogio)
{
    internal const int DiasUteisDoPar = 1;
    internal const int DiasUteisParaConferir = 2;

    public async Task ReconhecerAsync(DateOnly desde, CancellationToken ct)
    {
        var casalId = atual.CasalId ?? throw new InvalidOperationException("Reconhecimento sem casal.");
        var hoje = Brasilia.Hoje(relogio);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended('lancamentos.casal:' || {casalId}::text, 0))", ct);
            var ligadas = (await contas.ListarAsync(ct)).Where(c => c.Ligada).ToDictionary(c => c.Id);
            if (ligadas.Count == 0) return;
            await TravaDeConta.TomarAsync(db, ligadas.Keys, ct);

            var ids = ligadas.Keys.ToList();
            var itens = await db.ItensAConferir.ToListAsync(ct);
            var decididas = itens.Where(i => !i.Aberto && i.Resolucao != ResolucaoDoItem.PareadoAutomaticamente && i.TransacaoId is not null)
                .Select(i => i.TransacaoId!.Value).ToHashSet();
            var linhas = (await db.Transacoes
                    .Where(t => t.Origem == OrigemDaTransacao.OpenFinance && t.Natureza != NaturezaDaTransacao.Transferencia
                                && t.Confirmacao == ConfirmacaoDaTransacao.Confirmada && t.Parcela == null && t.Data >= desde && ids.Contains(t.ContaId))
                    .ToListAsync(ct))
                .Where(t => (t.Revisao == RevisaoDaTransacao.ARevisar || CategoriasDoBanco.ReconhecidoNaProximaEtapa(t.CategoriaExterna))
                            && !decididas.Contains(t.Id))
                .ToList();

            bool Cartao(Transacao t) => ligadas[t.ContaId].Tipo == "CARTAO";
            PapelDaCategoriaDoBanco Papel(Transacao t) => CategoriasDoBanco.Papel(t.CategoriaExterna);
            bool Pareia(Transacao t) => Papel(t) is not (PapelDaCategoriaDoBanco.Boleto or PapelDaCategoriaDoBanco.Iof or PapelDaCategoriaDoBanco.ImpostoDeRenda);
            bool Compativel(Transacao s, Transacao e) =>
                s.ContaId != e.ContaId && e.ValorCentavos == -s.ValorCentavos && DiasUteis.Distancia(s.Data, e.Data) <= DiasUteisDoPar
                && (!Cartao(e) || Papel(s) == PapelDaCategoriaDoBanco.PagamentoDeFatura || Papel(e) == PapelDaCategoriaDoBanco.PagamentoDeFatura);

            var saidas = linhas.Where(t => t.ValorCentavos < 0 && !Cartao(t) && Pareia(t)).ToList();
            var entradas = linhas.Where(t => t.ValorCentavos > 0 && Pareia(t)).ToList();
            var candidatosDaSaida = saidas.ToDictionary(s => s, s => entradas.Where(e => Compativel(s, e)).ToList());
            var candidatosDaEntrada = entradas.ToDictionary(e => e, e => saidas.Where(s => Compativel(s, e)).ToList());

            var pareadas = new HashSet<Guid>();
            foreach (var (s, cs) in candidatosDaSaida)
            {
                if (cs is not [var e] || candidatosDaEntrada[e] is not [_]) continue;
                Pareamento.Parear(s, e);
                pareadas.Add(s.Id);
                pareadas.Add(e.Id);
            }

            // Item aberto de uma linha que acabou de parear, ou que já virou transferência por outro caminho: resolvido sozinho
            // (revisão do E6a, D1).
            var abertas = itens.Where(i => i.Aberto && i.TransacaoId is not null).Select(i => i.TransacaoId!.Value).ToList();
            var jaTransferencia = (await db.Transacoes.AsNoTracking()
                    .Where(t => abertas.Contains(t.Id) && t.Natureza == NaturezaDaTransacao.Transferencia).Select(t => t.Id).ToListAsync(ct))
                .ToHashSet();
            foreach (var item in itens.Where(i => i.Aberto && i.TransacaoId is { } t && (pareadas.Contains(t) || jaTransferencia.Contains(t))))
                item.Resolver(ResolucaoDoItem.PareadoAutomaticamente);

            var chaves = itens.Select(i => i.Chave).ToHashSet(StringComparer.Ordinal);
            void Abrir(string tipo, string chave, Transacao t)
            {
                if (!chaves.Add(chave)) return;
                db.ItensAConferir.Add(ItemAConferir.Criar(casalId, tipo, chave, t.ContaId, t.Id, t.ValorCentavos, t.Data));
            }

            foreach (var t in linhas.Where(t => !pareadas.Contains(t.Id)))
            {
                var papel = Papel(t);
                if (papel is PapelDaCategoriaDoBanco.PagamentoDeFatura or PapelDaCategoriaDoBanco.MesmaTitularidade)
                {
                    var candidatos = t.ValorCentavos < 0 ? candidatosDaSaida.GetValueOrDefault(t)?.Count ?? 0 : candidatosDaEntrada.GetValueOrDefault(t)?.Count ?? 0;
                    if (candidatos >= 2 || DiasUteis.Entre(t.Data, hoje) >= DiasUteisParaConferir)
                        Abrir(TipoDeItemAConferir.TransferenciaSemPar, ItemAConferir.ChaveSemPar(t.Id), t);
                }
                else if ((papel is PapelDaCategoriaDoBanco.Aplicacao or PapelDaCategoriaDoBanco.ResgateOuRendimento) && !Cartao(t))
                {
                    Abrir(TipoDeItemAConferir.AporteOuResgate, ItemAConferir.ChaveMovimento(t.Id), t);
                }
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }
}
