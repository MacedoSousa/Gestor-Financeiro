using System.Security.Cryptography;

namespace GestorFinanceiro.SharedKernel.Seguranca;

/// <summary>Envelope da DEK para um segredo: a DEK embrulhada e o salt usado para derivar a KEK.</summary>
public sealed record EnvelopeDaDek(byte[] DekCifrada, byte[] Salt);

/// <summary>
/// Cria envelopes da DEK sem entregar a DEK a quem chama: o módulo só recebe o resultado já embrulhado.
/// </summary>
public interface IEnvelopesDaDek
{
    EnvelopeDaDek Embrulhar(string segredo);
}

public sealed class EnvelopesDaDek(Func<byte[]> lerDek, ParametrosArgon2 parametros) : IEnvelopesDaDek
{
    public EnvelopeDaDek Embrulhar(string segredo)
    {
        var dek = lerDek();
        byte[]? kek = null;
        try
        {
            var salt = EnvelopeDeChaves.GerarSalt();
            kek = EnvelopeDeChaves.DerivarKek(segredo, salt, parametros);
            return new EnvelopeDaDek(EnvelopeDeChaves.Embrulhar(dek, kek), salt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
            if (kek is not null) CryptographicOperations.ZeroMemory(kek);
        }
    }
}
