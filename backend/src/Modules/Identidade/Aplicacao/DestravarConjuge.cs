using GestorFinanceiro.Modules.Identidade.Infra;
using GestorFinanceiro.SharedKernel.Aplicacao;
using Microsoft.EntityFrameworkCore;

namespace GestorFinanceiro.Modules.Identidade.Aplicacao;

/// <summary>O cônjuge logado (com confirmação recente) destrava o outro na hora; a contagem de bloqueios continua.</summary>
internal sealed class DestravarConjuge(IdentidadeDbContext db, TentativaDeLogin tentativa, IUsuarioAtual atual)
{
    /// <returns>false se o usuário da sessão não tiver cônjuge.</returns>
    public async Task<bool> ExecutarAsync(CancellationToken ct)
    {
        if (atual.UsuarioId is not { } eu) return false;
        // Filtro Casal ligado: o cônjuge só é encontrado no casal da sessão.
        var conjugeId = await db.Usuarios.AsNoTracking().Where(u => u.Id == eu).Select(u => u.ConjugeId).FirstOrDefaultAsync(ct);
        if (conjugeId is not { } id) return false;

        // Com o lock do cônjuge, uma tentativa de login em curso não sobrescreve o destravamento (nem o contrário).
        var semCancelar = CancellationToken.None;
        await using var transacao = await db.Database.BeginTransactionAsync(semCancelar);
        await tentativa.TravarAsync(id);
        var conjuge = await db.Usuarios.AsTracking().FirstOrDefaultAsync(u => u.Id == id, semCancelar);
        if (conjuge is null)
        {
            await transacao.RollbackAsync(semCancelar);
            return false;
        }
        await db.Entry(conjuge).ReloadAsync(semCancelar);
        conjuge.Destravar();
        await db.SaveChangesAsync(semCancelar);
        await transacao.CommitAsync(semCancelar);
        return true;
    }
}
