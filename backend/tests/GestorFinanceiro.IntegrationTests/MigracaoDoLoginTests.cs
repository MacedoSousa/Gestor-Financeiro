using GestorFinanceiro.IntegrationTests.Infra;
using Npgsql;

namespace GestorFinanceiro.IntegrationTests;

[Collection("postgres")]
public class MigracaoDoLoginTests(PostgresFixture pg)
{
    [Theory]
    [InlineData("sessoes")]
    [InlineData("credenciais")]
    [InlineData("pedidos_dispositivo")]
    public async Task Tabela_nova_tem_os_triggers_de_protecao(string tabela)
    {
        var banco = await pg.NovoBancoAsync();
        var n = await EscalarAsync(banco.ConexaoSuperusuario,
            $"SELECT count(*) FROM pg_trigger WHERE tgrelid = 'identidade.{tabela}'::regclass AND tgname IN ('trg_auditoria','trg_sem_delete','trg_sem_truncate')");
        Assert.Equal(3, n);
    }

    [Fact]
    public async Task App_insere_e_atualiza_sessoes_mas_nao_apaga()
    {
        var banco = await pg.NovoBancoAsync();
        var (casal, usuario) = await CriarUsuarioAsync(banco);
        await using var db = new NpgsqlConnection(banco.ConexaoApp);
        await db.OpenAsync();
        var id = Guid.NewGuid();
        await ExecutarAsync(db, $"INSERT INTO identidade.sessoes (id, casal_id, usuario_id, token_hmac, expira_em, ultimo_uso_em, confirmada_em) VALUES ('{id}', '{casal}', '{usuario}', '\\x01', now(), now(), now())");
        await ExecutarAsync(db, $"UPDATE identidade.sessoes SET ultimo_uso_em = now() + interval '1 hour' WHERE id = '{id}'");
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecutarAsync(db, $"DELETE FROM identidade.sessoes WHERE id = '{id}'"));
        Assert.Equal("42501", ex.SqlState);
    }

    [Fact]
    public async Task Credential_id_e_unico_so_entre_ativas()
    {
        var banco = await pg.NovoBancoAsync();
        var (casal, usuario) = await CriarUsuarioAsync(banco);
        await using var db = new NpgsqlConnection(banco.ConexaoApp);
        await db.OpenAsync();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        string Passkey(Guid id) => $"INSERT INTO identidade.credenciais (id, casal_id, usuario_id, tipo, apelido, credential_id, chave_publica, contador) VALUES ('{id}', '{casal}', '{usuario}', 'PASSKEY', 'Celular', '\\xAA', '\\xBB', 0)";
        await ExecutarAsync(db, Passkey(a));
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecutarAsync(db, Passkey(b)));
        Assert.Equal("23505", ex.SqlState);
        await ExecutarAsync(db, $"UPDATE identidade.credenciais SET is_deleted = true, deleted_at = now() WHERE id = '{a}'");
        await ExecutarAsync(db, Passkey(b));
    }

    [Fact]
    public async Task Nenhum_segredo_ou_token_em_claro()
    {
        var banco = await pg.NovoBancoAsync();
        Assert.Equal(0, await EscalarAsync(banco.ConexaoSuperusuario,
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'identidade' AND table_name = 'sessoes' AND column_name LIKE '%token%' AND column_name NOT LIKE '%\\_hmac'"));
        Assert.Equal(0, await EscalarAsync(banco.ConexaoSuperusuario,
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'identidade' AND table_name = 'credenciais' AND column_name LIKE '%segredo%' AND column_name NOT LIKE '%\\_cifrado'"));
    }

    private static async Task<(Guid Casal, Guid Usuario)> CriarUsuarioAsync(BancoDeTeste banco)
    {
        var casal = Guid.NewGuid();
        var usuario = Guid.NewGuid();
        await using var db = new NpgsqlConnection(banco.ConexaoApp);
        await db.OpenAsync();
        await ExecutarAsync(db, $"INSERT INTO identidade.casais (id, nome_familia) VALUES ('{casal}', 'Família Teste')");
        await ExecutarAsync(db, $"INSERT INTO identidade.usuarios (id, casal_id, nome_completo, cpf_cifrado, cpf_hmac) VALUES ('{usuario}', '{casal}', 'Ana Teste', '\\x01', '\\x02')");
        return (casal, usuario);
    }

    private static async Task<long> EscalarAsync(string conexao, string sql)
    {
        await using var db = new NpgsqlConnection(conexao);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, db);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task ExecutarAsync(NpgsqlConnection db, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, db);
        await cmd.ExecuteNonQueryAsync();
    }
}
