using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace GestorFinanceiro.SharedKernel.Seguranca;

/// <summary>Custo do Argon2id. Padrão acima do mínimo da OWASP (19 MiB, t=2, p=1).</summary>
public sealed record ParametrosArgon2(int MemoriaKiB = 65_536, int Iteracoes = 3, int Paralelismo = 2);

/// <summary>
/// Envelope encryption: uma DEK aleatória cifra os dados; cada segredo (senha de cada cônjuge,
/// código de recuperação, arquivo de serviço) gera uma KEK que embrulha a mesma DEK.
/// Esquecer uma senha não perde dados enquanto existir outro envelope.
/// </summary>
public static class EnvelopeDeChaves
{
    private static readonly byte[] AadDek = "gf/dek/v1"u8.ToArray();

    public static byte[] GerarDek() => RandomNumberGenerator.GetBytes(32);

    public static byte[] GerarSalt() => RandomNumberGenerator.GetBytes(16);

    public static byte[] DerivarKek(string segredo, byte[] salt, ParametrosArgon2 parametros)
    {
        var bytes = Encoding.UTF8.GetBytes(segredo.Normalize(NormalizationForm.FormC));
        try
        {
            var argon = new Argon2id(bytes)
            {
                Salt = salt,
                MemorySize = parametros.MemoriaKiB,
                Iterations = parametros.Iteracoes,
                DegreeOfParallelism = parametros.Paralelismo,
            };
            return argon.GetBytes(32);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static byte[] Embrulhar(byte[] dek, byte[] kek)
    {
        var saida = new byte[12 + 16 + dek.Length];
        var nonce = saida.AsSpan(0, 12);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(kek, 16);
        aes.Encrypt(nonce, dek, saida.AsSpan(28), saida.AsSpan(12, 16), AadDek);
        return saida;
    }

    /// <exception cref="CryptographicException">Segredo errado ou envelope adulterado.</exception>
    public static byte[] Desembrulhar(byte[] envelope, byte[] kek)
    {
        if (envelope.Length != 12 + 16 + 32) throw new CryptographicException("Envelope de DEK inválido.");
        var dek = new byte[32];
        using var aes = new AesGcm(kek, 16);
        aes.Decrypt(envelope.AsSpan(0, 12), envelope.AsSpan(28), envelope.AsSpan(12, 16), dek, AadDek);
        return dek;
    }

    /// <summary>
    /// Forma canônica de um código (recuperação ou instalação): maiúsculas e só A–Z e 0–9.
    /// Hífens, espaços e qualquer outro caractere são descartados. É dessa forma que a KEK
    /// de recuperação é derivada e o código de instalação é conferido; quem recebe um código
    /// digitado deve canonizá-lo antes de usar.
    /// </summary>
    public static string CanonizarCodigo(string codigo)
    {
        ArgumentNullException.ThrowIfNull(codigo);
        var sb = new StringBuilder(codigo.Length);
        foreach (var c in codigo.ToUpperInvariant())
        {
            if (c is (>= 'A' and <= 'Z') or (>= '0' and <= '9')) sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Código de recuperação impresso no cadastro: 160 bits em base32, grupos de 4.</summary>
    public static string GerarCodigoRecuperacao()
    {
        const string alfabeto = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var bytes = RandomNumberGenerator.GetBytes(32);
        var sb = new StringBuilder();
        for (var i = 0; i < 32; i++)
        {
            if (i > 0 && i % 4 == 0) sb.Append('-');
            sb.Append(alfabeto[bytes[i] % alfabeto.Length]);
        }
        return sb.ToString();
    }
}
