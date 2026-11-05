namespace GestorFinanceiro.Modules.Identidade.Contracts;

/// <summary>O que outros módulos podem saber sobre os cônjuges. Nunca expõe CPF.</summary>
public interface IConsultaDeUsuarios
{
    /// <summary>Usado pelo bot: identifica quem mandou a mensagem pelo telefone (E.164).</summary>
    Task<UsuarioResumo?> ObterPorTelefoneAsync(string telefoneE164, CancellationToken ct = default);

    Task<IReadOnlyList<UsuarioResumo>> ListarDoCasalAsync(Guid casalId, CancellationToken ct = default);
}

/// <param name="Apelido">Primeiro nome, para exibição e para mensagens do bot.</param>
public sealed record UsuarioResumo(Guid UsuarioId, Guid CasalId, string Apelido, Guid? ConjugeId);

/// <summary>Resposta de GET /api/v1/casal.</summary>
public sealed record CasalDto(Guid Id, IReadOnlyList<MembroDto> Membros);

public sealed record MembroDto(Guid Id, string Apelido, bool EhVoce);
