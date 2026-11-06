using System.Text.Json.Serialization;

namespace GestorFinanceiro.Modules.Identidade.Contracts;

// Tipos no formato JSON do WebAuthn (PublicKeyCredentialCreationOptionsJSON, PublicKeyCredentialRequestOptionsJSON,
// RegistrationResponseJSON, AuthenticationResponseJSON), que o @simplewebauthn/browser consome e produz.
// Binários vão em base64url. Ids de credencial e desafios nunca vão para log: ToString não mostra nada.

/// <summary>Resposta de POST /api/v1/auth/passkey/registro/opcoes (PublicKeyCredentialCreationOptionsJSON).</summary>
public sealed record OpcoesDeRegistroDePasskeyDto(
    [property: JsonPropertyName("rp")] RpDePasskeyDto Rp,
    [property: JsonPropertyName("user")] UsuarioDePasskeyDto User,
    [property: JsonPropertyName("challenge")] string Challenge,
    [property: JsonPropertyName("pubKeyCredParams")] IReadOnlyList<ParametroDeChaveDto> PubKeyCredParams,
    [property: JsonPropertyName("timeout")] long Timeout,
    [property: JsonPropertyName("excludeCredentials")] IReadOnlyList<DescritorDeCredencialDto> ExcludeCredentials,
    [property: JsonPropertyName("authenticatorSelection")] SelecaoDeAutenticadorDto AuthenticatorSelection,
    [property: JsonPropertyName("attestation")] string Attestation)
{
    public override string ToString() => nameof(OpcoesDeRegistroDePasskeyDto);
}

public sealed record RpDePasskeyDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name);

/// <summary>Id = os 16 bytes do id do usuário (base64url); name e displayName = o primeiro nome, nunca o CPF.</summary>
public sealed record UsuarioDePasskeyDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("displayName")] string DisplayName)
{
    public override string ToString() => nameof(UsuarioDePasskeyDto);
}

public sealed record ParametroDeChaveDto(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("alg")] int Alg);

public sealed record DescritorDeCredencialDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type)
{
    public override string ToString() => nameof(DescritorDeCredencialDto);
}

public sealed record SelecaoDeAutenticadorDto(
    [property: JsonPropertyName("residentKey")] string ResidentKey,
    [property: JsonPropertyName("requireResidentKey")] bool RequireResidentKey,
    [property: JsonPropertyName("userVerification")] string UserVerification);

/// <summary>Resposta de POST /api/v1/auth/passkey/opcoes (PublicKeyCredentialRequestOptionsJSON), sem allowCredentials.</summary>
public sealed record OpcoesDeLoginComPasskeyDto(
    [property: JsonPropertyName("challenge")] string Challenge,
    [property: JsonPropertyName("timeout")] long Timeout,
    [property: JsonPropertyName("rpId")] string RpId,
    [property: JsonPropertyName("userVerification")] string UserVerification)
{
    public override string ToString() => nameof(OpcoesDeLoginComPasskeyDto);
}

/// <summary>Corpo de POST /api/v1/auth/passkey/registro. Apelido é o nome do dispositivo (1 a 60 caracteres).</summary>
public sealed record NovaPasskeyDto(
    [property: JsonPropertyName("apelido")] string Apelido,
    [property: JsonPropertyName("resposta")] RespostaDeRegistroDePasskeyDto Resposta)
{
    public override string ToString() => nameof(NovaPasskeyDto);
}

/// <summary>RegistrationResponseJSON, como devolvido por <c>startRegistration</c>.</summary>
public sealed record RespostaDeRegistroDePasskeyDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("rawId")] string RawId,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("response")] AtestacaoDePasskeyDto Response)
{
    public override string ToString() => nameof(RespostaDeRegistroDePasskeyDto);
}

public sealed record AtestacaoDePasskeyDto(
    [property: JsonPropertyName("clientDataJSON")] string ClientDataJson,
    [property: JsonPropertyName("attestationObject")] string AttestationObject,
    [property: JsonPropertyName("transports")] IReadOnlyList<string>? Transports)
{
    public override string ToString() => nameof(AtestacaoDePasskeyDto);
}

/// <summary>Corpo de POST /api/v1/auth/passkey: AuthenticationResponseJSON, como devolvido por <c>startAuthentication</c>.</summary>
public sealed record RespostaDeLoginComPasskeyDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("rawId")] string RawId,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("response")] AssercaoDePasskeyDto Response)
{
    public override string ToString() => nameof(RespostaDeLoginComPasskeyDto);
}

public sealed record AssercaoDePasskeyDto(
    [property: JsonPropertyName("clientDataJSON")] string ClientDataJson,
    [property: JsonPropertyName("authenticatorData")] string AuthenticatorData,
    [property: JsonPropertyName("signature")] string Signature,
    [property: JsonPropertyName("userHandle")] string? UserHandle)
{
    public override string ToString() => nameof(AssercaoDePasskeyDto);
}
