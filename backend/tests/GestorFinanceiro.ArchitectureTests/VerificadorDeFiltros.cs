using System.Text.RegularExpressions;

namespace GestorFinanceiro.ArchitectureTests;

/// <summary>
/// Regra 9 do CLAUDE.md: <c>IgnoreQueryFilters</c> sempre com o nome do filtro e só nos lugares permitidos.
/// Casal: fluxos do Identidade anteriores à sessão (D27). SoftDelete: checagem de chave natural da importação (D6).
/// A tela de lixeira, quando existir, entra aqui.
/// </summary>
internal static class VerificadorDeFiltros
{
    private static readonly Dictionary<string, string[]> Permitidos = new()
    {
        // Todos no Identidade, antes de haver sessão (o casal ainda não é conhecido).
        ["Casal"] =
        [
            "backend/src/Modules/Identidade/Aplicacao/ServicoDeSessoes.cs",               // sessões
            "backend/src/Modules/Identidade/Aplicacao/PedidosDeDispositivo.cs",           // celular sem sessão (pedido, consulta, resgate)
            "backend/src/Modules/Identidade/Aplicacao/RecuperacaoDeAcesso.cs",            // recuperação
            "backend/src/Modules/Identidade/Aplicacao/RotacaoDoCodigoDeRecuperacao.cs",   // recuperação (envelope do casal)
            "backend/src/Modules/Identidade/Aplicacao/PrimeiroAcesso.cs",                 // primeiro acesso
            "backend/src/Modules/Identidade/Aplicacao/TentativaDeLogin.cs",               // login por senha
            "backend/src/Modules/Identidade/Aplicacao/Passkeys.cs",                       // registro (token) e login por passkey
            "backend/src/Modules/Identidade/Aplicacao/ServicoDeTotp.cs",                  // registro (token) e login por TOTP
            "backend/src/Modules/Identidade/Infra/ConsultaDeUsuarios.cs",                 // bot: quem mandou a mensagem, pelo telefone
        ],
        ["SoftDelete"] = ["backend/src/Modules/Lancamentos/Aplicacao/Importacao.cs"],
    };

    private static readonly Regex Uso = new(@"\.IgnoreQueryFilters\s*\(\s*(?:\[(?<filtros>[^\]]*)\])?\s*\)");

    public static List<string> Violacoes(string arquivo, string codigo)
    {
        var achados = new List<string>();
        foreach (Match m in Uso.Matches(codigo))
        {
            var lista = m.Groups["filtros"].Value;
            if (string.IsNullOrWhiteSpace(lista))
            {
                achados.Add($"{arquivo}: IgnoreQueryFilters sem o nome do filtro");
                continue;
            }

            foreach (var item in lista.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var nome = item.Replace("Filtros.", "", StringComparison.Ordinal).Trim('"', ' ');
                if (!Permitidos.TryGetValue(nome, out var lugares) || !lugares.Contains(arquivo))
                    achados.Add($"{arquivo}: filtro {nome} desligado fora dos lugares permitidos");
            }
        }

        return achados;
    }
}
