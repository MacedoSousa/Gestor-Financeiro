namespace GestorFinanceiro.Modules.Identidade.Contracts;

/// <summary>Valida o token do cookie de sessão. Usado pelo handler de autenticação do Host.</summary>
public interface ISessoes
{
    /// <summary>
    /// Devolve null se o token não existe, a sessão foi encerrada, passou da expiração absoluta
    /// ou ficou 7 dias sem uso. Regrava o último uso só quando passou de 1 h.
    /// </summary>
    Task<SessaoValida?> ValidarAsync(string token, CancellationToken ct);
}

public sealed record SessaoValida(Guid SessaoId, Guid UsuarioId, Guid CasalId, DateTimeOffset ConfirmadaEm);

/// <summary>Nomes de cookie e de claims compartilhados entre o módulo e o Host.</summary>
public static class NomesDoLogin
{
    public const string Esquema = "Sessao";
    public const string CookieDeSessao = "__Host-gf_sessao";
    public const string CookieDeRegistro = "__Host-gf_registro";
    public const string ClaimCasal = "casal_id";
    public const string ClaimSessao = "sessao_id";
    public const string ClaimConfirmadaEm = "confirmada_em";
}

/// <summary>Resposta de GET /api/v1/auth/eu.</summary>
public sealed record EuDto(Guid UsuarioId, string Apelido, string ApelidoDoConjuge, bool ConfirmacaoRecente);
