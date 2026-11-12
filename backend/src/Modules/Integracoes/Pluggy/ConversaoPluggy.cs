using System.Globalization;
using System.Text.Json;

namespace GestorFinanceiro.Modules.Integracoes.Pluggy;

/// <summary>
/// Conversões do formato da Pluggy para o do sistema (regra 6 do CLAUDE.md). O valor é lido como <c>decimal</c> direto
/// do JSON, nunca como <c>double</c>, e vira centavos exatos; mais de duas casas é formato inválido, não arredondamento.
/// A data de uma transação é o dia em São Paulo do instante recebido (a Pluggy manda ISO 8601 em UTC).
/// </summary>
internal static class ConversaoPluggy
{
    private static readonly TimeZoneInfo Fuso = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    public static long Centavos(JsonElement valor)
    {
        if (valor.ValueKind != JsonValueKind.Number || !valor.TryGetDecimal(out var reais))
            throw new FormatException("Valor da Pluggy não é um número.");

        var centavos = reais * 100m;
        if (centavos != decimal.Truncate(centavos))
            throw new FormatException("Valor da Pluggy com mais de duas casas decimais.");

        try
        {
            return decimal.ToInt64(centavos);
        }
        catch (OverflowException e)
        {
            throw new FormatException("Valor da Pluggy fora da faixa.", e);
        }
    }

    /// <summary>
    /// Valor da transação no sinal do sistema (negativo = saída ou gasto, positivo = entrada), pelo tipo da conta na Pluggy
    /// (medição de 11/11; CEO 11/11 21h): na corrente (BANK) o sinal da Pluggy já é esse; no cartão (CREDIT) a compra
    /// vem positiva e o pagamento ou estorno negativo, então inverte.
    /// </summary>
    public static long ValorNoSistema(string tipoDaConta, long valorDaPluggy) => tipoDaConta switch
    {
        "BANK" => valorDaPluggy,
        "CREDIT" => -valorDaPluggy,
        _ => throw new FormatException("Tipo de conta da Pluggy desconhecido."),
    };

    /// <summary>
    /// <c>bankData.transferNumber</c> só com os dígitos (agência e conta), para o índice cego da trava de conta repetida
    /// (CEO 11/11 21h). Nulo se não houver dígito.
    /// </summary>
    public static string? TransferNumberNormalizado(string? transferNumber)
    {
        var digitos = new string((transferNumber ?? "").Where(char.IsAsciiDigit).ToArray());
        return digitos.Length == 0 ? null : digitos;
    }

    public static DateOnly DiaDeSaoPaulo(string instanteIso)
    {
        if (!DateTimeOffset.TryParse(instanteIso, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var instante))
            throw new FormatException("Data da Pluggy inválida.");

        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instante, Fuso).DateTime);
    }

    /// <summary>
    /// Data que é só dia (o vencimento de uma fatura): os 10 primeiros caracteres, sem conversão de fuso. Meia-noite UTC
    /// convertida para São Paulo cairia no dia anterior.
    /// </summary>
    public static DateOnly DiaDoCalendario(string dataIso) =>
        dataIso.Length >= 10 && DateOnly.TryParseExact(dataIso[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var dia)
            ? dia
            : throw new FormatException("Data da Pluggy inválida.");
}
