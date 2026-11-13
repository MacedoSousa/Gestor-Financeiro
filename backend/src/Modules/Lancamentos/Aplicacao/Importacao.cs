using System.Globalization;
using System.Text;
using GestorFinanceiro.Modules.Contas.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Dominio;
using GestorFinanceiro.Modules.Lancamentos.Infra;
using GestorFinanceiro.SharedKernel.Aplicacao;
using GestorFinanceiro.SharedKernel.Persistencia;
using GestorFinanceiro.SharedKernel.Seguranca;
using Microsoft.EntityFrameworkCore;

namespace GestorFinanceiro.Modules.Lancamentos.Aplicacao;

/// <summary>
/// Importação do banco (Fase 2, Tarefa 4.3): "criar ou atualizar pela chave natural" (OPEN_FINANCE, id da Pluggy), numa
/// transação, sob a trava do casal (a mesma da conciliação) e da conta (a mesma do apagar conta). Única exceção à regra 9
/// para o filtro SoftDelete (D6): a chave é procurada também entre as apagadas, para não recriá-las (a apagada pelo casal ou
/// promovida) ou para trazê-las de volta (a apagada por ausência no banco, D19); nada da linha apagada sai daqui. Nenhum
/// valor, descrição ou id do banco vai para log ou exceção.
/// </summary>
internal sealed class Importacao(LancamentosDbContext db, IUsuarioAtual atual, IConsultaDeContas contas, ICofre cofre)
    : IImportacaoDeLancamentos
{
    /// <summary>Pendente → definitiva: a data da definitiva fica até 3 dias da pendente (spec, números da Fase 2).</summary>
    internal const int DiasDaPendente = 3;

    private const int IdExternoMaximo = 100;

    public async Task<ResumoDaImportacao> ImportarAsync(Guid contaId, DateOnly desde, IReadOnlyList<LancamentoImportado> lote, bool apagarAusentes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lote);
        var casalId = atual.CasalId ?? throw new InvalidOperationException("Importação sem casal.");
        Validar(lote);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended('lancamentos.casal:' || {casalId}::text, 0))", ct);
            await TravaDeConta.TomarAsync(db, [contaId], ct);
            var conta = await contas.ObterAsync(contaId, ct);
            if (conta is not { Ligada: true }) throw new InvalidOperationException("A conta não existe no casal ou não é ligada.");

            var resumo = await AplicarAsync(casalId, conta, desde, lote, apagarAusentes, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return resumo;
        }
        finally
        {
            // Falhou ou não, o escopo pode importar o próximo vínculo: nada do lote fica rastreado.
            db.ChangeTracker.Clear();
        }
    }

    private async Task<ResumoDaImportacao> AplicarAsync(Guid casalId, ContaResumo conta, DateOnly desde, IReadOnlyList<LancamentoImportado> lote,
        bool apagarAusentes, CancellationToken ct)
    {
        int criados = 0, atualizados = 0, ignorados = 0, apagados = 0, promovidas = 0, semPar = 0, restaurados = 0;
        var chaves = lote.Select(l => l.IdExterno).ToArray();

        // D6: também as apagadas, para não recriar; o filtro Casal continua ligado.
        var existentes = (await db.Transacoes.IgnoreQueryFilters([Filtros.SoftDelete])
                .Where(t => t.Origem == OrigemDaTransacao.OpenFinance && chaves.Contains(t.IdExterno))
                .ToListAsync(ct))
            .GroupBy(t => t.IdExterno, StringComparer.Ordinal)
            // Uma chave pode ter uma apagada e (de antes desta regra) uma ativa: a ativa vale.
            // Duas apagadas: a que não volta vale (a do casal ganha da ausência).
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.IsDeleted).ThenBy(t => t.PodeVoltarDoBanco).First(), StringComparer.Ordinal);

        var novas = new List<Transacao>();
        foreach (var linha in lote)
        {
            if (linha.ValorCentavos == 0)
            {
                ignorados++;
                continue;
            }

            var fatura = contas.VencimentoDaFatura(conta, linha.Data);
            if (existentes.TryGetValue(linha.IdExterno, out var existente))
            {
                // De outra conta do casal não se move; apagada pelo casal (ou promovida) não se recria (D6, D19).
                if (existente.ContaId != conta.Id || (existente.IsDeleted && !existente.PodeVoltarDoBanco))
                {
                    ignorados++;
                    continue;
                }
                // Apagada por ausência e o banco mandou de novo: volta, com a revisão do casal (D19).
                var voltou = existente.IsDeleted;
                if (voltou) existente.VoltarDoBanco();
                existente.ZerarAusencias();
                var mudou = existente.AtualizarDoBanco(linha.Data, linha.ValorCentavos, linha.Descricao, linha.AConfirmar, linha.Parcela,
                    linha.Parcelas, linha.CategoriaExterna, linha.FaturaIdExterno, fatura, cofre);
                if (voltou) restaurados++;
                else if (mudou) atualizados++;
                continue;
            }

            var nova = Transacao.Importar(casalId, conta.Id, linha.Data, linha.ValorCentavos, linha.Descricao, linha.IdExterno, linha.AConfirmar,
                linha.Parcela, linha.Parcelas, linha.CategoriaExterna, linha.FaturaIdExterno, fatura, cofre);
            db.Transacoes.Add(nova);
            novas.Add(nova);
            criados++;
        }

        // Janela: o que o banco tinha na conta desde "desde" e não mandou agora.
        var naJanela = await db.Transacoes
            .Where(t => t.ContaId == conta.Id && t.Origem == OrigemDaTransacao.OpenFinance && t.Data >= desde && !chaves.Contains(t.IdExterno))
            .ToListAsync(ct);
        var sumidas = naJanela.Where(t => t.Confirmacao == ConfirmacaoDaTransacao.Confirmada).ToList();
        var pendentes = naJanela.Where(t => t.Confirmacao == ConfirmacaoDaTransacao.AConfirmar).ToList();

        // D19: só a segunda sincronização completa seguida sem a linha a apaga, e ela volta se o banco a mandar de novo. Lote
        // vazio com a janela cheia é resposta suspeita do banco, e quem chama pode saber que o lote veio incompleto: nesses
        // casos nem a ausência conta (revisão da E4, achado 4).
        if (!apagarAusentes || lote.Count == 0) sumidas.Clear();
        foreach (var t in sumidas)
        {
            if (!t.RegistrarAusencia()) continue;
            db.Transacoes.Remove(t); // SoftDeleteInterceptor: is_deleted = true, com auditoria; o motivo já está marcado.
            apagados++;
        }

        // Definitivas candidatas: as confirmadas do banco que chegaram ou continuam neste lote.
        var definitivas = novas.Where(t => t.Confirmacao == ConfirmacaoDaTransacao.Confirmada)
            .Concat(existentes.Values.Where(t => !t.IsDeleted && t.ContaId == conta.Id && t.Confirmacao == ConfirmacaoDaTransacao.Confirmada))
            .ToList();
        var pares = pendentes.ToDictionary(p => p, p => definitivas.Where(d => Par(p, d)).ToList());
        // Par único dos dois lados: a definitiva também não pode servir a outra pendente.
        var disputadas = pares.Values.Where(l => l.Count == 1).GroupBy(l => l[0]).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        foreach (var (pendente, candidatas) in pares)
        {
            if (candidatas.Count != 1 || disputadas.Contains(candidatas[0]))
            {
                semPar++;
                continue;
            }
            candidatas[0].HerdarRevisao(pendente, cofre);
            pendente.MarcarApagamento(MotivoDoApagamento.PendentePromovida);
            db.Transacoes.Remove(pendente);
            promovidas++;
        }

        return new ResumoDaImportacao(criados, atualizados, ignorados, apagados, promovidas, semPar, restaurados);
    }

    /// <summary>Mesma conta (já filtrada), mesmo valor, data até 3 dias e descrição do banco parecida (a do casal não conta).</summary>
    private bool Par(Transacao pendente, Transacao definitiva) =>
        pendente.ValorCentavos == definitiva.ValorCentavos
        && Math.Abs(pendente.Data.DayNumber - definitiva.Data.DayNumber) <= DiasDaPendente
        && Parecidas(pendente.LerDescricaoDoBanco(cofre), definitiva.LerDescricaoDoBanco(cofre));

    /// <summary>Normalizadas (maiúsculas, sem acento, sem dígitos, espaços colapsados), iguais ou uma prefixo da outra.</summary>
    internal static bool Parecidas(string a, string b)
    {
        var x = NormalizarDescricao(a);
        var y = NormalizarDescricao(b);
        if (x.Length == 0 || y.Length == 0) return false;
        return x.StartsWith(y, StringComparison.Ordinal) || y.StartsWith(x, StringComparison.Ordinal);
    }

    internal static string NormalizarDescricao(string texto)
    {
        var sb = new StringBuilder(texto.Length);
        foreach (var ch in texto.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark || char.IsDigit(ch)) continue;
            sb.Append(char.ToUpperInvariant(ch));
        }
        return string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Chave presente, curta e sem repetição no lote. Mensagens sem nenhum dado da linha (regra 5).</summary>
    private static void Validar(IReadOnlyList<LancamentoImportado> lote)
    {
        var vistas = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < lote.Count; i++)
        {
            var id = lote[i]?.IdExterno;
            if (string.IsNullOrWhiteSpace(id) || id.Length > IdExternoMaximo)
                throw new ArgumentException($"Linha {i + 1} do lote sem chave válida.", nameof(lote));
            if (!vistas.Add(id)) throw new ArgumentException($"Linha {i + 1} do lote repete uma chave.", nameof(lote));
        }
    }
}
