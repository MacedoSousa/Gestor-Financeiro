using GestorFinanceiro.SharedKernel.Dominio;
using GestorFinanceiro.SharedKernel.Seguranca;

namespace GestorFinanceiro.Modules.Identidade.Dominio;

/// <summary>Forma de entrar de um cônjuge: passkey (WebAuthn) ou TOTP. O segredo TOTP só existe cifrado.</summary>
internal sealed class Credencial : Entidade, ICasalScoped
{
    internal const string Tabela = "identidade.credenciais";

    public Guid CasalId { get; private set; }
    public Guid UsuarioId { get; private set; }
    public TipoCredencial Tipo { get; private set; }
    public string Apelido { get; private set; } = string.Empty;
    public byte[]? CredentialId { get; private set; }
    public byte[]? ChavePublica { get; private set; }
    public long? Contador { get; private set; }
    public byte[]? SegredoCifrado { get; private set; }
    public long? UltimoPasso { get; private set; }
    public bool Confirmada { get; private set; }
    public DateTimeOffset? UltimoUsoEm { get; private set; }

    private Credencial() { }

    public static Credencial CriarPasskey(Guid casalId, Guid usuarioId, string apelido, byte[] credentialId, byte[] chavePublica, long contador)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apelido);
        ArgumentNullException.ThrowIfNull(credentialId);
        ArgumentNullException.ThrowIfNull(chavePublica);
        return new Credencial
        {
            CasalId = casalId, UsuarioId = usuarioId, Tipo = TipoCredencial.Passkey, Apelido = apelido.Trim(),
            CredentialId = credentialId, ChavePublica = chavePublica, Contador = contador, Confirmada = true,
        };
    }

    /// <summary>TOTP nasce não confirmado: só vale depois do primeiro código certo.</summary>
    public static Credencial CriarTotp(Guid casalId, Guid usuarioId, string apelido, string segredoBase32, ICofre cofre)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apelido);
        var c = new Credencial { CasalId = casalId, UsuarioId = usuarioId, Tipo = TipoCredencial.Totp, Apelido = apelido.Trim() };
        c.SegredoCifrado = cofre.Cifrar(segredoBase32, Cofre.Aad(Tabela, "segredo_cifrado", c.Id));
        return c;
    }

    public string LerSegredo(ICofre cofre) =>
        cofre.Decifrar(SegredoCifrado ?? throw new InvalidOperationException("Credencial sem segredo."),
            Cofre.Aad(Tabela, "segredo_cifrado", Id));

    public void Confirmar() => Confirmada = true;

    public void RegistrarUsoPasskey(long contador, DateTimeOffset agora)
    {
        Contador = contador;
        UltimoUsoEm = agora;
    }

    public void RegistrarUsoTotp(long passo, DateTimeOffset agora)
    {
        UltimoPasso = passo;
        UltimoUsoEm = agora;
    }
}

internal enum TipoCredencial
{
    Passkey,
    Totp,
}
