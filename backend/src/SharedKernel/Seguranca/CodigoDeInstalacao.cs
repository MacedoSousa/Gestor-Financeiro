using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace GestorFinanceiro.SharedKernel.Seguranca;

/// <summary>Código de uso único que autoriza o cadastro do casal (a rota não tem sessão).</summary>
public interface ICodigoDeInstalacao
{
    bool Confere(string? codigo);

    /// <summary>Invalida o código. Chamar só depois do commit do cadastro.</summary>
    void Consumir();
}

/// <summary>
/// Guarda só o SHA-256 do código e a validade. Formato: 40 bytes,
/// [validade: Unix segundos, int64 big-endian][SHA-256 do código normalizado: 32 bytes].
/// SHA-256 basta porque o código tem 160 bits aleatórios.
/// </summary>
public sealed class CodigoDeInstalacaoEmArquivo(string caminho, TimeProvider relogio) : ICodigoDeInstalacao
{
    public static readonly TimeSpan Validade = TimeSpan.FromHours(24);

    private const int Tamanho = 8 + 32;
    private readonly object trava = new();

    public static string Gerar(string caminho, TimeProvider relogio)
    {
        var codigo = EnvelopeDeChaves.GerarCodigoRecuperacao();
        Gravar(caminho, (relogio.GetUtcNow() + Validade).ToUnixTimeSeconds(), Hash(codigo));
        return codigo;
    }

    public bool Confere(string? codigo)
    {
        if (string.IsNullOrWhiteSpace(codigo)) return false;

        byte[] conteudo;
        lock (trava)
        {
            try
            {
                if (!File.Exists(caminho)) return false;
                conteudo = File.ReadAllBytes(caminho);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        if (conteudo.Length != Tamanho) return false;

        var validade = BinaryPrimitives.ReadInt64BigEndian(conteudo);
        var hashOk = CryptographicOperations.FixedTimeEquals(conteudo.AsSpan(8), Hash(codigo));
        return hashOk && relogio.GetUtcNow().ToUnixTimeSeconds() < validade;
    }

    public void Consumir()
    {
        lock (trava)
        {
            var conteudo = File.ReadAllBytes(caminho);
            if (conteudo.Length != Tamanho)
                throw new InvalidOperationException("Arquivo do código de instalação inválido.");
            Gravar(caminho, 0, conteudo.AsSpan(8).ToArray());
        }
    }

    private static byte[] Hash(string codigo)
    {
        var normalizado = new string([.. codigo.Where(c => c != '-' && !char.IsWhiteSpace(c))]).ToUpperInvariant();
        return SHA256.HashData(Encoding.UTF8.GetBytes(normalizado));
    }

    private static void Gravar(string caminho, long validadeUnix, byte[] hash)
    {
        var conteudo = new byte[Tamanho];
        BinaryPrimitives.WriteInt64BigEndian(conteudo, validadeUnix);
        hash.CopyTo(conteudo, 8);

        if (!OperatingSystem.IsWindows() && File.Exists(caminho))
            File.SetUnixFileMode(caminho, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllBytes(caminho, conteudo);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(caminho, UnixFileMode.UserRead);
    }
}
