using GestorFinanceiro.IntegrationTests.Infra;
using Npgsql;

namespace GestorFinanceiro.IntegrationTests;

[Collection("postgres")]
public class CadeiaDeAuditoriaTests(PostgresFixture pg)
{
    [Fact]
    public async Task Cadeia_de_um_casal_novo_esta_integra()
    {
        var banco = await pg.NovoBancoAsync();
        var casal = await InserirCasalComAlteracaoAsync(banco);
        Assert.Equal(2, await ContarAsync(banco, $"SELECT count(*) FROM audit.eventos WHERE casal_id = '{casal}'"));
        Assert.Equal(0, await ContarAsync(banco, $"SELECT count(*) FROM audit.verificar_cadeia('{casal}')"));
    }

    [Fact]
    public async Task Adulterar_um_evento_quebra_a_cadeia_naquele_ponto()
    {
        var banco = await pg.NovoBancoAsync();
        var casal = await InserirCasalComAlteracaoAsync(banco);
        var adulterado = await AdulterarPrimeiroEventoAsync(banco, casal);

        var (seq, motivo) = await PrimeiraQuebraAsync(banco, casal);
        Assert.Equal(adulterado, seq);
        Assert.Equal("hash não confere", motivo);
    }

    [Fact]
    public async Task Elo_trocado_no_meio_da_cadeia_e_detectado()
    {
        var banco = await pg.NovoBancoAsync();
        var casal = await InserirCasalComAlteracaoAsync(banco, alteracoes: 2);
        Assert.Equal(3, await ContarAsync(banco, $"SELECT count(*) FROM audit.eventos WHERE casal_id = '{casal}'"));
        var adulterado = await TrocarHashAnteriorDoUltimoAsync(banco, casal);

        var (seq, motivo) = await PrimeiraQuebraAsync(banco, casal);
        Assert.Equal(adulterado, seq);
        Assert.Equal("elo quebrado", motivo);
    }

    [Fact]
    public async Task Cadeias_de_casais_diferentes_sao_independentes()
    {
        var banco = await pg.NovoBancoAsync();
        var a = await InserirCasalComAlteracaoAsync(banco);
        var b = await InserirCasalComAlteracaoAsync(banco);
        await AdulterarPrimeiroEventoAsync(banco, a);

        Assert.Equal(1, await ContarAsync(banco, $"SELECT count(*) FROM audit.verificar_cadeia('{a}')"));
        Assert.Equal(0, await ContarAsync(banco, $"SELECT count(*) FROM audit.verificar_cadeia('{b}')"));
    }

    // INSERT + N UPDATEs como gf_app; o trigger de auditoria gera 1 + N eventos.
    private static async Task<Guid> InserirCasalComAlteracaoAsync(BancoDeTeste banco, int alteracoes = 1)
    {
        var casal = Guid.NewGuid();
        await using var db = new NpgsqlConnection(banco.ConexaoApp);
        await db.OpenAsync();
        await ExecutarAsync(db, "SELECT set_config('app.ip', 'teste', false)");
        await ExecutarAsync(db, $"INSERT INTO identidade.casais (id, nome_familia) VALUES ('{casal}', 'Família Teste')");
        for (var i = 1; i <= alteracoes; i++)
            await ExecutarAsync(db, $"UPDATE identidade.casais SET nome_familia = 'Família Teste {i}' WHERE id = '{casal}'");
        return casal;
    }

    private static Task<long> AdulterarPrimeiroEventoAsync(BancoDeTeste banco, Guid casal) =>
        AdulterarAsync(banco, casal, primeiro: true,
            "UPDATE audit.eventos SET alteracoes = alteracoes || '{\"adulterado\": true}'::jsonb WHERE seq = @seq");

    private static Task<long> TrocarHashAnteriorDoUltimoAsync(BancoDeTeste banco, Guid casal) =>
        AdulterarAsync(banco, casal, primeiro: false,
            "UPDATE audit.eventos SET hash_anterior = '\\x00'::bytea WHERE seq = @seq");

    // Só UPDATE (nunca DELETE); o trigger de imutabilidade volta sempre, mesmo se o UPDATE falhar.
    private static async Task<long> AdulterarAsync(BancoDeTeste banco, Guid casal, bool primeiro, string update)
    {
        await using var db = new NpgsqlConnection(banco.ConexaoSuperusuario);
        await db.OpenAsync();

        await using var consulta = new NpgsqlCommand(
            $"SELECT seq FROM audit.eventos WHERE casal_id = '{casal}' ORDER BY seq {(primeiro ? "ASC" : "DESC")} LIMIT 1", db);
        var seq = (long)(await consulta.ExecuteScalarAsync())!;

        await ExecutarAsync(db, "ALTER TABLE audit.eventos DISABLE TRIGGER trg_eventos_imutavel");
        try
        {
            await using var cmd = new NpgsqlCommand(update, db);
            cmd.Parameters.AddWithValue("seq", seq);
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
        }
        finally
        {
            await ExecutarAsync(db, "ALTER TABLE audit.eventos ENABLE TRIGGER trg_eventos_imutavel");
        }
        return seq;
    }

    private static async Task<(long Seq, string Motivo)> PrimeiraQuebraAsync(BancoDeTeste banco, Guid casal)
    {
        await using var db = new NpgsqlConnection(banco.ConexaoApp);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT seq, motivo FROM audit.verificar_cadeia('{casal}')", db);
        await using var leitor = await cmd.ExecuteReaderAsync();
        Assert.True(await leitor.ReadAsync(), "A cadeia deveria ter uma quebra.");
        return (leitor.GetInt64(0), leitor.GetString(1));
    }

    private static async Task<long> ContarAsync(BancoDeTeste banco, string sql)
    {
        await using var db = new NpgsqlConnection(banco.ConexaoSuperusuario);
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
