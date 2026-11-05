namespace GestorFinanceiro.UnitTests;

/// <summary>Gera CPFs sintéticos com dígitos verificadores válidos, só para teste. Sem dependências.</summary>
internal static class CpfSintetico
{
    public static string Gerar(int semente)
    {
        // Passo coprimo com 10^9: sementes diferentes dão bases diferentes.
        var baseNumerica = (long)((semente * 7_654_321L + 123_456_789L) % 1_000_000_000L);
        while (true)
        {
            var d = new int[11];
            var resto = baseNumerica;
            for (var i = 8; i >= 0; i--) { d[i] = (int)(resto % 10); resto /= 10; }
            d[9] = Digito(d, 9, 10);
            d[10] = Digito(d, 10, 11);
            if (d.Distinct().Count() > 1) return string.Concat(d);
            baseNumerica = (baseNumerica + 1) % 1_000_000_000L;
        }
    }

    private static int Digito(int[] d, int quantidade, int pesoInicial)
    {
        var soma = 0;
        for (var i = 0; i < quantidade; i++) soma += d[i] * (pesoInicial - i);
        var r = soma % 11;
        return r < 2 ? 0 : 11 - r;
    }
}
