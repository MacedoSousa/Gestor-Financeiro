using System.Security.Cryptography;
using System.Text;

namespace GestorFinanceiro.SharedKernel.Seguranca;

/// <summary>Cifra campos sensíveis e gera índices cegos (HMAC) para busca e unicidade.</summary>
public interface ICofre
{
    /// <param name="aad">Contexto amarrado ao valor: use <see cref="Cofre.Aad"/> (tabela|coluna|id).</param>
    byte[] Cifrar(string texto, string aad);

    string Decifrar(byte[] cifrado, string aad);

    /// <summary>HMAC-SHA256 com chave derivada da DEK. Mesmo valor + propósito = mesmo índice.</summary>
    byte[] IndiceCego(string valorNormalizado, string proposito);
}

public static class Cofre
{
    public static string Aad(string tabela, string coluna, Guid id) => $"{tabela}|{coluna}|{id:N}";
}

/// <summary>
/// AES-256-GCM com nonce aleatório por valor. Formato: [versão 1][nonce 12][tag 16][cifra].
/// Chaves de cifra e de HMAC são derivadas da DEK por HKDF, nunca a DEK direto.
/// </summary>
public sealed class CofreAesGcm : ICofre, IDisposable
{
    private const byte Versao = 1;
    private const int TamNonce = 12;
    private const int TamTag = 16;

    private readonly byte[] _chaveCifra;
    private readonly byte[] _chaveIndice;

    public CofreAesGcm(ReadOnlySpan<byte> dek)
    {
        if (dek.Length != 32) throw new ArgumentException("A DEK deve ter 32 bytes.", nameof(dek));
        _chaveCifra = HKDF.DeriveKey(HashAlgorithmName.SHA256, dek.ToArray(), 32, info: "gf/cifra/v1"u8.ToArray());
        _chaveIndice = HKDF.DeriveKey(HashAlgorithmName.SHA256, dek.ToArray(), 32, info: "gf/indice-cego/v1"u8.ToArray());
    }

    public byte[] Cifrar(string texto, string aad)
    {
        var claro = Encoding.UTF8.GetBytes(texto);
        var saida = new byte[1 + TamNonce + TamTag + claro.Length];
        saida[0] = Versao;
        var nonce = saida.AsSpan(1, TamNonce);
        var tag = saida.AsSpan(1 + TamNonce, TamTag);
        var cifra = saida.AsSpan(1 + TamNonce + TamTag);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_chaveCifra, TamTag);
        aes.Encrypt(nonce, claro, cifra, tag, Encoding.UTF8.GetBytes(aad));
        CryptographicOperations.ZeroMemory(claro);
        return saida;
    }

    public string Decifrar(byte[] cifrado, string aad)
    {
        if (cifrado.Length < 1 + TamNonce + TamTag || cifrado[0] != Versao)
            throw new CryptographicException("Formato de campo cifrado desconhecido.");

        var nonce = cifrado.AsSpan(1, TamNonce);
        var tag = cifrado.AsSpan(1 + TamNonce, TamTag);
        var cifra = cifrado.AsSpan(1 + TamNonce + TamTag);
        var claro = new byte[cifra.Length];

        using var aes = new AesGcm(_chaveCifra, TamTag);
        aes.Decrypt(nonce, cifra, tag, claro, Encoding.UTF8.GetBytes(aad));
        return Encoding.UTF8.GetString(claro);
    }

    public byte[] IndiceCego(string valorNormalizado, string proposito) =>
        HMACSHA256.HashData(_chaveIndice, Encoding.UTF8.GetBytes($"{proposito}|{valorNormalizado}"));

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_chaveCifra);
        CryptographicOperations.ZeroMemory(_chaveIndice);
    }
}
