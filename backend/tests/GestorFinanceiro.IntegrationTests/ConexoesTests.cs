using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GestorFinanceiro.IntegrationTests.Infra;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.Modules.Integracoes.Dominio;
using GestorFinanceiro.Modules.Integracoes.Infra;
using GestorFinanceiro.Modules.Integracoes.Pluggy;
using GestorFinanceiro.SharedKernel.Seguranca;
using GestorFinanceiro.UnitTests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
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

    // ------------------------------------------------------------------ API /api/v1/integracoes (Tarefa 2.3)

    private const string Rota = "/api/v1/integracoes/conexoes";
    private const string IdSlot1 = "id-sintetico-do-slot-1";
    private const string SegredoSlot1 = "segredo-sintetico-do-slot-1";
    private const string IdSlot2 = "id-sintetico-do-slot-2";
    private const string SegredoSlot2 = "segredo-sintetico-do-slot-2";
    private const string TituloSlotSemChaves = "As chaves deste slot não foram gravadas no servidor.";
    private const string TituloSlotEmUso = "Este slot já é usado no casal.";
    private const string TituloJaTemConexao = "Você já tem uma conexão.";
    private const string TituloItemRepetido = "Este item já está cadastrado.";
    private const string TituloSoOTitular = "Só o titular pode alterar esta conexão.";
    private const string TituloPluggyFora = "A Pluggy não respondeu. Tente de novo em alguns minutos.";

    private static string TituloRecusado(int slot) => $"O banco recusou este item com as chaves do slot {slot}.";

    /// <summary>API com a Pluggy falsa, os segredos sintéticos numa pasta temporária e os logs capturados.</summary>
    private sealed record ComApi(Casal C, ClientePluggyFalso Pluggy, LogsCapturados Logs, HttpClient Ana, HttpClient Bruno, string Pasta)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await C.DisposeAsync();
            Directory.Delete(Pasta, recursive: true);
        }
    }

    private async Task<ComApi> NovoCasalComApiAsync(bool slot2Configurado = true)
    {
        var pasta = Directory.CreateTempSubdirectory("gf-pluggy-").FullName;
        await File.WriteAllTextAsync(Path.Combine(pasta, "pluggy_1_client_id"), IdSlot1);
        await File.WriteAllTextAsync(Path.Combine(pasta, "pluggy_1_client_secret"), SegredoSlot1);
        // Slot não configurado = arquivos vazios, como o gerar-segredos.sh os cria.
        await File.WriteAllTextAsync(Path.Combine(pasta, "pluggy_2_client_id"), slot2Configurado ? IdSlot2 : "");
        await File.WriteAllTextAsync(Path.Combine(pasta, "pluggy_2_client_secret"), slot2Configurado ? SegredoSlot2 : "");

        var pluggy = new ClientePluggyFalso();
        var logs = new LogsCapturados();
        var banco = await pg.NovoBancoAsync();
        var api = new ApiDeTeste(banco, relogio: new RelogioFixo(), servicos: s =>
        {
            s.Replace(ServiceDescriptor.Singleton<IClientePluggy>(pluggy));
            s.Configure<OpcoesDaPluggy>(o => o.PastaDeSegredos = pasta);
            s.AddSingleton<ILoggerProvider>(logs);
        });
        var c = await NovoCasalAsync(api, banco);
        return new ComApi(c, pluggy, logs, await api.ClienteLogadoAsync(c.Ana), await api.ClienteLogadoAsync(c.Bruno), pasta);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<string> TituloAsync(HttpResponseMessage r) => (await JsonAsync(r)).GetProperty("title").GetString()!;

    private static Task<HttpResponseMessage> CadastrarAsync(HttpClient cliente, int slot, string itemId, string banco = "Itaú") =>
        cliente.PostAsJsonAsync(Rota, new { slot, itemId, banco });

    private static async Task<JsonElement> ConexoesAsync(HttpClient cliente)
    {
        var r = await cliente.GetAsync(Rota);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await JsonAsync(r);
    }

    private static async Task<long> ContarAsync(Casal c, string tabela) =>
        await EscalarAsync<long>(c.Banco, $"SELECT count(*) FROM integracoes.{tabela} WHERE NOT is_deleted");

    [Fact]
    public async Task Cadastro_usa_o_usuario_da_sessao()
    {
        await using var t = await NovoCasalComApiAsync();
        var itemId = ItemSintetico();
        t.Pluggy.Cadastrar(IdSlot2, itemId);

        var r = await CadastrarAsync(t.Bruno, 2, itemId);

        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var conexao = await JsonAsync(r);
        Assert.Equal(t.C.Bruno, conexao.GetProperty("usuarioId").GetGuid());
        Assert.Equal(2, conexao.GetProperty("slot").GetInt32());
        Assert.Equal(t.C.Bruno, await EscalarAsync<Guid>(t.C.Banco, "SELECT usuario_id FROM integracoes.conexoes"));
        // Autenticou com as chaves do slot escolhido e consultou o item informado (D20, D23).
        Assert.Equal([(2, IdSlot2, itemId)], t.Pluggy.Consultas);

        var lista = await ConexoesAsync(t.Bruno);
        var unica = Assert.Single(lista.GetProperty("conexoes").EnumerateArray());
        Assert.True(unica.GetProperty("ehSua").GetBoolean());
        Assert.Equal("Bruno", unica.GetProperty("apelido").GetString());
        Assert.True(unica.GetProperty("slotConfigurado").GetBoolean());
        Assert.Equal("Itaú", Assert.Single(unica.GetProperty("items").EnumerateArray()).GetProperty("banco").GetString());
    }

    [Fact]
    public async Task Outro_conjuge_ve_mas_nao_altera()
    {
        await using var t = await NovoCasalComApiAsync();
        var itemId = ItemSintetico();
        var outro = ItemSintetico();
        t.Pluggy.Cadastrar(IdSlot1, itemId);
        t.Pluggy.Cadastrar(IdSlot1, outro);
        var conexao = await JsonAsync(await CadastrarAsync(t.Ana, 1, itemId));
        var id = conexao.GetProperty("id").GetGuid();
        var item = Assert.Single(conexao.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid();

        // O Bruno vê a conexão da Ana e o estado do item…
        var daAna = Assert.Single((await ConexoesAsync(t.Bruno)).GetProperty("conexoes").EnumerateArray());
        Assert.False(daAna.GetProperty("ehSua").GetBoolean());
        Assert.Equal("Ana", daAna.GetProperty("apelido").GetString());
        Assert.Equal("ATIVO", Assert.Single(daAna.GetProperty("items").EnumerateArray()).GetProperty("estado").GetString());

        // …mas não acrescenta, não tira item e não apaga a conexão (D21).
        var acrescentar = await t.Bruno.PostAsJsonAsync($"{Rota}/{id}/items", new { itemId = outro, banco = "Nubank" });
        Assert.Equal(HttpStatusCode.Forbidden, acrescentar.StatusCode);
        Assert.Equal(TituloSoOTitular, await TituloAsync(acrescentar));
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Bruno.DeleteAsync($"{Rota}/{id}/items/{item}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Bruno.DeleteAsync($"{Rota}/{id}")).StatusCode);

        Assert.Equal(1L, await ContarAsync(t.C, "conexoes"));
        Assert.Equal(1L, await ContarAsync(t.C, "items"));
        // A Pluggy não foi chamada pelo pedido recusado.
        Assert.Single(t.Pluggy.Consultas);
    }

    [Fact]
    public async Task Slot_sem_chaves_e_recusado()
    {
        await using var t = await NovoCasalComApiAsync(slot2Configurado: false);
        var itemId = ItemSintetico();
        t.Pluggy.Cadastrar(IdSlot2, itemId);

        var r = await CadastrarAsync(t.Bruno, 2, itemId);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(TituloSlotSemChaves, await TituloAsync(r));
        Assert.Empty(t.Pluggy.Consultas);
        Assert.Equal(0L, await ContarAsync(t.C, "conexoes"));

        var slots = (await ConexoesAsync(t.Ana)).GetProperty("slots").EnumerateArray().ToList();
        Assert.Equal([1, 2], slots.Select(s => s.GetProperty("slot").GetInt32()));
        Assert.Equal([true, false], slots.Select(s => s.GetProperty("configurado").GetBoolean()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task Slot_fora_de_1_e_2_e_invalido(int slot)
    {
        await using var t = await NovoCasalComApiAsync();
        var r = await CadastrarAsync(t.Ana, slot, ItemSintetico());
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.True((await JsonAsync(r)).GetProperty("errors").TryGetProperty("slot", out _));
        Assert.Empty(t.Pluggy.Consultas);
    }

    [Fact]
    public async Task Item_recusado_pelo_banco_nao_e_gravado()
    {
        await using var t = await NovoCasalComApiAsync();
        var itemId = ItemSintetico();
        // O item existe, mas com as chaves do slot 2: com as do slot 1 o banco recusa.
        t.Pluggy.Cadastrar(IdSlot2, itemId);

        var r = await CadastrarAsync(t.Ana, 1, itemId);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(TituloRecusado(1), await TituloAsync(r));
        Assert.Equal(0L, await ContarAsync(t.C, "conexoes"));
        Assert.Equal(0L, await ContarAsync(t.C, "items"));
    }

    [Fact]
    public async Task Pluggy_fora_do_ar_nao_grava_nada()
    {
        await using var t = await NovoCasalComApiAsync();
        t.Pluggy.Falha = new HttpRequestException("sem rede");

        var r = await CadastrarAsync(t.Ana, 1, ItemSintetico());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Equal(TituloPluggyFora, await TituloAsync(r));
        Assert.Equal(0L, await ContarAsync(t.C, "conexoes"));
    }

    [Fact]
    public async Task Item_aceito_guarda_estado_e_consentimento()
    {
        await using var t = await NovoCasalComApiAsync();
        var agora = new RelogioFixo().Agora;
        var vencendo = ItemSintetico();
        var comErro = ItemSintetico();
        // 10 dias: dentro do aviso de 15. 02:30Z de 15/11 é 14/11 em São Paulo.
        t.Pluggy.Cadastrar(IdSlot1, vencendo, consentimentoVenceEm: new DateTimeOffset(2026, 11, 15, 2, 30, 0, TimeSpan.Zero));
        t.Pluggy.Cadastrar(IdSlot1, comErro, status: "LOGIN_ERROR", consentimentoVenceEm: agora.AddDays(300));

        var conexao = await JsonAsync(await CadastrarAsync(t.Ana, 1, vencendo, "Itaú"));
        var id = conexao.GetProperty("id").GetGuid();
        var segundo = await t.Ana.PostAsJsonAsync($"{Rota}/{id}/items", new { itemId = comErro, banco = "Nubank" });
        Assert.Equal(HttpStatusCode.Created, segundo.StatusCode);

        var items = Assert.Single((await ConexoesAsync(t.Ana)).GetProperty("conexoes").EnumerateArray())
            .GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("banco").GetString()!);
        Assert.Equal("CONSENTIMENTO_VENCENDO", items["Itaú"].GetProperty("estado").GetString());
        Assert.Equal("2026-11-14", items["Itaú"].GetProperty("consentimentoVenceEm").GetString());
        Assert.Equal(JsonValueKind.Null, items["Itaú"].GetProperty("motivo").ValueKind);
        Assert.Equal("ERRO", items["Nubank"].GetProperty("estado").GetString());
        Assert.Equal("LOGIN_ERROR", items["Nubank"].GetProperty("motivo").GetString());
        // O segundo item também foi validado com as chaves do slot da conexão.
        Assert.All(t.Pluggy.Consultas, q => Assert.Equal((1, IdSlot1), (q.Slot, q.ClientId)));
    }

    [Fact]
    public async Task Slot_ocupado_conexao_repetida_e_item_repetido_dao_409()
    {
        await using var t = await NovoCasalComApiAsync();
        var itemId = ItemSintetico();
        t.Pluggy.Cadastrar(IdSlot1, itemId);
        var id = (await JsonAsync(await CadastrarAsync(t.Ana, 1, itemId))).GetProperty("id").GetGuid();

        var slotOcupado = await CadastrarAsync(t.Bruno, 1, ItemSintetico());
        Assert.Equal(HttpStatusCode.Conflict, slotOcupado.StatusCode);
        Assert.Equal(TituloSlotEmUso, await TituloAsync(slotOcupado));

        var segunda = await CadastrarAsync(t.Ana, 2, ItemSintetico());
        Assert.Equal(HttpStatusCode.Conflict, segunda.StatusCode);
        Assert.Equal(TituloJaTemConexao, await TituloAsync(segunda));

        // O mesmo banco duas vezes, na mesma conexão ou na do outro cônjuge.
        var mesmo = await t.Ana.PostAsJsonAsync($"{Rota}/{id}/items", new { itemId = itemId.ToUpperInvariant(), banco = "Itaú" });
        Assert.Equal(HttpStatusCode.Conflict, mesmo.StatusCode);
        Assert.Equal(TituloItemRepetido, await TituloAsync(mesmo));
        var peloBruno = await CadastrarAsync(t.Bruno, 2, itemId);
        Assert.Equal(HttpStatusCode.Conflict, peloBruno.StatusCode);
        Assert.Equal(TituloItemRepetido, await TituloAsync(peloBruno));
        Assert.Equal(1L, await ContarAsync(t.C, "conexoes"));
        // Só o primeiro cadastro chegou ao banco: os repetidos são recusados antes.
        Assert.Single(t.Pluggy.Consultas);
    }

    [Fact]
    public async Task Titular_troca_o_item_e_a_propria_conexao()
    {
        await using var t = await NovoCasalComApiAsync();
        var itemId = ItemSintetico();
        t.Pluggy.Cadastrar(IdSlot1, itemId);
        var conexao = await JsonAsync(await CadastrarAsync(t.Ana, 1, itemId));
        var id = conexao.GetProperty("id").GetGuid();
        var item = Assert.Single(conexao.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await t.Ana.DeleteAsync($"{Rota}/{id}/items/{item}")).StatusCode);
        Assert.Empty(Assert.Single((await ConexoesAsync(t.Ana)).GetProperty("conexoes").EnumerateArray()).GetProperty("items").EnumerateArray());
        // Item apagado pode voltar (troca do item).
        Assert.Equal(HttpStatusCode.Created,
            (await t.Ana.PostAsJsonAsync($"{Rota}/{id}/items", new { itemId, banco = "Itaú" })).StatusCode);

        // Apagar a conexão apaga os items e libera o slot e o cônjuge.
        Assert.Equal(HttpStatusCode.NoContent, (await t.Ana.DeleteAsync($"{Rota}/{id}")).StatusCode);
        Assert.Equal(0L, await ContarAsync(t.C, "items"));
        Assert.Empty((await ConexoesAsync(t.Ana)).GetProperty("conexoes").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await t.Ana.DeleteAsync($"{Rota}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CadastrarAsync(t.Ana, 1, itemId)).StatusCode);
    }

    [Fact]
    public async Task Resposta_nunca_traz_item_id_nem_chaves()
    {
        await using var t = await NovoCasalComApiAsync();
        var aceito = ItemSintetico();
        var recusado = ItemSintetico();
        t.Pluggy.Cadastrar(IdSlot1, aceito);
        var corpos = new List<string>();
        async Task Guardar(HttpResponseMessage r) => corpos.Add(await r.Content.ReadAsStringAsync());

        var criada = await CadastrarAsync(t.Ana, 1, aceito);
        await Guardar(criada);
        var id = (await JsonAsync(criada)).GetProperty("id").GetGuid();
        await Guardar(await t.Ana.PostAsJsonAsync($"{Rota}/{id}/items", new { itemId = recusado, banco = "Nubank" }));
        await Guardar(await t.Ana.PostAsJsonAsync($"{Rota}/{id}/items", new { itemId = aceito, banco = "Itaú" }));
        await Guardar(await t.Bruno.PostAsJsonAsync($"{Rota}/{id}/items", new { itemId = recusado, banco = "Nubank" }));
        await Guardar(await CadastrarAsync(t.Bruno, 2, recusado));
        await Guardar(await t.Ana.GetAsync(Rota));
        await Guardar(await t.Bruno.GetAsync(Rota));
        t.Pluggy.Falha = new HttpRequestException($"falha com {recusado}");
        await Guardar(await t.Ana.PostAsJsonAsync($"{Rota}/{id}/items", new { itemId = recusado, banco = "Nubank" }));

        string[] proibidos = [aceito, recusado, IdSlot1, SegredoSlot1, IdSlot2, SegredoSlot2];
        Assert.Equal(8, corpos.Count);
        foreach (var texto in corpos.Concat(t.Logs.Linhas))
            foreach (var p in proibidos)
                Assert.DoesNotContain(p, texto, StringComparison.OrdinalIgnoreCase);
    }
}
