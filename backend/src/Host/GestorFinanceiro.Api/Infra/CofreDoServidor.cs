using System.Security.Cryptography;
using GestorFinanceiro.SharedKernel.Seguranca;

namespace GestorFinanceiro.Api.Infra;

/// <summary>
/// Monta o <see cref="ICofre"/> do processo: a chave de serviço (arquivo 0400) desembrulha a DEK.
/// Na Fase 0 o envelope de serviço fica em arquivo; na Fase 1 os envelopes por cônjuge vão para identidade.chaves.
/// </summary>
internal static class CofreDoServidor
{
    public static ICofre Carregar(IConfiguration config, IHostEnvironment ambiente)
    {
        var dek = LerDek(config, ambiente);
        try { return new CofreAesGcm(dek); }
        finally { CryptographicOperations.ZeroMemory(dek); }
    }

    /// <summary>Desembrulha a DEK de serviço. Devolve uma cópia: quem chama deve zerá-la.</summary>
    internal static byte[] LerDek(IConfiguration config, IHostEnvironment ambiente)
    {
        var raiz = ambiente.ContentRootPath;
        var arquivoChave = CaminhoObrigatorio(config, "Seguranca:ArquivoChaveServico", raiz);
        var arquivoDek = CaminhoObrigatorio(config, "Seguranca:ArquivoDekServico", raiz);

        var kek = ChaveDeServico.Ler(arquivoChave);
        try
        {
            return EnvelopeDeChaves.Desembrulhar(File.ReadAllBytes(arquivoDek), kek);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>
    /// Primeira instalação: cria a DEK, grava o envelope de serviço e devolve o código de recuperação
    /// (que embrulha a mesma DEK) para ser impresso e guardado em papel.
    /// </summary>
    public static async Task InicializarAsync(IConfiguration config, string raiz, TextWriter saida)
    {
        var arquivoChave = CaminhoObrigatorio(config, "Seguranca:ArquivoChaveServico", raiz);
        var arquivoDek = CaminhoObrigatorio(config, "Seguranca:ArquivoDekServico", raiz);
        var arquivoRecuperacao = CaminhoObrigatorio(config, "Seguranca:ArquivoDekRecuperacao", raiz);

        if (File.Exists(arquivoDek))
            throw new InvalidOperationException("Já existe uma DEK. Inicializar de novo tornaria os dados ilegíveis.");

        var kek = ChaveDeServico.Ler(arquivoChave);
        var dek = EnvelopeDeChaves.GerarDek();
        try
        {
            await File.WriteAllBytesAsync(arquivoDek, EnvelopeDeChaves.Embrulhar(dek, kek));

            var codigo = EnvelopeDeChaves.GerarCodigoRecuperacao();
            var salt = EnvelopeDeChaves.GerarSalt();
            var kekRecuperacao = EnvelopeDeChaves.DerivarKek(codigo, salt, new ParametrosArgon2());
            await File.WriteAllBytesAsync(arquivoRecuperacao, [.. salt, .. EnvelopeDeChaves.Embrulhar(dek, kekRecuperacao)]);
            CryptographicOperations.ZeroMemory(kekRecuperacao);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(arquivoDek, UnixFileMode.UserRead);
                File.SetUnixFileMode(arquivoRecuperacao, UnixFileMode.UserRead);
            }

            await saida.WriteLineAsync("Chaves criadas. Imprima o código abaixo e guarde em papel — ele não será mostrado de novo:");
            await saida.WriteLineAsync();
            await saida.WriteLineAsync($"    {codigo}");
            await saida.WriteLineAsync();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    /// <summary>Caminhos relativos são resolvidos a partir da raiz do projeto, não do diretório atual.</summary>
    internal static string CaminhoObrigatorio(IConfiguration config, string chave, string raiz) =>
        config[chave] is { Length: > 0 } valor
            ? Path.GetFullPath(valor, raiz)
            : throw new InvalidOperationException($"Configuração obrigatória ausente: {chave}");
}
