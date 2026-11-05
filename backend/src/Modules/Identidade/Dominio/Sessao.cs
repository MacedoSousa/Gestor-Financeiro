using GestorFinanceiro.SharedKernel.Dominio;

namespace GestorFinanceiro.Modules.Identidade.Dominio;

/// <summary>Sessão de login. Guarda só o HMAC do token: nem token, nem IP, nem user agent.</summary>
internal sealed class Sessao : Entidade, ICasalScoped
{
    public Guid CasalId { get; private set; }
    public Guid UsuarioId { get; private set; }
    public byte[] TokenHmac { get; private set; } = [];
    public DateTimeOffset ExpiraEm { get; private set; }
    public DateTimeOffset UltimoUsoEm { get; private set; }
    public DateTimeOffset ConfirmadaEm { get; private set; }

    private Sessao() { }

    public static Sessao Criar(Guid casalId, Guid usuarioId, byte[] tokenHmac, DateTimeOffset agora) =>
        new()
        {
            CasalId = casalId, UsuarioId = usuarioId, TokenHmac = tokenHmac,
            ExpiraEm = agora + TemposDoLogin.SessaoAbsoluta, UltimoUsoEm = agora, ConfirmadaEm = agora,
        };

    /// <summary>Vale enquanto não passou da expiração absoluta nem de 7 dias sem uso.</summary>
    public bool Valida(DateTimeOffset agora) =>
        agora < ExpiraEm && agora - UltimoUsoEm < TemposDoLogin.SessaoParada;

    /// <summary>Só regrava o último uso quando passou de 1 h, para não inundar a trilha de auditoria.</summary>
    public bool RegistrarUso(DateTimeOffset agora)
    {
        if (agora - UltimoUsoEm <= TemposDoLogin.RegravacaoDoUltimoUso) return false;
        UltimoUsoEm = agora;
        return true;
    }

    public void Confirmar(DateTimeOffset agora) => ConfirmadaEm = agora;
}
