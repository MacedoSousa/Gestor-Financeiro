using System.Text.RegularExpressions;

namespace GestorFinanceiro.ArchitectureTests;

/// <summary>
/// A Pluggy é só leitura (ADR-015, spec da Fase 2 §6): o código fala com ela por GET e só faz POST em /auth.
/// Rotas que movimentam dinheiro e os verbos PATCH, PUT e DELETE são proibidos. Varre o texto do código.
/// </summary>
internal static class VerificadorDaPluggy
{
    private static readonly string[] RotasProibidas = ["/payments", "/smart-transfers", "/boletos", "/boleto-connections", "/pix-biometric"];

    private static readonly (Regex Padrao, string Nome)[] Verbos =
    [
        (new Regex(@"\bDelete(?:AsJson)?Async\s*\("), "DeleteAsync"),
        (new Regex(@"\bPatch(?:AsJson)?Async\s*\("), "PatchAsync"),
        (new Regex(@"\bPut(?:AsJson)?Async\s*\("), "PutAsync"),
        (new Regex(@"\bHttpMethod\.Delete\b"), "HttpMethod.Delete"),
        (new Regex(@"\bHttpMethod\.Patch\b"), "HttpMethod.Patch"),
        (new Regex(@"\bHttpMethod\.Put\b"), "HttpMethod.Put"),
        (new Regex(@"\bPost(?:AsJson)?Async\s*\((?!\s*""/auth"")"), "PostAsync fora de /auth"),
        (new Regex(@"\bHttpMethod\.Post\b(?!\s*,\s*""/auth"")"), "HttpMethod.Post fora de /auth"),
    ];

    public static List<string> Violacoes(string codigo)
    {
        var achados = Verbos.Where(v => v.Padrao.IsMatch(codigo)).Select(v => v.Nome).ToList();
        // A rota conta como proibida quando aparece dentro de uma string, seguida de fim, '/', '?' ou aspas.
        achados.AddRange(RotasProibidas.Where(r => Regex.IsMatch(codigo, "\"[^\"\\n]*" + Regex.Escape(r) + "(?=[\"/?])")));
        return achados;
    }
}
