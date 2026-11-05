using System.Text.RegularExpressions;
using Npgsql;

namespace GestorFinanceiro.Api.Infra;

/// <summary>
/// Aplica db/migrations/V{nnnn}__*.sql em ordem, cada arquivo numa transação.
/// Uma migração aplicada nunca é reaplicada; o conteúdo é conferido por hash para detectar edição.
/// </summary>
internal static partial class MigradorSql
{
    [GeneratedRegex(@"^V(\d{4})__[a-z0-9_]+\.sql$")]
    private static partial Regex NomeValido();

    public static async Task AplicarAsync(string conexao, string pasta, TextWriter log)
    {
        var arquivos = Directory.GetFiles(pasta, "*.sql")
            .Select(Path.GetFileName)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToList();

        foreach (var nome in arquivos.Where(n => !NomeValido().IsMatch(n)))
            throw new InvalidOperationException($"Nome de migração inválido: {nome} (esperado V0001__descricao.sql)");

        await using var db = new NpgsqlConnection(conexao);
        await db.OpenAsync();

        await using (var cmd = new NpgsqlCommand(
            "CREATE TABLE IF NOT EXISTS public.schema_versoes (versao text PRIMARY KEY, sha256 text NOT NULL, aplicado_em timestamptz NOT NULL DEFAULT now())", db))
            await cmd.ExecuteNonQueryAsync();

        var aplicadas = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand("SELECT versao, sha256 FROM public.schema_versoes", db))
        await using (var leitor = await cmd.ExecuteReaderAsync())
            while (await leitor.ReadAsync())
                aplicadas[leitor.GetString(0)] = leitor.GetString(1);

        foreach (var nome in arquivos)
        {
            var sql = await File.ReadAllTextAsync(Path.Combine(pasta, nome));
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sql)));

            if (aplicadas.TryGetValue(nome, out var hashAplicado))
            {
                if (!string.Equals(hashAplicado, hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"{nome} foi editada depois de aplicada. Crie uma migração nova.");
                continue;
            }

            await using var tx = await db.BeginTransactionAsync();
            await using (var cmd = new NpgsqlCommand(sql, db, tx))
                await cmd.ExecuteNonQueryAsync();
            await using (var cmd = new NpgsqlCommand("INSERT INTO public.schema_versoes (versao, sha256) VALUES (@v, @h)", db, tx))
            {
                cmd.Parameters.AddWithValue("v", nome);
                cmd.Parameters.AddWithValue("h", hash);
                await cmd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
            await log.WriteLineAsync($"aplicada {nome}");
        }

        await log.WriteLineAsync("migrações em dia.");
    }
}
