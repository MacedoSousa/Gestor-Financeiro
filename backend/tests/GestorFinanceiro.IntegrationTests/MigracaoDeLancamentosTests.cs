using GestorFinanceiro.IntegrationTests.Infra;
using Npgsql;

namespace GestorFinanceiro.IntegrationTests;

[Collection("postgres")]
public class MigracaoDeLancamentosTests(PostgresFixture pg)
{
    private static async Task ExecutarAsync(NpgsqlConnection db, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, db);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> EscalarAsync(string conexao, string sql)
    {
        await using var db = new NpgsqlConnection(conexao);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, db);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<(NpgsqlConnection Db, Guid Casal)> PrepararAsync(BancoDeTeste banco)
    {
        var casal = Guid.NewGuid();
        var db = new NpgsqlConnection(banco.ConexaoApp);
        await db.OpenAsync();
        await ExecutarAsync(db, $"INSERT INTO identidade.casais (id, nome_familia) VALUES ('{casal}', 'Família Teste')");
        return (db, casal);
    }

    private static string Transacao(Guid id, Guid casal, string natureza, long valor, string externo = "k1", string origem = "MANUAL", string? categoria = null, string parcelas = "NULL, NULL") =>
        $"INSERT INTO lancamentos.transacoes (id, casal_id, conta_id, categoria_id, data, valor_centavos, natureza, descricao_cifrada, origem, id_externo, parcela, parcelas) " +
        $"VALUES ('{id}', '{casal}', '{Guid.NewGuid()}', {(categoria is null ? "NULL" : $"'{categoria}'")}, '2026-11-01', {valor}, '{natureza}', '\\x01', '{origem}', '{externo}', {parcelas})";

    private static string SqlDoUpdateDaV0006()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var arquivo = Path.Combine(dir.FullName, "db", "migrations", "V0006__categorias_papel.sql");
            if (File.Exists(arquivo))
                return string.Join("\n", File.ReadAllText(arquivo).Split(';')
                    .Select(c => c.Trim()).Where(c => c.StartsWith("UPDATE lancamentos.categorias", StringComparison.Ordinal)).Select(c => c + ";"));
        }
        throw new FileNotFoundException("V0006__categorias_papel.sql não encontrada");
    }

    // O MigradorSql aplica a pasta inteira (sem "até a versão"): o banco-modelo já tem a V0006, então as
    // categorias entram sem papel e o texto do UPDATE da V0006 roda de novo (ele precisa ser idempotente).
    [Fact]
    public async Task Migracao_preenche_papel_pelo_nome()
    {
        var banco = await pg.NovoBancoAsync();
        var (db, casal) = await PrepararAsync(banco);
        await using var _ = db;
        foreach (var (nome, tipo) in new[] { ("Investimento", "DESPESA"), ("Dívidas e financiamentos", "DESPESA"), ("Mercado", "DESPESA"), ("Salário", "RECEITA") })
            await ExecutarAsync(db, $"INSERT INTO lancamentos.categorias (id, casal_id, nome, tipo) VALUES ('{Guid.NewGuid()}', '{casal}', '{nome}', '{tipo}')");

        var update = SqlDoUpdateDaV0006();
        Assert.NotEmpty(update);
        await ExecutarAsync(db, update);
        await ExecutarAsync(db, update);

        async Task<string> PapelAsync(string nome)
        {
            await using var cmd = new NpgsqlCommand($"SELECT coalesce(papel, 'NULO') FROM lancamentos.categorias WHERE casal_id = '{casal}' AND nome = '{nome}'", db);
            return (string)(await cmd.ExecuteScalarAsync())!;
        }
        Assert.Equal("INVESTIMENTO", await PapelAsync("Investimento"));
        Assert.Equal("DIVIDAS", await PapelAsync("Dívidas e financiamentos"));
        Assert.Equal("NULO", await PapelAsync("Mercado"));
        Assert.Equal("NULO", await PapelAsync("Salário"));
    }

    [Fact]
    public async Task Papel_unico_por_casal()
    {
        var banco = await pg.NovoBancoAsync();
        var (db, casal) = await PrepararAsync(banco);
        await using var _ = db;
        var a = Guid.NewGuid();
        string Inserir(Guid id, string nome) =>
            $"INSERT INTO lancamentos.categorias (id, casal_id, nome, tipo, papel) VALUES ('{id}', '{casal}', '{nome}', 'DESPESA', 'INVESTIMENTO')";
        await ExecutarAsync(db, Inserir(a, "Investimento"));

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecutarAsync(db, Inserir(Guid.NewGuid(), "Aportes")));
        Assert.Equal("23505", ex.SqlState);
        Assert.Equal("ux_categorias_papel", ex.ConstraintName);

        await ExecutarAsync(db, $"UPDATE lancamentos.categorias SET is_deleted = true, deleted_at = now() WHERE id = '{a}'");
        await ExecutarAsync(db, Inserir(Guid.NewGuid(), "Aportes"));
    }

    [Fact]
    public async Task Origem_e_id_externo_so_sao_unicos_entre_ativos()
    {
        var banco = await pg.NovoBancoAsync();
        var (db, casal) = await PrepararAsync(banco);
        await using var _ = db;
        var a = Guid.NewGuid();
        await ExecutarAsync(db, Transacao(a, casal, "DESPESA", -100));
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecutarAsync(db, Transacao(Guid.NewGuid(), casal, "DESPESA", -100)));
        Assert.Equal("23505", ex.SqlState);
        await ExecutarAsync(db, $"UPDATE lancamentos.transacoes SET is_deleted = true, deleted_at = now() WHERE id = '{a}'");
        await ExecutarAsync(db, Transacao(Guid.NewGuid(), casal, "DESPESA", -100));
    }

    [Theory]
    [InlineData("DESPESA", 100)]
    [InlineData("RECEITA", -100)]
    [InlineData("DESPESA", 0)]
    public async Task Sinal_incoerente_com_a_natureza_da_23514(string natureza, long valor)
    {
        var banco = await pg.NovoBancoAsync();
        var (db, casal) = await PrepararAsync(banco);
        await using var _ = db;
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecutarAsync(db, Transacao(Guid.NewGuid(), casal, natureza, valor)));
        Assert.Equal("23514", ex.SqlState);
    }

    [Fact]
    public async Task Transferencia_com_categoria_da_23514()
    {
        var banco = await pg.NovoBancoAsync();
        var (db, casal) = await PrepararAsync(banco);
        await using var _ = db;
        var categoria = Guid.NewGuid();
        await ExecutarAsync(db, $"INSERT INTO lancamentos.categorias (id, casal_id, nome, tipo) VALUES ('{categoria}', '{casal}', 'Mercado', 'DESPESA')");
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecutarAsync(db, Transacao(Guid.NewGuid(), casal, "TRANSFERENCIA", -100, categoria: categoria.ToString())));
        Assert.Equal("23514", ex.SqlState);
        await ExecutarAsync(db, Transacao(Guid.NewGuid(), casal, "TRANSFERENCIA", -100, externo: "k2"));
    }

    [Theory]
    [InlineData("1, NULL")]
    [InlineData("0, 3")]
    [InlineData("4, 3")]
    [InlineData("1, 25")]
    public async Task Parcelas_incoerentes_dao_23514(string parcelas)
    {
        var banco = await pg.NovoBancoAsync();
        var (db, casal) = await PrepararAsync(banco);
        await using var _ = db;
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecutarAsync(db, Transacao(Guid.NewGuid(), casal, "DESPESA", -100, parcelas: parcelas)));
        Assert.Equal("23514", ex.SqlState);
        await ExecutarAsync(db, Transacao(Guid.NewGuid(), casal, "DESPESA", -100, externo: "ok", parcelas: "2, 24"));
    }

    [Theory]
    [InlineData("lancamentos", "categorias")]
    [InlineData("lancamentos", "transacoes")]
    public async Task Tabelas_tem_os_triggers_de_protecao(string schema, string tabela)
    {
        var banco = await pg.NovoBancoAsync();
        Assert.Equal(3, await EscalarAsync(banco.ConexaoSuperusuario,
            $"SELECT count(*) FROM pg_trigger WHERE tgrelid = '{schema}.{tabela}'::regclass AND tgname IN ('trg_auditoria','trg_sem_delete','trg_sem_truncate')"));
    }

    [Fact]
    public async Task Nao_existe_descricao_nem_contraparte_em_claro()
    {
        var banco = await pg.NovoBancoAsync();
        Assert.Equal(0, await EscalarAsync(banco.ConexaoSuperusuario,
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'lancamentos' AND table_name = 'transacoes' AND (column_name LIKE 'descricao%' OR column_name LIKE 'contraparte%') AND column_name NOT LIKE '%\\_cifrada'"));
        Assert.Equal(2, await EscalarAsync(banco.ConexaoSuperusuario,
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'lancamentos' AND table_name = 'transacoes' AND column_name IN ('descricao_cifrada','contraparte_cifrada')"));
    }
}
