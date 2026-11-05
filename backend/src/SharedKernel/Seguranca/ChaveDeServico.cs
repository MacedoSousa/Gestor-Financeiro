using System.Security.Cryptography;

namespace GestorFinanceiro.SharedKernel.Seguranca;

/// <summary>
/// Arquivo de 32 bytes (permissão 0400, disco com LUKS) que embrulha a DEK para os jobs
/// noturnos rodarem sem ninguém logado. Ver ADR-005.
/// </summary>
public static class ChaveDeServico
{
    public static byte[] Ler(string caminho)
    {
        if (!File.Exists(caminho))
            throw new FileNotFoundException("Arquivo da chave de serviço não encontrado. Rode infra/scripts/gerar-segredos.sh.", caminho);

        if (!OperatingSystem.IsWindows())
        {
            var modo = File.GetUnixFileMode(caminho);
            if ((modo & (UnixFileMode.GroupRead | UnixFileMode.OtherRead | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                throw new UnauthorizedAccessException($"A chave de serviço não pode ser legível por grupo/outros (atual: {modo}). Use chmod 400.");
        }

        var bytes = File.ReadAllBytes(caminho);
        if (bytes.Length != 32)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new CryptographicException("A chave de serviço deve ter exatamente 32 bytes.");
        }
        return bytes;
    }
}
