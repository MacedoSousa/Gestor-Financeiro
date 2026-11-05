using GestorFinanceiro.Modules.Identidade.Infra;
using GestorFinanceiro.SharedKernel.Aplicacao;

namespace GestorFinanceiro.Modules.Identidade.Aplicacao;

/// <summary>
/// Um cônjuge logado (com confirmação recente) gera um código de recuperação novo para o casal.
/// Na mesma transação o envelope ativo é marcado como removido e o novo é gravado.
/// </summary>
internal sealed class NovoCodigoDeRecuperacao(
    IdentidadeDbContext db, TentativaDeLogin tentativa, RotacaoDoCodigoDeRecuperacao rotacao, IUsuarioAtual atual)
{
    /// <returns>O código no formato exibido (mostrado uma única vez), ou null sem usuário na sessão.</returns>
    public async Task<string?> GerarAsync(CancellationToken ct)
    {
        if (atual.UsuarioId is not { } eu || atual.CasalId is not { } casalId) return null;
        ct.ThrowIfCancellationRequested();
        var semCancelar = CancellationToken.None;

        await using var transacao = await db.Database.BeginTransactionAsync(semCancelar);
        // Ordem dos locks: sempre o cônjuge primeiro, depois o casal (GirarAsync trava o casal).
        await tentativa.TravarAsync(eu);
        var codigo = await rotacao.GirarAsync(casalId);
        await transacao.CommitAsync(semCancelar);
        return codigo;
    }
}
