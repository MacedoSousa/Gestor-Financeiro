using System.Security.Cryptography;
using System.Text;
using OtpNet;

namespace GestorFinanceiro.Modules.Identidade.Aplicacao;

/// <summary>TOTP padrão RFC 6238 (SHA-1, 6 dígitos, 30 s), tolerando um passo para cada lado e sem reuso.</summary>
internal static class Totp
{
    internal const int Digitos = 6;
    internal const int PeriodoEmSegundos = 30;
    internal const int TamanhoDoSegredo = 20;

    /// <summary>
    /// Aceita o código se bater com algum passo da janela (atual ±1) e o passo for maior que <paramref name="ultimoPasso"/>.
    /// Confere sempre os três passos, em tempo constante por passo, para o custo não depender do acerto.
    /// </summary>
    public static bool Conferir(byte[] segredo, string codigo, long ultimoPasso, DateTimeOffset agora, out long passoAceito)
    {
        passoAceito = 0;
        var informado = Encoding.ASCII.GetBytes(codigo is { Length: Digitos } && codigo.All(char.IsAsciiDigit) ? codigo : new string('x', Digitos));
        var totp = new OtpNet.Totp(segredo, PeriodoEmSegundos, OtpHashMode.Sha1, Digitos);
        var atual = agora.ToUnixTimeSeconds() / PeriodoEmSegundos;
        var aceito = false;
        for (var passo = atual - 1; passo <= atual + 1; passo++)
        {
            var esperado = Encoding.ASCII.GetBytes(totp.ComputeTotp(DateTimeOffset.FromUnixTimeSeconds(passo * PeriodoEmSegundos).UtcDateTime));
            if (!CryptographicOperations.FixedTimeEquals(esperado, informado) || passo <= ultimoPasso) continue;
            aceito = true;
            passoAceito = passo;
        }
        return aceito;
    }

    public static byte[] GerarSegredo() => RandomNumberGenerator.GetBytes(TamanhoDoSegredo);

    public static string ParaBase32(byte[] segredo) => Base32Encoding.ToString(segredo);

    public static byte[] DeBase32(string base32) => Base32Encoding.ToBytes(base32);

    public static string MontarUri(string apelidoDoUsuario, string segredoBase32) =>
        $"otpauth://totp/Gestor%20Financeiro:{Uri.EscapeDataString(apelidoDoUsuario)}?secret={segredoBase32}&issuer=Gestor%20Financeiro&digits={Digitos}&period={PeriodoEmSegundos}";
}
