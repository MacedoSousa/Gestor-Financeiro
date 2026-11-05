using System.Reflection;
using System.Text.RegularExpressions;
using GestorFinanceiro.Modules.Assistente;
using GestorFinanceiro.Modules.Auditoria;
using GestorFinanceiro.Modules.Boletos;
using GestorFinanceiro.Modules.Contas;
using GestorFinanceiro.Modules.Identidade;
using GestorFinanceiro.Modules.Integracoes;
using GestorFinanceiro.Modules.Investimentos;
using GestorFinanceiro.Modules.Lancamentos;
using GestorFinanceiro.Modules.Orcamento;
using NetArchTest.Rules;

namespace GestorFinanceiro.ArchitectureTests;

/// <summary>
/// Regras do CLAUDE.md que o build garante. Se um teste daqui falha, a mudança quebrou a arquitetura.
/// </summary>
public class RegrasDeArquitetura
{
    public static TheoryData<string> Modulos => new(
        typeof(IdentidadeModule).Assembly.GetName().Name!,
        typeof(ContasModule).Assembly.GetName().Name!,
        typeof(LancamentosModule).Assembly.GetName().Name!,
        typeof(OrcamentoModule).Assembly.GetName().Name!,
        typeof(BoletosModule).Assembly.GetName().Name!,
        typeof(InvestimentosModule).Assembly.GetName().Name!,
        typeof(IntegracoesModule).Assembly.GetName().Name!,
        typeof(AssistenteModule).Assembly.GetName().Name!,
        typeof(AuditoriaModule).Assembly.GetName().Name!);

    [Theory]
    [MemberData(nameof(Modulos))]
    public void Modulo_so_expoe_Contracts_e_a_classe_Module(string nomeAssembly)
    {
        var assembly = Assembly.Load(nomeAssembly);
        var contratos = $"{nomeAssembly}.Contracts";

        var vazando = assembly.GetExportedTypes()
            .Where(t => t.Namespace != contratos && !(t.Namespace?.StartsWith(contratos + ".", StringComparison.Ordinal) ?? false))
            .Where(t => !t.Name.EndsWith("Module", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .ToList();

        Assert.True(vazando.Count == 0, $"Tipos públicos fora de Contracts em {nomeAssembly}: {string.Join(", ", vazando)}. Torne-os internal.");
    }

    [Theory]
    [MemberData(nameof(Modulos))]
    public void Dominio_nao_depende_de_infra_nem_de_web(string nomeAssembly)
    {
        var resultado = Types.InAssembly(Assembly.Load(nomeAssembly))
            .That().ResideInNamespace($"{nomeAssembly}.Dominio")
            .ShouldNot().HaveDependencyOnAny("Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore", "Npgsql", $"{nomeAssembly}.Infra", $"{nomeAssembly}.Api")
            .GetResult();

        Assert.True(resultado.IsSuccessful, $"O domínio de {nomeAssembly} depende de infraestrutura.");
    }

    [Fact]
    public void Codigo_nao_tem_delete_fisico()
    {
        var proibido = new Regex(@"DELETE\s+FROM|ExecuteDelete|TRUNCATE\s+TABLE", RegexOptions.IgnoreCase);
        var ofensores = Arquivos("backend/src", "*.cs").Concat(Arquivos("db/migrations", "*.sql"))
            .Where(f => proibido.IsMatch(File.ReadAllText(f)))
            .ToList();

        Assert.True(ofensores.Count == 0, $"DELETE físico encontrado em: {string.Join(", ", ofensores)}");
    }

    [Fact]
    public void Toda_tabela_criada_e_protegida_pela_auditoria()
    {
        var criar = new Regex(@"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?([a-z_]+\.[a-z_]+)", RegexOptions.IgnoreCase);
        var sql = string.Join("\n", Arquivos("db/migrations", "*.sql").Select(File.ReadAllText));

        var semProtecao = criar.Matches(sql)
            .Select(m => m.Groups[1].Value)
            .Where(t => t != "audit.eventos")
            .Where(t => !sql.Contains($"audit.proteger('{t}')", StringComparison.Ordinal))
            .ToList();

        Assert.True(semProtecao.Count == 0, $"Tabelas sem SELECT audit.proteger(...): {string.Join(", ", semProtecao)}");
    }

    [Fact]
    public void Unicidade_e_sempre_indice_parcial_ignorando_excluidos()
    {
        var linhas = Arquivos("db/migrations", "*.sql")
            .SelectMany(f => File.ReadAllLines(f).Select((l, i) => (f, l, i)))
            .Where(x => Regex.IsMatch(x.l, @"\bUNIQUE\b", RegexOptions.IgnoreCase))
            .Where(x => !x.l.TrimStart().StartsWith("--", StringComparison.Ordinal))
            .Where(x => !x.l.Contains("WHERE NOT is_deleted", StringComparison.Ordinal))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}")
            .ToList();

        Assert.True(linhas.Count == 0, $"UNIQUE sem 'WHERE NOT is_deleted' em: {string.Join(", ", linhas)}");
    }

    private static IEnumerable<string> Arquivos(string relativo, string padrao)
    {
        var pasta = Path.Combine(RaizDoRepositorio(), relativo);
        return Directory.Exists(pasta)
            ? Directory.EnumerateFiles(pasta, padrao, SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                         && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            : [];
    }

    private static string RaizDoRepositorio()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório (CLAUDE.md) não encontrada.");
    }
}
