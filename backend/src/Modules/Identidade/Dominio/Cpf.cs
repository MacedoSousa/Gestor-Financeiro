namespace GestorFinanceiro.Modules.Identidade.Dominio;

/// <summary>CPF validado e normalizado (11 dígitos). Nunca é logado nem serializado em claro.</summary>
internal readonly struct Cpf
{
    public string Digitos { get; }

    private Cpf(string digitos) => Digitos = digitos;

    public static bool TentarCriar(string? entrada, out Cpf cpf)
    {
        cpf = default;
        if (string.IsNullOrWhiteSpace(entrada)) return false;

        Span<int> d = stackalloc int[11];
        var n = 0;
        foreach (var c in entrada)
        {
            if (char.IsAsciiDigit(c))
            {
                if (n == 11) return false;
                d[n++] = c - '0';
            }
            else if (c is not ('.' or '-' or ' ')) return false;
        }
        if (n != 11) return false;

        var todosIguais = true;
        for (var i = 1; i < 11; i++) todosIguais &= d[i] == d[0];
        if (todosIguais) return false;

        if (DigitoVerificador(d[..9], 10) != d[9]) return false;
        if (DigitoVerificador(d[..10], 11) != d[10]) return false;

        cpf = new Cpf(string.Concat(d.ToArray()));
        return true;
    }

    private static int DigitoVerificador(ReadOnlySpan<int> digitos, int pesoInicial)
    {
        var soma = 0;
        for (var i = 0; i < digitos.Length; i++) soma += digitos[i] * (pesoInicial - i);
        var resto = soma % 11;
        return resto < 2 ? 0 : 11 - resto;
    }

    /// <summary>Exibição mascarada: ***.456.789-**.</summary>
    public string Mascarado() => $"***.{Digitos[3..6]}.{Digitos[6..9]}-**";

    public override string ToString() => Mascarado();
}
