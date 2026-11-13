using GestorFinanceiro.Modules.Contas.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Dominio;
using Microsoft.EntityFrameworkCore;

namespace GestorFinanceiro.Modules.Lancamentos.Infra;

/// <summary>
/// Uso do mês para o orçamento. Tudo é SUM no banco, sob os filtros SoftDelete e Casal do contexto. Só o que o casal já
/// revisou: "a revisar" fica fora do uso, da receita e dos aportes; "a confirmar" (pendente no banco) conta (D14).
/// </summary>
internal sealed class ConsultaDeUso(LancamentosDbContext db, IConsultaDeContas contas) : IConsultaDeUso
{
    public async Task<IReadOnlyList<UsoDaCategoria>> UsoPorCategoriaAsync(DateOnly mes, CancellationToken ct = default)
    {
        var fim = Fim(mes);
        return await (from t in db.Transacoes.AsNoTracking()
                      join c in db.Categorias on t.CategoriaId equals c.Id
                      where c.Tipo == TipoDeCategoria.Despesa && t.Data >= mes && t.Data < fim && t.Revisao == RevisaoDaTransacao.Revisada
                      group t by c.Id into g
                      select new UsoDaCategoria(g.Key, -g.Sum(t => t.ValorCentavos)))
            .ToListAsync(ct);
    }

    public async Task<long> ReceitasAsync(DateOnly mes, CancellationToken ct = default)
    {
        var fim = Fim(mes);
        var guardadas = await GuardadasAsync(ct);
        return await (from t in db.Transacoes.AsNoTracking()
                      join c in db.Categorias on t.CategoriaId equals c.Id
                      where c.Tipo == TipoDeCategoria.Receita && t.Natureza == NaturezaDaTransacao.Receita && t.Revisao == RevisaoDaTransacao.Revisada
                            && t.Data >= mes && t.Data < fim && !guardadas.Contains(t.ContaId)
                      select (long?)t.ValorCentavos)
            .SumAsync(ct) ?? 0;
    }

    public async Task<IReadOnlyList<AporteDaConta>> AportesPorContaAsync(DateOnly mes, CancellationToken ct = default)
    {
        var fim = Fim(mes);
        var guardadas = await GuardadasAsync(ct);
        if (guardadas.Length == 0) return [];

        var transacoes = db.Transacoes.AsNoTracking();
        return await transacoes
            .Where(t => t.Natureza == NaturezaDaTransacao.Transferencia && t.ValorCentavos > 0 && t.Revisao == RevisaoDaTransacao.Revisada
                        && guardadas.Contains(t.ContaId) && t.Data >= mes && t.Data < fim
                        && transacoes.Any(o => o.GrupoId == t.GrupoId && o.ValorCentavos < 0 && !guardadas.Contains(o.ContaId)))
            .GroupBy(t => t.ContaId)
            .Select(g => new AporteDaConta(g.Key, g.Sum(t => t.ValorCentavos)))
            .ToListAsync(ct);
    }

    public async Task<long> AportesAsync(DateOnly mes, CancellationToken ct = default) =>
        (await AportesPorContaAsync(mes, ct)).Sum(a => a.AporteCentavos);

    private async Task<Guid[]> GuardadasAsync(CancellationToken ct) =>
        (await contas.ListarAsync(ct)).Where(c => c.Tipo is "POUPANCA" or "INVESTIMENTO").Select(c => c.Id).ToArray();

    private static DateOnly Fim(DateOnly mes)
    {
        if (mes.Day != 1) throw new ArgumentException("O mês é sempre o dia 1.", nameof(mes));
        return mes.AddMonths(1);
    }
}
