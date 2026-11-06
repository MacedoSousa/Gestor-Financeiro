using GestorFinanceiro.Modules.Lancamentos.Aplicacao;
using GestorFinanceiro.Modules.Lancamentos.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Dominio;
using GestorFinanceiro.SharedKernel.Aplicacao;
using Microsoft.EntityFrameworkCore;

namespace GestorFinanceiro.Modules.Lancamentos.Infra;

internal sealed class ConsultaDeCategorias(LancamentosDbContext db, ServicoDeCategorias categorias, IUsuarioAtual atual) : IConsultaDeCategorias
{
    public async Task<IReadOnlyList<CategoriaDeDespesa>> ListarDeDespesaAsync(CancellationToken ct = default)
    {
        if (atual.CasalId is not { } casalId) return [];
        await categorias.GarantirPadraoAsync(casalId, ct);
        return await db.Categorias.AsNoTracking()
            .Where(c => c.Tipo == TipoDeCategoria.Despesa)
            .OrderBy(c => c.Nome)
            .Select(c => new CategoriaDeDespesa(c.Id, c.Nome, c.Papel, c.Arquivada))
            .ToListAsync(ct);
    }
}
