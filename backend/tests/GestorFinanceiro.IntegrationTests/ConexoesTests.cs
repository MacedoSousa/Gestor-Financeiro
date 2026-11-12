using System.Net;
using System.Net.Http.Json;
using GestorFinanceiro.IntegrationTests.Infra;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.Modules.Integracoes.Dominio;
using GestorFinanceiro.Modules.Integracoes.Infra;
using GestorFinanceiro.SharedKernel.Seguranca;
using GestorFinanceiro.UnitTests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GestorFinanceiro.IntegrationTests;

/// <summary>
/// Conexões e items da Pluggy (Fase 2, etapa 2). Uma conexão por cônjuge e por slot, só entre as não apagadas;
/// o itemId só existe cifrado (AAD integracoes.items|item_id_cifrado|id) e é único no casal pelo HMAC.
/// Dados sintéticos; nenhuma chamada à Pluggy de verdade.
/// </summary>
[Collection("postgres")]
public class ConexoesTests(PostgresFixture pg)
{
    private const string ViolacaoDeUnicidade = "23505";

    private sealed record Casal(ApiDeTeste Api, BancoDeTeste Banco, Guid CasalId, Guid Ana, Guid Bruno) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Api.DisposeAsync();
    }

    private static async Task<T> EscalarAsync<T>(BancoDeTeste banco, string sql, params (string Nome, object Valor)[] parametros)
    {
        await using var db = new NpgsqlConnection(banco.ConexaoSuperusuario);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, db);
        foreach (var (nome, valor) in parametros) cmd.Parameters.AddWithValue(nome, valor);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<Casal> NovoCasalAsync(ApiDeTeste? api = null, BancoDeTeste? banco = null)
    {
        banco ??= await pg.NovoBancoAsync();
        api ??= new ApiDeTeste(banco, relogio: new RelogioFixo());
        var pedido = new CadastroDoCasalDto(
            api.CodigoInstalacao, "Família Teste",
            new ConjugeNovoDto("Ana Teste", CpfSintetico.Gerar(1), "senha-da-ana-123"),
            new ConjugeNovoDto("Bruno Teste", CpfSintetico.Gerar(2), "senha-do-bruno-1"));
        Assert.Equal(HttpStatusCode.Created, (await api.CreateClient().PostAsJsonAsync("/api/v1/casal", pedido)).StatusCode);
        var ana = await EscalarAsync<Guid>(banco, "SELECT id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'");
        var bruno = await EscalarAsync<Guid>(banco, "SELECT id FROM identidade.usuarios WHERE nome_completo = 'Bruno Teste'");
        var casal = await EscalarAsync<Guid>(banco, "SELECT casal_id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'");
        return new Casal(api, banco, casal, ana, bruno);
    }

    /// <summary>Grava pelo DbContext do módulo, fora do HTTP; devolve o erro de unicidade do Postgres, se houver.</summary>
    private static async Task<string?> GravarAsync(Casal c, Action<IntegracoesDbContext, ICofre> acao)
    {
        await using var escopo = c.Api.Services.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<IntegracoesDbContext>();
        acao(db, escopo.ServiceProvider.GetRequiredService<ICofre>());
        try
        {
            await db.SaveChangesAsync();
            return null;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException p)
        {
            return p.SqlState;
        }
    }

    private static string ItemSintetico() => $"item-sintetico-{Guid.NewGuid():N}";

    [Fact]
    public async Task Slot_repetido_no_casal_e_recusado()
    {
        await using var c = await NovoCasalAsync();
        Assert.Null(await GravarAsync(c, (db, _) => db.Conexoes.Add(Conexao.Criar(c.CasalId, c.Ana, 1))));

        Assert.Equal(ViolacaoDeUnicidade, await GravarAsync(c, (db, _) => db.Conexoes.Add(Conexao.Criar(c.CasalId, c.Bruno, 1))));
    }

    [Fact]
    public async Task Segunda_conexao_do_mesmo_conjuge_e_recusada()
    {
        await using var c = await NovoCasalAsync();
        Assert.Null(await GravarAsync(c, (db, _) => db.Conexoes.Add(Conexao.Criar(c.CasalId, c.Ana, 1))));

        Assert.Equal(ViolacaoDeUnicidade, await GravarAsync(c, (db, _) => db.Conexoes.Add(Conexao.Criar(c.CasalId, c.Ana, 2))));
    }

    [Fact]
    public async Task Item_id_so_existe_cifrado()
    {
        await using var c = await NovoCasalAsync();
        var itemId = ItemSintetico();
        var conexao = Conexao.Criar(c.CasalId, c.Ana, 1);
        Item? item = null;
        Assert.Null(await GravarAsync(c, (db, cofre) =>
        {
            db.Conexoes.Add(conexao);
            item = Item.Criar(c.CasalId, conexao.Id, itemId, "Itaú", EstadoDoItem.Ativo, null, null, cofre);
            db.Items.Add(item);
        }));

        // Nem a linha nem a auditoria têm o texto do item.
        Assert.Equal(0L, await EscalarAsync<long>(c.Banco,
            "SELECT count(*) FROM integracoes.items i WHERE i::text LIKE '%' || @id || '%'", ("id", itemId)));
        Assert.Equal(0L, await EscalarAsync<long>(c.Banco,
            "SELECT count(*) FROM audit.eventos e WHERE e.alteracoes::text LIKE '%' || @id || '%'", ("id", itemId)));
        Assert.Equal(1L, await EscalarAsync<long>(c.Banco,
            "SELECT count(*) FROM audit.eventos WHERE tabela = 'integracoes.items'"));

        // A cifra está amarrada à linha: decifra com o AAD integracoes.items|item_id_cifrado|<id>.
        await using var escopo = c.Api.Services.CreateAsyncScope();
        var cofre = escopo.ServiceProvider.GetRequiredService<ICofre>();
        var cifrado = await EscalarAsync<byte[]>(c.Banco,
            "SELECT item_id_cifrado FROM integracoes.items WHERE id = @id", ("id", item!.Id));
        Assert.Equal(itemId, cofre.Decifrar(cifrado, Cofre.Aad("integracoes.items", "item_id_cifrado", item.Id)));
    }

    [Fact]
    public async Task Mesmo_item_duas_vezes_e_recusado()
    {
        await using var c = await NovoCasalAsync();
        var itemId = ItemSintetico();
        var daAna = Conexao.Criar(c.CasalId, c.Ana, 1);
        var doBruno = Conexao.Criar(c.CasalId, c.Bruno, 2);
        Assert.Null(await GravarAsync(c, (db, cofre) =>
        {
            db.Conexoes.AddRange(daAna, doBruno);
            db.Items.Add(Item.Criar(c.CasalId, daAna.Id, itemId, "Itaú", EstadoDoItem.Ativo, null, null, cofre));
        }));

        // Mesmo item, mesmo que pela outra conexão e com espaços em volta.
        Assert.Equal(ViolacaoDeUnicidade, await GravarAsync(c, (db, cofre) =>
            db.Items.Add(Item.Criar(c.CasalId, doBruno.Id, $"  {itemId} ", "Itaú", EstadoDoItem.Ativo, null, null, cofre))));
    }

    [Fact]
    public async Task Slot_apagado_libera_a_vaga()
    {
        await using var c = await NovoCasalAsync();
        var daAna = Conexao.Criar(c.CasalId, c.Ana, 1);
        Assert.Null(await GravarAsync(c, (db, _) => db.Conexoes.Add(daAna)));

        Assert.Null(await GravarAsync(c, (db, _) =>
        {
            db.Conexoes.Attach(daAna);
            db.Conexoes.Remove(daAna);
        }));
        Assert.True(await EscalarAsync<bool>(c.Banco,
            "SELECT is_deleted FROM integracoes.conexoes WHERE id = @id", ("id", daAna.Id)));

        Assert.Null(await GravarAsync(c, (db, _) => db.Conexoes.Add(Conexao.Criar(c.CasalId, c.Bruno, 1))));
    }
}
