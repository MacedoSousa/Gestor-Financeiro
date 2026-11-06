using GestorFinanceiro.Modules.Identidade.Dominio;
using GestorFinanceiro.Modules.Identidade.Infra;
using GestorFinanceiro.SharedKernel.Persistencia;
using Microsoft.EntityFrameworkCore;

namespace GestorFinanceiro.Modules.Identidade.Aplicacao;

internal enum ResultadoDaTentativa
{
    Sucesso,
    Falha,
    Bloqueado,
    /// <summary>O impedimento específico do fluxo (ex.: primeiro acesso encerrado) se aplicou; nada foi contado.</summary>
    Impedido,
}

/// <summary>Se o bloqueio por falhas vale para a conferência (ADR-011).</summary>
internal enum RegraDeBloqueio
{
    /// <summary>Senha, TOTP e código de recuperação: conta bloqueada não confere, e erro conta falha.</summary>
    Aplica,

    /// <summary>
    /// Passkey: confere mesmo com a conta bloqueada e erro não conta falha. O sucesso zera o contador e encerra o
    /// bloqueio, como qualquer sucesso.
    /// </summary>
    NaoSeAplica,
}

/// <summary>
/// Toda conferência de segredo de login passa por aqui. Serializa as tentativas por cônjuge
/// (advisory lock na transação), recarrega o usuário já com o lock e só então confere e conta,
/// para que tentativas paralelas não percam falhas nem passem do bloqueio.
/// </summary>
internal sealed class TentativaDeLogin(IdentidadeDbContext db, TimeProvider relogio)
{
    /// <param name="conferir">A conferência cara (Argon2, TOTP...). Roda com o lock e o usuário recarregado.</param>
    /// <param name="impedido">Regra do fluxo avaliada depois do bloqueio e antes da conferência; não conta falha.</param>
    /// <param name="regra">Passkey usa <see cref="RegraDeBloqueio.NaoSeAplica"/>; o resto, o padrão.</param>
    public async Task<ResultadoDaTentativa> ExecutarAsync(
        Guid usuarioId,
        Func<Usuario, CancellationToken, Task<bool>> conferir,
        Func<Usuario, bool>? impedido = null,
        RegraDeBloqueio regra = RegraDeBloqueio.Aplica,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // Daqui em diante sem o token da requisição: uma desconexão não pode deixar estado pela metade.
        var semCancelar = CancellationToken.None;

        await using var transacao = await db.Database.BeginTransactionAsync(semCancelar);
        await TravarAsync(usuarioId);

        // O casal ainda não é conhecido: só o filtro de soft delete continua ativo.
        var usuario = await db.Usuarios
            .IgnoreQueryFilters([Filtros.Casal])
            .AsTracking()
            .FirstOrDefaultAsync(u => u.Id == usuarioId, semCancelar);
        if (usuario is null)
        {
            await transacao.RollbackAsync(semCancelar);
            return ResultadoDaTentativa.Falha;
        }
        await db.Entry(usuario).ReloadAsync(semCancelar);

        var agora = relogio.GetUtcNow();
        var contaFalhas = regra == RegraDeBloqueio.Aplica;
        if (contaFalhas && usuario.EstaBloqueado(agora))
        {
            await transacao.RollbackAsync(semCancelar);
            return ResultadoDaTentativa.Bloqueado;
        }
        if (impedido?.Invoke(usuario) == true)
        {
            await transacao.RollbackAsync(semCancelar);
            return ResultadoDaTentativa.Impedido;
        }

        var ok = await conferir(usuario, semCancelar);
        if (!ok && !contaFalhas)
        {
            await transacao.RollbackAsync(semCancelar);
            return ResultadoDaTentativa.Falha;
        }
        if (ok) usuario.RegistrarSucesso();
        else usuario.RegistrarFalha(agora);
        await db.SaveChangesAsync(semCancelar);
        await transacao.CommitAsync(semCancelar);
        return ok ? ResultadoDaTentativa.Sucesso : ResultadoDaTentativa.Falha;
    }

    /// <summary>
    /// O lock por cônjuge, na transação já aberta pelo chamador. Quem mexe no estado de login de um cônjuge
    /// fora de uma tentativa (destravar, resgatar pedido) usa o mesmo lock, para não sobrescrever uma tentativa em curso.
    /// </summary>
    public async Task TravarAsync(Guid usuarioId)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("O lock do cônjuge exige uma transação aberta.");
        var chave = $"identidade.login:{usuarioId}";
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({chave}, 0))", CancellationToken.None);
    }
}
