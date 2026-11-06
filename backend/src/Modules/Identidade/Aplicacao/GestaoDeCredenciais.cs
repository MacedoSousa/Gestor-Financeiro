using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.Modules.Identidade.Dominio;
using GestorFinanceiro.Modules.Identidade.Infra;
using GestorFinanceiro.SharedKernel.Aplicacao;
using Microsoft.EntityFrameworkCore;

namespace GestorFinanceiro.Modules.Identidade.Aplicacao;

internal enum ResultadoDaRemocao
{
    Removida,
    NaoEncontrada,
    UltimaCredencial,
}

/// <summary>Lista e remove as credenciais do próprio usuário logado.</summary>
internal sealed class GestaoDeCredenciais(IdentidadeDbContext db, TentativaDeLogin tentativa, IUsuarioAtual atual)
{
    /// <summary>Só as ativas e confirmadas do próprio usuário (os filtros SoftDelete e Casal continuam ligados).</summary>
    public async Task<IReadOnlyList<CredencialDto>> ListarAsync(CancellationToken ct)
    {
        if (atual.UsuarioId is not { } eu) return [];
        var itens = await db.Credenciais.AsNoTracking()
            .Where(c => c.UsuarioId == eu && c.Confirmada)
            .OrderBy(c => c.CriadoEm)
            .ToListAsync(ct);
        return itens.Select(c => new CredencialDto(c.Id, c.Tipo.ToString().ToUpperInvariant(), c.Apelido, c.CriadoEm, c.UltimoUsoEm)).ToList();
    }

    /// <summary>
    /// Soft delete da credencial. "É a última?" e a remoção acontecem sob o lock do próprio usuário
    /// (o mesmo do login), para que duas remoções paralelas não deixem o usuário sem forma de entrar.
    /// Na mesma transação encerra todas as outras sessões do usuário (só a <paramref name="sessaoAtual"/> fica):
    /// quem remove uma credencial costuma estar tirando um celular perdido, e a sessão dele não pode continuar valendo.
    /// </summary>
    public async Task<ResultadoDaRemocao> RemoverAsync(Guid id, Guid? sessaoAtual, CancellationToken ct)
    {
        if (atual.UsuarioId is not { } eu || sessaoAtual is not { } minhaSessao) return ResultadoDaRemocao.NaoEncontrada;
        ct.ThrowIfCancellationRequested();
        var semCancelar = CancellationToken.None;

        await using var transacao = await db.Database.BeginTransactionAsync(semCancelar);
        await tentativa.TravarAsync(eu);

        var credencial = await db.Credenciais.AsTracking().FirstOrDefaultAsync(c => c.Id == id && c.UsuarioId == eu, semCancelar);
        if (credencial is null)
        {
            await transacao.RollbackAsync(semCancelar);
            return ResultadoDaRemocao.NaoEncontrada;
        }

        if (credencial.Confirmada)
        {
            var outras = await db.Credenciais.AnyAsync(c => c.UsuarioId == eu && c.Confirmada && c.Id != id, semCancelar);
            if (!outras)
            {
                await transacao.RollbackAsync(semCancelar);
                return ResultadoDaRemocao.UltimaCredencial;
            }
        }

        db.Credenciais.Remove(credencial); // o SoftDeleteInterceptor transforma em is_deleted = true
        var outrasSessoes = await db.Sessoes.AsTracking()
            .Where(s => s.UsuarioId == eu && s.Id != minhaSessao)
            .ToListAsync(semCancelar);
        db.Sessoes.RemoveRange(outrasSessoes); // idem: is_deleted = true
        await db.SaveChangesAsync(semCancelar);
        await transacao.CommitAsync(semCancelar);
        return ResultadoDaRemocao.Removida;
    }
}
