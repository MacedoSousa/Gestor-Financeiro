using System.Security.Cryptography;
using GestorFinanceiro.Modules.Identidade.Dominio;
using GestorFinanceiro.SharedKernel.Seguranca;

namespace GestorFinanceiro.Modules.Identidade.Aplicacao;

/// <summary>Confere senha ou código abrindo o envelope da DEK. Não guarda nem devolve chave nenhuma.</summary>
internal static class ConferenciaDeSegredo
{
    private static readonly byte[] SaltFalso = new byte[16];

    public static bool SenhaAbreEnvelope(ChaveDoCasal envelopeUsuario, string senha) => Abre(envelopeUsuario, senha);

    public static bool CodigoAbreEnvelope(ChaveDoCasal envelopeRecuperacao, string codigo) =>
        Abre(envelopeRecuperacao, EnvelopeDeChaves.CanonizarCodigo(codigo));

    /// <summary>Mesmo custo de uma conferência real, para quando o usuário não existe.</summary>
    public static void IgualarTempo(string segredo) =>
        CryptographicOperations.ZeroMemory(EnvelopeDeChaves.DerivarKek(segredo, SaltFalso, new ParametrosArgon2()));

    private static bool Abre(ChaveDoCasal envelope, string segredo)
    {
        byte[]? kek = null;
        byte[]? dek = null;
        try
        {
            kek = EnvelopeDeChaves.DerivarKek(segredo, envelope.Salt ?? SaltFalso, new ParametrosArgon2());
            dek = EnvelopeDeChaves.Desembrulhar(envelope.DekCifrada, kek);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            if (kek is not null) CryptographicOperations.ZeroMemory(kek);
            if (dek is not null) CryptographicOperations.ZeroMemory(dek);
        }
    }
}
