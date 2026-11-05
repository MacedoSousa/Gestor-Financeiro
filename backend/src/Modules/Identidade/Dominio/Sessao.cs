using GestorFinanceiro.SharedKernel.Dominio;

namespace GestorFinanceiro.Modules.Identidade.Dominio;

/// <summary>Sessão de login. Guarda só o HMAC do token: nem token, nem IP, nem user agent.</summary>
internal sealed class Sessao : Entidade, ICasalScoped
{
    public static readonly TimeSpan Parada = TimeSpan.FromDays(7);
    public static readonly TimeSpan Absoluta = TimeSpan.FromDays(30);
    public static readonly TimeSpan ConfirmacaoRecente = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RegravarUltimoUso = TimeSpan.FromHours(1);

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
            ExpiraEm = agora + Absoluta, UltimoUsoEm = agora, ConfirmadaEm = agora,
        };
}
