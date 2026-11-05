using System.Text;
using GestorFinanceiro.Modules.Identidade.Aplicacao;

namespace GestorFinanceiro.UnitTests;

/// <summary>Vetores do apêndice B da RFC 6238 (SHA-1), com o segredo ASCII "12345678901234567890".</summary>
public class TotpTests
{
    private static readonly byte[] SegredoDaRfc = Encoding.ASCII.GetBytes("12345678901234567890");

    // A RFC dá 8 dígitos (94287082, 07081804); com 6 dígitos ficam os 6 últimos.
    [Theory]
    [InlineData(59L, "287082", 1L)]
    [InlineData(1111111109L, "081804", 37037036L)]
    public void Confere_o_vetor_da_rfc_6238(long unix, string codigo, long passo)
    {
        var agora = DateTimeOffset.FromUnixTimeSeconds(unix);

        Assert.True(Totp.Conferir(SegredoDaRfc, codigo, 0, agora, out var aceito));
        Assert.Equal(passo, aceito);
    }

    [Fact]
    public void Recusa_codigo_errado_e_passo_ja_usado()
    {
        var agora = DateTimeOffset.FromUnixTimeSeconds(59);

        Assert.False(Totp.Conferir(SegredoDaRfc, "287083", 0, agora, out _));
        Assert.False(Totp.Conferir(SegredoDaRfc, "287082", 1, agora, out _));
        Assert.False(Totp.Conferir(SegredoDaRfc, "28708", 0, agora, out _));
    }
}
