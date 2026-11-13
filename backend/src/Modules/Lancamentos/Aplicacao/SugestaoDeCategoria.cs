using GestorFinanceiro.Modules.Lancamentos.Dominio;
using GestorFinanceiro.Modules.Lancamentos.Infra;
using GestorFinanceiro.SharedKernel.Seguranca;
using Microsoft.EntityFrameworkCore;

namespace GestorFinanceiro.Modules.Lancamentos.Aplicacao;

/// <summary>
/// Sugestão de categoria para linhas novas do banco (Fase 2, Tarefa 5.1), em ordem: (1) a regra pela descrição do banco
/// normalizada, (2) o mapa pela categoria da Pluggy, (3) nada. Só preenche linha "a revisar" e sem categoria; ela continua
/// "a revisar" (fora do orçamento, D14). Nunca mexe no que o casal revisou. Categoria apagada, arquivada ou de outro tipo
/// não é sugerida.
/// </summary>
internal sealed class SugestaoDeCategoria(LancamentosDbContext db, ICofre cofre)
{
    public async Task SugerirAsync(Guid casalId, IReadOnlyList<Transacao> novas, CancellationToken ct)
    {
        var candidatas = novas.Where(t => t.Revisao == RevisaoDaTransacao.ARevisar && t.CategoriaId is null
                                          && t.Natureza != NaturezaDaTransacao.Transferencia).ToList();
        if (candidatas.Count == 0) return;

        // Poucas por casal: carregadas inteiras (sob os filtros Casal e SoftDelete).
        var regras = (await db.Regras.AsNoTracking().ToListAsync(ct))
            .ToDictionary(r => Convert.ToHexString(r.DescricaoHmac), r => r.CategoriaId, StringComparer.Ordinal);
        var mapa = (await db.Mapa.AsNoTracking().ToListAsync(ct))
            .ToDictionary(m => m.CategoriaExterna, m => m.CategoriaId, StringComparer.Ordinal);
        if (regras.Count == 0 && mapa.Count == 0) return;

        var sugestoes = new List<(Transacao T, Guid Categoria)>();
        foreach (var t in candidatas)
        {
            if (Normalizada(t, cofre) is { } normalizada
                && regras.TryGetValue(Convert.ToHexString(RegraDeCategoria.Indice(casalId, normalizada, cofre)), out var daRegra))
                sugestoes.Add((t, daRegra));
            else if (t.CategoriaExterna is { } codigo && mapa.TryGetValue(codigo, out var doMapa))
                sugestoes.Add((t, doMapa));
        }
        if (sugestoes.Count == 0) return;

        // A trava de cada categoria sugerida (a mesma do apagar categoria), em ordem, e só então a leitura: uma categoria
        // apagada em paralelo não recebe linha (revisão da E5, achado 2). Quem chama já tem as travas do casal e da conta.
        var ids = sugestoes.Select(s => s.Categoria).Distinct().Order().ToList();
        foreach (var id in ids)
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended('lancamentos.categoria:' || {id}::text, 0))", ct);
        var categorias = await db.Categorias.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);

        foreach (var (t, id) in sugestoes)
            if (categorias.TryGetValue(id, out var categoria) && !categoria.Arquivada && Serve(categoria, t.Natureza))
                t.SugerirCategoria(id);
    }

    /// <summary>A descrição do banco normalizada (a mesma da pendente, Tarefa 4.3); nula sem descrição.</summary>
    internal static string? Normalizada(Transacao t, ICofre cofre)
    {
        var doBanco = t.LerDescricaoDoBanco(cofre);
        if (doBanco == Transacao.SemDescricao) return null;
        var normalizada = Importacao.NormalizarDescricao(doBanco);
        return normalizada.Length == 0 ? null : normalizada;
    }

    /// <summary>
    /// A categoria serve ao lançamento: despesa em categoria de despesa; receita em categoria de receita ou, como estorno, de
    /// despesa sem papel (a mesma regra da edição).
    /// </summary>
    internal static bool Serve(Categoria categoria, NaturezaDaTransacao natureza) => natureza switch
    {
        NaturezaDaTransacao.Despesa => categoria.Tipo == TipoDeCategoria.Despesa,
        NaturezaDaTransacao.Receita => categoria.Tipo == TipoDeCategoria.Receita || categoria is { Tipo: TipoDeCategoria.Despesa, Papel: null },
        _ => false,
    };
}
