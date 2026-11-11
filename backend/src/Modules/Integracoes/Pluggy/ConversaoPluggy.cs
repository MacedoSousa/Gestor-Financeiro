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

    public static DateOnly DiaDeSaoPaulo(string instanteIso)
    {
        if (!DateTimeOffset.TryParse(instanteIso, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var instante))
            throw new FormatException("Data da Pluggy inválida.");

        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instante, Fuso).DateTime);
    }
}
