using GestorFinanceiro.Modules.Integracoes.Pluggy;

namespace GestorFinanceiro.Modules.Integracoes.Dominio;

/// <summary>O que a confirmação vem pré-preenchida: apelido (D4) e dias do cartão (D1).</summary>
internal static class SugestaoDeApelido
{
    private const int ApelidoMaximo = 60;

    private static readonly Dictionary<string, string> NomeDoTipo = new(StringComparer.Ordinal)
    {
        ["CORRENTE"] = "Corrente",
        ["POUPANCA"] = "Poupança",
        ["CARTAO"] = "Cartão",
    };

    /// <summary>"&lt;Banco&gt; &lt;Tipo&gt; · &lt;primeiro nome&gt;", ou "· Conjunta" sem titular; o banco é cortado para caber em 60.</summary>
    public static string Para(string banco, string tipoDaConta, string? primeiroNome)
    {
        ArgumentNullException.ThrowIfNull(banco);
        var fim = $" {NomeDoTipo.GetValueOrDefault(tipoDaConta, tipoDaConta)} · {primeiroNome ?? "Conjunta"}";
        var inicio = banco.Trim();
        return inicio[..Math.Min(inicio.Length, Math.Max(0, ApelidoMaximo - fim.Length))].TrimEnd() + fim;
    }

    /// <summary>
    /// A moda do dia de fechamento e do dia de vencimento das faturas do banco (D1); no empate, o dia da fatura mais recente.
    /// Sem faturas (ou sem fechamento), nulo: a pessoa preenche.
    /// </summary>
    public static (int? Fechamento, int? Vencimento) DiasDoCartao(IReadOnlyCollection<FaturaPluggy> faturas)
    {
        ArgumentNullException.ThrowIfNull(faturas);
        return (Moda(faturas.Where(f => f.Fechamento is not null).Select(f => (f.Fechamento!.Value.Day, f.Vencimento))),
            Moda(faturas.Select(f => (f.Vencimento.Day, f.Vencimento))));
    }

    private static int? Moda(IEnumerable<(int Dia, DateOnly Vencimento)> dias)
    {
        var grupos = dias.GroupBy(d => d.Dia).ToList();
        return grupos.Count == 0
            ? null
            : grupos.OrderByDescending(g => g.Count()).ThenByDescending(g => g.Max(d => d.Vencimento)).First().Key;
    }
}
