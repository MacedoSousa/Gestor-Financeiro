using GestorFinanceiro.Api.Infra;
using Npgsql;
using Testcontainers.PostgreSql;

namespace GestorFinanceiro.IntegrationTests.Infra;

/// <summary>Strings de conexão de um banco de teste isolado.</summary>
public sealed record BancoDeTeste(string ConexaoApp, string ConexaoSuperusuario, string ConexaoMigrador);

/// <summary>
/// Um Postgres por execução. Migra uma vez o banco-modelo "gestor_modelo"; cada teste recebe uma cópia
/// (o cadastro do casal só acontece uma vez por instalação).
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string Senha = "teste";
    private const string Modelo = "gestor_modelo";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public PostgresFixture()
    {
        // Faz a API subir com uma DEK efêmera, sem arquivos de chave.
        Environment.SetEnvironmentVariable("GF_DEV_CHAVE_EFEMERA", "1");
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await using (var db = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await db.OpenAsync();
            await ExecutarAsync(db, $"CREATE ROLE gf_migrator LOGIN PASSWORD '{Senha}'");
            await ExecutarAsync(db, $"CREATE ROLE gf_app LOGIN PASSWORD '{Senha}'");
            await ExecutarAsync(db, $"CREATE DATABASE {Modelo} OWNER gf_migrator");
        }

        await MigradorSql.AplicarAsync(Conexao(Modelo, "gf_migrator"), PastaDeMigracoes(), TextWriter.Null);

        // CREATE DATABASE ... TEMPLATE exige o modelo sem conexões abertas.
        NpgsqlConnection.ClearAllPools();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public async Task<BancoDeTeste> NovoBancoAsync()
    {
        var nome = $"gestor_{Guid.NewGuid():N}";
        await using (var db = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await db.OpenAsync();
            await ExecutarAsync(db, $"CREATE DATABASE {nome} TEMPLATE {Modelo} OWNER gf_migrator");
        }
        return new BancoDeTeste(
            ConexaoApp: Conexao(nome, "gf_app"),
            ConexaoSuperusuario: new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = nome }.ConnectionString,
            ConexaoMigrador: Conexao(nome, "gf_migrator"));
    }

    private string Conexao(string banco, string usuario) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = banco,
            Username = usuario,
            Password = Senha,
        }.ConnectionString;

    private static string PastaDeMigracoes()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidata = Path.Combine(dir.FullName, "db", "migrations");
            if (Directory.Exists(candidata)) return candidata;
        }
        throw new DirectoryNotFoundException("db/migrations não encontrada acima de " + AppContext.BaseDirectory);
    }

    private static async Task ExecutarAsync(NpgsqlConnection db, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, db);
        await cmd.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresDefinicao : ICollectionFixture<PostgresFixture>;
