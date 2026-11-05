using GestorFinanceiro.Modules.Identidade.Dominio;
using GestorFinanceiro.Modules.Identidade.Infra;
using GestorFinanceiro.SharedKernel.Persistencia;
using GestorFinanceiro.SharedKernel.Seguranca;
using Microsoft.EntityFrameworkCore;

namespace GestorFinanceiro.Modules.Identidade.Aplicacao;

/// <summary>
/// Troca o código de recuperação do casal dentro da transação de quem chama: o envelope RECUPERACAO ativo
/// é marcado como removido e um novo, derivado da forma canônica do código novo, é gravado.
/// Usado ao entrar com o código de recuperação e ao gerar um código novo estando logado.
/// </summary>
internal sealed class RotacaoDoCodigoDeRecuperacao(IdentidadeDbContext db, IEnvelopesDaDek envelopes)
{
    /// <summary>
    /// Serializa quem lê ou troca o envelope de recuperação do casal (os dois cônjuges compartilham um só).
    /// Locks consultivos de transação são reentrantes: chamar de novo na mesma transação não trava.
    /// </summary>
    public async Task TravarAsync(Guid casalId)
    {
        ExigirTransacao();
        var chave = $"identidade.recuperacao:{casalId}";
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({chave}, 0))", CancellationToken.None);
    }

    /// <summary>O envelope RECUPERACAO ativo do casal (rastreado), ou null.</summary>
    public Task<ChaveDoCasal?> EnvelopeAtivoAsync(Guid casalId) =>
        // Também é chamado sem sessão (recuperação): o casal vem do usuário e entra explícito no filtro.
        db.Chaves
            .IgnoreQueryFilters([Filtros.Casal])
            .AsTracking()
            .FirstOrDefaultAsync(c => c.CasalId == casalId && c.Tipo == TipoChave.Recuperacao, CancellationToken.None);

    /// <summary>Gira o código e devolve o novo no formato exibido (com hífens). Exige a transação do chamador.</summary>
    public async Task<string> GirarAsync(Guid casalId)
    {
        await TravarAsync(casalId);
        var semCancelar = CancellationToken.None;

        // Primeiro o soft delete, gravado à parte: o índice único parcial só aceita o novo depois disso.
        if (await EnvelopeAtivoAsync(casalId) is { } ativo)
        {
            db.Chaves.Remove(ativo); // o SoftDeleteInterceptor transforma em is_deleted = true
            await db.SaveChangesAsync(semCancelar);
        }

        var codigo = EnvelopeDeChaves.GerarCodigoRecuperacao();
        var envelope = envelopes.Embrulhar(EnvelopeDeChaves.CanonizarCodigo(codigo));
        db.Chaves.Add(ChaveDoCasal.Criar(casalId, TipoChave.Recuperacao, null, envelope.DekCifrada, envelope.Salt));
        await db.SaveChangesAsync(semCancelar);
        return codigo;
    }

    private void ExigirTransacao()
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A rotação do código de recuperação exige a transação do chamador.");
    }
}
