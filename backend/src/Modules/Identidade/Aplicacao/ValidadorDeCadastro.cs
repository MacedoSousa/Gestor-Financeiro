using System.Text;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.Modules.Identidade.Dominio;

namespace GestorFinanceiro.Modules.Identidade.Aplicacao;

/// <summary>Valida o cadastro do casal. As mensagens são fixas: nunca incluem valores do pedido.</summary>
internal static class ValidadorDeCadastro
{
    private const string MensagemNomeFamilia = "Informe o nome da família (até 100 caracteres).";
    private const string MensagemNome = "Informe o nome completo (até 150 caracteres).";
    private const string MensagemCpf = "CPF inválido.";
    private const string MensagemCpfRepetido = "Os dois cônjuges não podem ter o mesmo CPF.";
    private const string MensagemSenha = "A senha precisa ter de 12 a 128 caracteres.";

    public static IReadOnlyDictionary<string, string[]> Validar(CadastroDoCasalDto pedido)
    {
        var erros = new Dictionary<string, string[]>();

        if (!TamanhoOk(pedido.NomeFamilia, 100)) erros["nomeFamilia"] = [MensagemNomeFamilia];

        var cpf1 = ValidarConjuge("conjuge1", pedido.Conjuge1, erros);
        var cpf2 = ValidarConjuge("conjuge2", pedido.Conjuge2, erros);

        if (cpf1 is { } a && cpf2 is { } b && a.Digitos == b.Digitos)
            erros["conjuge2.cpf"] = [MensagemCpfRepetido];

        return erros;
    }

    private static Cpf? ValidarConjuge(string prefixo, ConjugeNovoDto? conjuge, Dictionary<string, string[]> erros)
    {
        if (conjuge is null)
        {
            erros[$"{prefixo}.nomeCompleto"] = [MensagemNome];
            erros[$"{prefixo}.cpf"] = [MensagemCpf];
            erros[$"{prefixo}.senha"] = [MensagemSenha];
            return null;
        }

        if (!TamanhoOk(conjuge.NomeCompleto, 150)) erros[$"{prefixo}.nomeCompleto"] = [MensagemNome];
        if (!SenhaOk(conjuge.Senha)) erros[$"{prefixo}.senha"] = [MensagemSenha];

        if (Cpf.TentarCriar(conjuge.Cpf, out var cpf)) return cpf;
        erros[$"{prefixo}.cpf"] = [MensagemCpf];
        return null;
    }

    private static bool TamanhoOk(string? texto, int maximo)
    {
        var t = texto?.Trim();
        return !string.IsNullOrEmpty(t) && t.Length <= maximo;
    }

    private static bool SenhaOk(string? senha)
    {
        if (senha is null) return false;
        string normalizada;
        try { normalizada = senha.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { return false; } // Unicode inválido (ex.: surrogate solto)
        return normalizada.Length is >= 12 and <= 128;
    }
}
