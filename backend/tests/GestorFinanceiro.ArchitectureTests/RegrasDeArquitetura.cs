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
using GestorFinanceiro.Modules.Painel;
using NetArchTest.Rules;

namespace GestorFinanceiro.ArchitectureTests;

/// <summary>
/// Regras do CLAUDE.md que o build garante. Se um teste daqui falha, a mudança quebrou a arquitetura.
/// </summary>
public class RegrasDeArquitetura
{
    private static readonly string[] NomesDosModulos =
    [
        typeof(IdentidadeModule).Assembly.GetName().Name!,
        typeof(ContasModule).Assembly.GetName().Name!,
        typeof(LancamentosModule).Assembly.GetName().Name!,
        typeof(OrcamentoModule).Assembly.GetName().Name!,
        typeof(BoletosModule).Assembly.GetName().Name!,
        typeof(InvestimentosModule).Assembly.GetName().Name!,
        typeof(IntegracoesModule).Assembly.GetName().Name!,
        typeof(AssistenteModule).Assembly.GetName().Name!,
        typeof(AuditoriaModule).Assembly.GetName().Name!,
        typeof(PainelModule).Assembly.GetName().Name!,
    ];

    public static TheoryData<string> Modulos => new(NomesDosModulos);

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

    public static TheoryData<string, string> ParesDeModulos
    {
        get
        {
            var dados = new TheoryData<string, string>();
            foreach (var a in NomesDosModulos)
                foreach (var b in NomesDosModulos.Where(b => b != a))
                    dados.Add(a, b);
            return dados;
        }
    }

    [Theory]
    [MemberData(nameof(ParesDeModulos))]
    public void Modulo_so_depende_de_Contracts_de_outro_modulo(string moduloA, string moduloB)
    {
        // Tudo de B que não é Contracts: os namespaces internos e a classe <B>Module.
        var proibidos = Assembly.Load(moduloB).GetTypes()
            .Where(t => t.FullName is not null && !t.FullName.StartsWith(moduloB + ".Contracts", StringComparison.Ordinal))
            .Select(t => t.Namespace == moduloB ? t.FullName! : t.Namespace)
            .Where(n => n is not null && n != moduloB)
            .Select(n => n!)
            .Distinct()
            .ToArray();
        if (proibidos.Length == 0) return;

        var resultado = Types.InAssembly(Assembly.Load(moduloA))
            .ShouldNot().HaveDependencyOnAny(proibidos)
            .GetResult();

        var ofensores = string.Join(", ", resultado.FailingTypes?.Select(t => t.FullName) ?? []);
        Assert.True(resultado.IsSuccessful, $"{moduloA} depende de algo de {moduloB} fora de Contracts: {ofensores}");
    }

    [Fact]
    public void Painel_nao_referencia_EF_Npgsql_nem_DbContext()
    {
        var achados = AcessoADados(typeof(PainelModule).Assembly);

        Assert.True(achados.Count == 0, $"Painel acessa dados: {string.Join(", ", achados)}. O Painel só lê Contracts.");
    }

    [Fact]
    public void Verificador_de_acesso_a_dados_acusa_Lancamentos()
    {
        Assert.NotEmpty(AcessoADados(typeof(LancamentosModule).Assembly));
    }

    private static List<string> AcessoADados(Assembly a)
    {
        var referencias = a.GetReferencedAssemblies()
            .Select(r => r.Name!)
            .Where(n => n.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                     || n.StartsWith("Npgsql", StringComparison.Ordinal));

        var contextos = a.GetTypes().Where(t =>
        {
            for (var b = t.BaseType; b is not null; b = b.BaseType)
                if (b.FullName == "Microsoft.EntityFrameworkCore.DbContext") return true;
            return false;
        }).Select(t => t.FullName!);

        return referencias.Concat(contextos).ToList();
    }

    [Fact]
    public void Financas_nao_depende_de_nenhum_assembly_do_projeto()
    {
        var referenciados = typeof(GestorFinanceiro.Financas.FinanceEngine).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("GestorFinanceiro.", StringComparison.Ordinal))
            .ToList();

        Assert.True(referenciados.Count == 0, $"Financas referencia: {string.Join(", ", referenciados)}. Deve ser livre de dependências do projeto.");
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
    public void Toda_tabela_criada_tem_schema()
    {
        var semSchema = Arquivos("db/migrations", "*.sql")
            .SelectMany(f => TabelasSemSchema(File.ReadAllText(f)).Select(t => $"{Path.GetFileName(f)}: {t}"))
            .ToList();

        Assert.True(semSchema.Count == 0, $"CREATE TABLE sem schema (regras 2 e 7): {string.Join(", ", semSchema)}");
    }

    private static List<string> TabelasSemSchema(string sql) =>
        Regex.Matches(sql, @"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?([^\s(]+)", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value)
            .Where(t => !t.Contains('.', StringComparison.Ordinal))
            .ToList();

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

    // ---------------------------------------------------------------- Pluggy só de leitura (ADR-015)

    [Fact]
    public void Cliente_da_Pluggy_so_le()
    {
        var ofensores = Arquivos("backend/src/Modules/Integracoes", "*.cs")
            .SelectMany(f => VerificadorDaPluggy.Violacoes(File.ReadAllText(f)).Select(v => $"{Relativo(f)}: {v}"))
            .ToList();

        Assert.True(ofensores.Count == 0, $"A Pluggy é só leitura (POST só em /auth): {string.Join("; ", ofensores)}");
    }

    [Theory]
    [InlineData("await http.DeleteAsync(\"/items/1\")", "DeleteAsync")]
    [InlineData("await http.PatchAsync(\"/items/1\", corpo)", "PatchAsync")]
    [InlineData("await http.PutAsync(\"/items/1\", corpo)", "PutAsync")]
    [InlineData("new HttpRequestMessage(HttpMethod.Delete, \"/items/1\")", "HttpMethod.Delete")]
    [InlineData("new HttpRequestMessage(HttpMethod.Patch, \"/items/1\")", "HttpMethod.Patch")]
    [InlineData("new HttpRequestMessage(HttpMethod.Put, \"/items/1\")", "HttpMethod.Put")]
    [InlineData("await http.PostAsync(\"/payments\", corpo)", "PostAsync fora de /auth")]
    [InlineData("new HttpRequestMessage(HttpMethod.Post, \"/items\")", "HttpMethod.Post fora de /auth")]
    [InlineData("const string Rota = \"/smart-transfers\";", "/smart-transfers")]
    [InlineData("var r = \"/boletos?x=1\";", "/boletos")]
    [InlineData("var r = \"/boleto-connections\";", "/boleto-connections")]
    [InlineData("var r = \"/pix-biometric\";", "/pix-biometric")]
    [InlineData("var r = \"/payments/requests\";", "/payments")]
    public void Verificador_da_Pluggy_acusa(string codigo, string esperado)
    {
        Assert.Contains(esperado, VerificadorDaPluggy.Violacoes(codigo));
    }

    [Theory]
    [InlineData("await http.PostAsync(\"/auth\", corpo)")]
    [InlineData("new HttpRequestMessage(HttpMethod.Post, \"/auth\")")]
    [InlineData("await http.GetAsync($\"/v2/transactions?accountId={id}\")")]
    [InlineData("new HttpRequestMessage(HttpMethod.Get, \"/items/1\")")]
    public void Verificador_da_Pluggy_aceita_leitura(string codigo)
    {
        Assert.Empty(VerificadorDaPluggy.Violacoes(codigo));
    }

    // ---------------------------------------------------------------- filtros (regra 9, D6 e D27)

    [Fact]
    public void IgnoreQueryFilters_so_nos_lugares_permitidos()
    {
        var ofensores = Arquivos("backend/src", "*.cs")
            .SelectMany(f => VerificadorDeFiltros.Violacoes(Relativo(f), File.ReadAllText(f)))
            .ToList();

        Assert.True(ofensores.Count == 0, $"IgnoreQueryFilters fora dos lugares permitidos (regra 9): {string.Join("; ", ofensores)}");
    }

    [Theory]
    [InlineData("backend/src/Modules/Contas/Aplicacao/X.cs", "db.Contas.IgnoreQueryFilters([Filtros.Casal])")]
    [InlineData("backend/src/Modules/Contas/Aplicacao/X.cs", "db.Contas.IgnoreQueryFilters([Filtros.SoftDelete])")]
    [InlineData("backend/src/Modules/Identidade/Aplicacao/ServicoDeSessoes.cs", "db.Sessoes.IgnoreQueryFilters()")]
    [InlineData("backend/src/Modules/Identidade/Aplicacao/ServicoDeSessoes.cs", "db.Sessoes.IgnoreQueryFilters([Filtros.SoftDelete])")]
    [InlineData("backend/src/Modules/Lancamentos/Aplicacao/Importacao.cs", "db.Transacoes.IgnoreQueryFilters([Filtros.Casal])")]
    [InlineData("backend/src/Modules/Lancamentos/Aplicacao/Importacao.cs", "db.Transacoes.IgnoreQueryFilters([Filtros.SoftDelete, Filtros.Casal])")]
    [InlineData("backend/src/Modules/Lancamentos/Aplicacao/Outro.cs", "db.Transacoes.IgnoreQueryFilters([Filtros.SoftDelete])")]
    public void Verificador_de_filtros_acusa(string arquivo, string codigo)
    {
        Assert.NotEmpty(VerificadorDeFiltros.Violacoes(arquivo, codigo));
    }

    [Theory]
    [InlineData("backend/src/Modules/Identidade/Aplicacao/PrimeiroAcesso.cs", "db.Usuarios.IgnoreQueryFilters([Filtros.Casal])")]
    [InlineData("backend/src/Modules/Lancamentos/Aplicacao/Importacao.cs", "db.Transacoes.IgnoreQueryFilters([Filtros.SoftDelete])")]
    [InlineData("backend/src/Modules/Contas/Aplicacao/X.cs", "db.Contas.Where(c => c.Arquivada)")]
    public void Verificador_de_filtros_aceita_os_lugares_permitidos(string arquivo, string codigo)
    {
        Assert.Empty(VerificadorDeFiltros.Violacoes(arquivo, codigo));
    }

    private static string Relativo(string caminho) =>
        Path.GetRelativePath(RaizDoRepositorio(), caminho).Replace(Path.DirectorySeparatorChar, '/');

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
