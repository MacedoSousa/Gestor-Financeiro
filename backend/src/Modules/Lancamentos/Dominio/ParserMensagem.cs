using System.Globalization;
using System.Text.RegularExpressions;
using GestorFinanceiro.SharedKernel.Dominio;

namespace GestorFinanceiro.Modules.Lancamentos.Dominio;

/// <summary>Resultado de uma mensagem como "mercado 142,50".</summary>
internal sealed record GastoInformado(Dinheiro Valor, string Descricao);

/// <summary>
/// Entende valores em português: "142,50", "1.234,56", "R$ 23", "23.90".
/// O último número da mensagem é o valor; o resto vira a descrição.
/// </summary>
internal static partial class ParserMensagem
{
    [GeneratedRegex(@"(?:R\$\s*)?\d[\d.,]*", RegexOptions.CultureInvariant)]
    private static partial Regex Numero();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex EspacosRepetidos();

    public static GastoInformado? Interpretar(string? mensagem)
    {
        if (string.IsNullOrWhiteSpace(mensagem)) return null;

        var candidatos = Numero().Matches(mensagem);
        for (var i = candidatos.Count - 1; i >= 0; i--)
        {
            var m = candidatos[i];
            if (!TentarValor(m.Value, out var valor) || valor <= 0) continue;

            var descricao = EspacosRepetidos()
                .Replace(mensagem.Remove(m.Index, m.Length), " ")
                .Trim(' ', '-', ':', ',', '.');

            return new GastoInformado(Dinheiro.DeReais(valor), descricao.Length == 0 ? "Sem descrição" : descricao);
        }
        return null;
    }

    internal static bool TentarValor(string bruto, out decimal valor)
    {
        var s = bruto.Replace("R$", string.Empty, StringComparison.Ordinal).Trim().TrimEnd('.', ',');

        if (s.Contains(','))
        {
            // Padrão brasileiro: ponto é milhar, vírgula é decimal.
            var partes = s.Split(',');
            if (partes.Length != 2 || partes[1].Length is 0 or > 2) { valor = 0; return false; }
            s = partes[0].Replace(".", string.Empty, StringComparison.Ordinal) + "." + partes[1];
        }
        else
        {
            var pontos = s.Count(c => c == '.');
            var depois = pontos == 1 ? s.Length - s.IndexOf('.') - 1 : 0;
            // "23.90" é decimal; "1.234" e "1.234.567" são milhares.
            if (!(pontos == 1 && depois <= 2))
                s = s.Replace(".", string.Empty, StringComparison.Ordinal);
        }

        return decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out valor);
    }
}
