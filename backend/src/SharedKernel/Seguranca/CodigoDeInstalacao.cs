using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
/// [validade: Unix segundos, int64 big-endian][SHA-256 do código na forma canônica
/// (<see cref="EnvelopeDeChaves.CanonizarCodigo"/>): 32 bytes].
/// SHA-256 basta porque o código tem 160 bits aleatórios.
/// </summary>
public sealed partial class CodigoDeInstalacaoEmArquivo(
    string caminho,
    TimeProvider relogio,
    ILogger<CodigoDeInstalacaoEmArquivo>? logger = null) : ICodigoDeInstalacao
{
    private readonly ILogger log = logger ?? NullLogger<CodigoDeInstalacaoEmArquivo>.Instance;

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
                conteudo = File.ReadAllBytes(caminho);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                ArquivoAusente(log);
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                ArquivoSemPermissao(log);
                return false;
            }
            catch (IOException e)
            {
                ArquivoIlegivel(log, e.GetType().Name);
                return false;
            }
        }
        if (conteudo.Length != Tamanho)
        {
            ArquivoComTamanhoErrado(log, conteudo.Length);
            return false;
        }

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

    private static byte[] Hash(string codigo) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(EnvelopeDeChaves.CanonizarCodigo(codigo)));

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

    // Sem caminho, conteúdo ou código: só o bastante para separar problema de posse/instalação de código errado.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Código de instalação não conferido: arquivo não existe. Gere com --codigo-instalacao.")]
    private static partial void ArquivoAusente(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Código de instalação não conferido: sem permissão para ler o arquivo (confira o dono e o modo).")]
    private static partial void ArquivoSemPermissao(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Código de instalação não conferido: falha ao ler o arquivo ({Tipo}).")]
    private static partial void ArquivoIlegivel(ILogger logger, string tipo);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Código de instalação não conferido: arquivo com {Tamanho} bytes, esperado 40.")]
    private static partial void ArquivoComTamanhoErrado(ILogger logger, int tamanho);
}
