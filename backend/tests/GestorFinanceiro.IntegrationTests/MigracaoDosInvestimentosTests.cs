using GestorFinanceiro.IntegrationTests.Infra;
using Npgsql;

namespace GestorFinanceiro.IntegrationTests;

/// <summary>
/// V0019 (ordem D22 do CEO): os grupos de investimento que nasceram antes da regra "só ACTIVE" ficam marcados para a
/// sincronização seguinte refazer o saldo inicial; aplicação sem grupo, apagada ou conta e cartão não são marcados.
/// Dados sintéticos.
/// </summary>
[Collection("postgres")]
public class MigracaoDosInvestimentosTests(PostgresFixture pg)
{
    private static async Task ExecutarAsync(NpgsqlConnection db, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, db);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string> TextoAsync(NpgsqlConnection db, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, db);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Grupos_ja_nascidos_ficam_marcados_para_refazer_o_saldo_inicial()
    {
        var banco = await pg.NovoBancoAteAsync("V0018");
        await using var db = new NpgsqlConnection(banco.ConexaoSuperusuario);
        await db.OpenAsync();
        var casal = Guid.NewGuid();
        var conexao = Guid.NewGuid();
        var item = Guid.NewGuid();
        var grupo = Guid.NewGuid();
        await ExecutarAsync(db, $"INSERT INTO identidade.casais (id, nome_familia) VALUES ('{casal}', 'Família Teste')");
        await ExecutarAsync(db, $"INSERT INTO integracoes.conexoes (id, casal_id, usuario_id, slot) VALUES ('{conexao}', '{casal}', '{Guid.NewGuid()}', 1)");
        await ExecutarAsync(db, "INSERT INTO integracoes.items (id, casal_id, conexao_id, item_id_cifrado, item_id_hmac, banco, estado) " +
                                $"VALUES ('{item}', '{casal}', '{conexao}', '\\x01', '\\x02', 'Itaú', 'ATIVO')");
        foreach (var (externo, tipo, conta, apagado) in new[]
                 {
                     ("cdb-1", "INVESTIMENTO", $"'{grupo}'", false), ("cdb-2", "INVESTIMENTO", $"'{grupo}'", false),
                     ("cdb-sem-grupo", "INVESTIMENTO", "NULL", false), ("cdb-apagado", "INVESTIMENTO", $"'{grupo}'", true),
                     ("corrente-1", "CONTA", $"'{Guid.NewGuid()}'", false),
                 })
            await ExecutarAsync(db, "INSERT INTO integracoes.vinculos (id, casal_id, item_id, tipo, id_externo, conta_id, is_deleted) " +
                                    $"VALUES ('{Guid.NewGuid()}', '{casal}', '{item}', '{tipo}', '{externo}', {conta}, {apagado})");

        await PostgresFixture.MigrarTudoAsync(banco);

        Assert.Equal("cdb-1:true;cdb-2:true;cdb-apagado:false;cdb-sem-grupo:false;corrente-1:false", await TextoAsync(db,
            "SELECT string_agg(id_externo || ':' || refazer_saldo_inicial, ';' ORDER BY id_externo) FROM integracoes.vinculos"));
        Assert.Equal("-", await TextoAsync(db, "SELECT coalesce(max(situacao_banco), '-') FROM integracoes.vinculos"));
    }
}
