using System.Buffers.Binary;
using System.Buffers.Text;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GestorFinanceiro.IntegrationTests.Infra;

/// <summary>
/// Autenticador WebAuthn de software (ES256, P-256) para os testes: lê as opções no JSON padrão do WebAuthn
/// e devolve as respostas no mesmo formato que o <c>@simplewebauthn/browser</c> envia.
/// Attestation <c>none</c>, flags UP|UV (e AT no registro).
/// </summary>
internal sealed class AutenticadorDeTeste : IDisposable
{
    private const byte FlagUp = 0x01;
    private const byte FlagUv = 0x04;
    private const byte FlagAt = 0x40;
    private const int CoseEs256 = -7;

    private readonly ECDsa _chave = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(32);

    /// <summary>User handle recebido no registro e devolvido nas asserções.</summary>
    public byte[]? UserHandle { get; set; }

    /// <summary>Contador atual. Com <see cref="ContadorFixoEmZero"/>, fica sempre em 0 (autenticador sem contador).</summary>
    public uint Contador { get; set; }

    public bool ContadorFixoEmZero { get; init; }

    public string RpId { get; init; } = "localhost";

    public string Origem { get; set; } = ApiDeTeste.OrigemWebAuthn;

    public string IdBase64Url => Base64Url.EncodeToString(CredentialId);

    /// <summary>Monta a resposta de <c>navigator.credentials.create</c> para as opções recebidas.</summary>
    public JsonObject Registrar(JsonElement opcoes)
    {
        UserHandle = Base64Url.DecodeFromChars(opcoes.GetProperty("user").GetProperty("id").GetString());
        var clientData = ClientData("webauthn.create", opcoes.GetProperty("challenge").GetString()!);

        var dadosDoAutenticador = new List<byte>();
        dadosDoAutenticador.AddRange(SHA256.HashData(Encoding.UTF8.GetBytes(RpId)));
        dadosDoAutenticador.Add(FlagUp | FlagUv | FlagAt);
        dadosDoAutenticador.AddRange(ContadorBigEndian(ContadorFixoEmZero ? 0 : Contador));
        dadosDoAutenticador.AddRange(new byte[16]); // AAGUID zerado
        var tamanho = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(tamanho, (ushort)CredentialId.Length);
        dadosDoAutenticador.AddRange(tamanho);
        dadosDoAutenticador.AddRange(CredentialId);
        dadosDoAutenticador.AddRange(ChavePublicaCose());

        var cbor = new CborWriter(CborConformanceMode.Ctap2Canonical);
        cbor.WriteStartMap(3);
        cbor.WriteTextString("fmt");
        cbor.WriteTextString("none");
        cbor.WriteTextString("attStmt");
        cbor.WriteStartMap(0);
        cbor.WriteEndMap();
        cbor.WriteTextString("authData");
        cbor.WriteByteString(dadosDoAutenticador.ToArray());
        cbor.WriteEndMap();

        return new JsonObject
        {
            ["id"] = IdBase64Url,
            ["rawId"] = IdBase64Url,
            ["type"] = "public-key",
            ["authenticatorAttachment"] = "platform",
            ["clientExtensionResults"] = new JsonObject(),
            ["response"] = new JsonObject
            {
                ["clientDataJSON"] = Base64Url.EncodeToString(clientData),
                ["attestationObject"] = Base64Url.EncodeToString(cbor.Encode()),
                ["transports"] = new JsonArray("internal"),
            },
        };
    }

    /// <summary>Monta a resposta de <c>navigator.credentials.get</c>. O contador avança antes de assinar (salvo se fixo em zero).</summary>
    public JsonObject Autenticar(JsonElement opcoes, bool assinaturaValida = true)
    {
        if (!ContadorFixoEmZero) Contador++;
        var clientData = ClientData("webauthn.get", opcoes.GetProperty("challenge").GetString()!);

        var dadosDoAutenticador = new List<byte>();
        dadosDoAutenticador.AddRange(SHA256.HashData(Encoding.UTF8.GetBytes(RpId)));
        dadosDoAutenticador.Add(FlagUp | FlagUv);
        dadosDoAutenticador.AddRange(ContadorBigEndian(ContadorFixoEmZero ? 0 : Contador));
        var authData = dadosDoAutenticador.ToArray();

        var assinado = authData.Concat(SHA256.HashData(clientData)).ToArray();
        if (!assinaturaValida) assinado[0] ^= 0xFF;
        var assinatura = _chave.SignData(assinado, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        return new JsonObject
        {
            ["id"] = IdBase64Url,
            ["rawId"] = IdBase64Url,
            ["type"] = "public-key",
            ["authenticatorAttachment"] = "platform",
            ["clientExtensionResults"] = new JsonObject(),
            ["response"] = new JsonObject
            {
                ["clientDataJSON"] = Base64Url.EncodeToString(clientData),
                ["authenticatorData"] = Base64Url.EncodeToString(authData),
                ["signature"] = Base64Url.EncodeToString(assinatura),
                ["userHandle"] = UserHandle is null ? null : Base64Url.EncodeToString(UserHandle),
            },
        };
    }

    private byte[] ClientData(string tipo, string desafio) =>
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["type"] = tipo,
            ["challenge"] = desafio,
            ["origin"] = Origem,
            ["crossOrigin"] = false,
        });

    private static byte[] ContadorBigEndian(uint contador)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, contador);
        return bytes;
    }

    private byte[] ChavePublicaCose()
    {
        var p = _chave.ExportParameters(includePrivateParameters: false);
        var cbor = new CborWriter(CborConformanceMode.Ctap2Canonical);
        cbor.WriteStartMap(5);
        cbor.WriteInt32(1); cbor.WriteInt32(2);          // kty: EC2
        cbor.WriteInt32(3); cbor.WriteInt32(CoseEs256);  // alg: ES256
        cbor.WriteInt32(-1); cbor.WriteInt32(1);         // crv: P-256
        cbor.WriteInt32(-2); cbor.WriteByteString(p.Q.X!);
        cbor.WriteInt32(-3); cbor.WriteByteString(p.Q.Y!);
        cbor.WriteEndMap();
        return cbor.Encode();
    }

    public void Dispose() => _chave.Dispose();
}
