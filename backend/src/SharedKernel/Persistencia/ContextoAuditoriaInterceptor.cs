using System.Data.Common;
using GestorFinanceiro.SharedKernel.Aplicacao;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GestorFinanceiro.SharedKernel.Persistencia;

/// <summary>
/// Ao abrir a conexão, informa ao Postgres quem está operando (app.usuario_id, app.casal_id, app.ip).
/// Os triggers de auditoria leem esses valores. O Npgsql executa DISCARD ALL ao devolver a conexão ao pool.
/// </summary>
public sealed class ContextoAuditoriaInterceptor(IUsuarioAtual usuario) : DbConnectionInterceptor
{
    private const string Sql =
        "SELECT set_config('app.usuario_id', @u, false), set_config('app.casal_id', @c, false), set_config('app.ip', @i, false)";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var comando = Montar(connection);
        comando.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var comando = Montar(connection);
        await comando.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbCommand Montar(DbConnection conexao)
    {
        var comando = conexao.CreateCommand();
        comando.CommandText = Sql;
        Adicionar(comando, "u", usuario.UsuarioId?.ToString() ?? string.Empty);
        Adicionar(comando, "c", usuario.CasalId?.ToString() ?? string.Empty);
        Adicionar(comando, "i", usuario.Ip ?? string.Empty);
        return comando;
    }

    private static void Adicionar(DbCommand comando, string nome, string valor)
    {
        var parametro = comando.CreateParameter();
        parametro.ParameterName = nome;
        parametro.Value = valor;
        comando.Parameters.Add(parametro);
    }
}
