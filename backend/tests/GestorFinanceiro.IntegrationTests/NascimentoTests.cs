using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using GestorFinanceiro.IntegrationTests.Infra;
using GestorFinanceiro.Modules.Contas.Aplicacao;
using GestorFinanceiro.Modules.Contas.Contracts;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.Modules.Integracoes.Dominio;
using GestorFinanceiro.Modules.Integracoes.Infra;
using GestorFinanceiro.Modules.Integracoes.Pluggy;
using GestorFinanceiro.SharedKernel.Seguranca;
using GestorFinanceiro.UnitTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace GestorFinanceiro.IntegrationTests;

/// <summary>
/// Fase 2, etapa 3: descoberta (o que o banco tem e o sistema ainda não) e nascimento das contas ligadas, com confirmação.
/// Só a Pluggy falsa e dados sintéticos.
/// </summary>
[Collection("postgres")]
public class NascimentoTests(PostgresFixture pg)
{
    private const string IdSlot1 = "id-sintetico-do-slot-1";
    private const string IdSlot2 = "id-sintetico-do-slot-2";
    private const string RotaConexoes = "/api/v1/integracoes/conexoes";

    private static readonly ContaPluggy Corrente =
        new("conta-sintetica-1", "BANK", "CHECKING_ACCOUNT", "Conta Corrente", null, "0001/12345-6", 150_000, null, null, null);

    private static readonly ContaPluggy CartaoDaAna =
        new("cartao-sintetico-1", "CREDIT", "CREDIT_CARD", "Cartão Platinum", null, null, 80_000, 500_000, "ANA TESTE", "4821");

    private sealed record Ctx(ApiDeTeste Api, BancoDeTeste Banco, ClientePluggyFalso Pluggy, HttpClient Ana, HttpClient Bruno,
        Guid AnaId, Guid BrunoId, string Pasta) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Api.DisposeAsync();
            Directory.Delete(Pasta, recursive: true);
        }
    }

    private async Task<Ctx> IniciarAsync(Action<IServiceCollection>? mais = null)
    {
        var pasta = Directory.CreateTempSubdirectory("gf-pluggy-").FullName;
        await File.WriteAllTextAsync(Path.Combine(pasta, "pluggy_1_client_id"), IdSlot1);
        await File.WriteAllTextAsync(Path.Combine(pasta, "pluggy_1_client_secret"), "segredo-sintetico-do-slot-1");
        await File.WriteAllTextAsync(Path.Combine(pasta, "pluggy_2_client_id"), IdSlot2);
        await File.WriteAllTextAsync(Path.Combine(pasta, "pluggy_2_client_secret"), "segredo-sintetico-do-slot-2");

        var pluggy = new ClientePluggyFalso();
        var banco = await pg.NovoBancoAsync();
        var api = new ApiDeTeste(banco, relogio: new RelogioFixo(), servicos: s =>
        {
            s.Replace(ServiceDescriptor.Singleton<IClientePluggy>(pluggy));
            s.Configure<OpcoesDaPluggy>(o => o.PastaDeSegredos = pasta);
            mais?.Invoke(s);
        });
        var pedido = new CadastroDoCasalDto(
            api.CodigoInstalacao, "Família Teste",
            new ConjugeNovoDto("Ana Teste", CpfSintetico.Gerar(1), "senha-da-ana-123"),
            new ConjugeNovoDto("Bruno Teste", CpfSintetico.Gerar(2), "senha-do-bruno-1"));
        Assert.Equal(HttpStatusCode.Created, (await api.CreateClient().PostAsJsonAsync("/api/v1/casal", pedido)).StatusCode);
        var ana = await EscalarAsync<Guid>(banco, "SELECT id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'");
        var bruno = await EscalarAsync<Guid>(banco, "SELECT id FROM identidade.usuarios WHERE nome_completo = 'Bruno Teste'");
        return new Ctx(api, banco, pluggy, await api.ClienteLogadoAsync(ana), await api.ClienteLogadoAsync(bruno), ana, bruno, pasta);
    }

    private static async Task<T> EscalarAsync<T>(BancoDeTeste banco, string sql)
    {
        await using var db = new NpgsqlConnection(banco.ConexaoSuperusuario);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, db);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    /// <summary>Cadastra a conexão (slot 1 para a Ana, 2 para o Bruno) com um item sintético; devolve o id do item no sistema.</summary>
    private static async Task<(Guid ItemId, string ItemPluggy)> ConectarAsync(Ctx c, HttpClient cliente, int slot, string codigoBanco = "341")
    {
        var itemPluggy = $"item-sintetico-{Guid.NewGuid():N}";
        c.Pluggy.Cadastrar(slot == 1 ? IdSlot1 : IdSlot2, itemPluggy);
        var r = await cliente.PostAsJsonAsync(RotaConexoes, new { slot, itemId = itemPluggy, codigoBanco });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return ((await JsonAsync(r)).GetProperty("items")[0].GetProperty("id").GetGuid(), itemPluggy);
    }

    private static string RotaDescoberta(Guid item) => $"/api/v1/integracoes/items/{item}/descoberta";

    private static async Task<JsonElement> DescobertaAsync(HttpClient cliente, Guid item)
    {
        var r = await cliente.GetAsync(RotaDescoberta(item));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await JsonAsync(r);
    }

    private static JsonElement ContaEncontrada(JsonElement descoberta, string idExterno) =>
        descoberta.GetProperty("contas").EnumerateArray().Single(x => x.GetProperty("idExterno").GetString() == idExterno);

    /// <summary>Grava um vínculo direto pelo DbContext, com a identidade que a descoberta calcula.</summary>
    private static async Task VincularAsync(Ctx c, Guid item, string tipo, ContaPluggy conta, Guid? contaId = null, Guid? casal = null)
    {
        await using var escopo = c.Api.Services.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<IntegracoesDbContext>();
        var cofre = escopo.ServiceProvider.GetRequiredService<ICofre>();
        casal ??= await EscalarAsync<Guid>(c.Banco, $"SELECT casal_id FROM integracoes.items WHERE id = '{item}'");
        var codigoBanco = await EscalarAsync<string>(c.Banco, $"SELECT codigo_banco FROM integracoes.items WHERE id = '{item}'");
        db.Vinculos.Add(Vinculo.Criar(casal.Value, item, tipo, conta.Id, contaId ?? Guid.CreateVersion7(),
            Vinculo.IdentidadeDe(conta, codigoBanco, cofre)));
        await db.SaveChangesAsync();
    }

    private static async Task<string> ContagensAsync(BancoDeTeste banco) => await EscalarAsync<string>(banco,
        "SELECT (SELECT count(*) FROM integracoes.vinculos) || '|' || (SELECT count(*) FROM contas.contas) || '|' || " +
        "(SELECT count(*) FROM investimentos.metas_contas) || '|' || (SELECT count(*) FROM audit.eventos)");

    // ------------------------------------------------------------------ Tarefa 3.2: descoberta

    [Fact]
    public async Task Descoberta_sugere_apelido_por_tipo_e_titular()
    {
        await using var c = await IniciarAsync();
        var (item, itemPluggy) = await ConectarAsync(c, c.Ana, 1);
        c.Pluggy.CadastrarConta(itemPluggy, Corrente);
        c.Pluggy.CadastrarConta(itemPluggy, CartaoDaAna);

        var d = await DescobertaAsync(c.Ana, item);

        Assert.Equal("Itaú", d.GetProperty("banco").GetString());
        var corrente = ContaEncontrada(d, Corrente.Id);
        Assert.Equal("CORRENTE", corrente.GetProperty("tipo").GetString());
        Assert.Equal("Itaú Corrente · Ana", corrente.GetProperty("apelidoSugerido").GetString());
        Assert.Equal(c.AnaId, corrente.GetProperty("titularSugeridoUsuarioId").GetGuid());
        Assert.Equal(JsonValueKind.Null, corrente.GetProperty("jaVemDe").ValueKind);
        var cartao = ContaEncontrada(d, CartaoDaAna.Id);
        Assert.Equal("CARTAO", cartao.GetProperty("tipo").GetString());
        Assert.Equal("Itaú Cartão · Ana", cartao.GetProperty("apelidoSugerido").GetString());
        Assert.Equal(500_000, cartao.GetProperty("limiteCentavos").GetInt64());
    }

    [Fact]
    public async Task Dias_do_cartao_vem_das_faturas()
    {
        await using var c = await IniciarAsync();
        var (item, itemPluggy) = await ConectarAsync(c, c.Ana, 1);
        c.Pluggy.CadastrarConta(itemPluggy, CartaoDaAna);
        foreach (var mes in new[] { 8, 9, 10 })
            c.Pluggy.CadastrarFatura(CartaoDaAna.Id, new FaturaPluggy($"fatura-sintetica-{mes}", new DateOnly(2026, mes, 10),
                new DateOnly(2026, mes, 3), 10_000));

        var cartao = ContaEncontrada(await DescobertaAsync(c.Ana, item), CartaoDaAna.Id);

        Assert.Equal(3, cartao.GetProperty("diaFechamentoSugerido").GetInt32());
        Assert.Equal(10, cartao.GetProperty("diaVencimentoSugerido").GetInt32());
    }

    [Fact]
    public async Task Cartao_sem_faturas_nao_sugere_dias()
    {
        await using var c = await IniciarAsync();
        var (item, itemPluggy) = await ConectarAsync(c, c.Ana, 1);
        c.Pluggy.CadastrarConta(itemPluggy, CartaoDaAna);

        var cartao = ContaEncontrada(await DescobertaAsync(c.Ana, item), CartaoDaAna.Id);

        Assert.Equal(JsonValueKind.Null, cartao.GetProperty("diaFechamentoSugerido").ValueKind);
        Assert.Equal(JsonValueKind.Null, cartao.GetProperty("diaVencimentoSugerido").ValueKind);
    }

    [Fact]
    public async Task Conta_que_ja_vem_da_outra_conexao_e_marcada()
    {
        await using var c = await IniciarAsync();
        var (itemDaAna, _) = await ConectarAsync(c, c.Ana, 1);
        await VincularAsync(c, itemDaAna, "CONTA", Corrente);
        var (itemDoBruno, pluggyDoBruno) = await ConectarAsync(c, c.Bruno, 2);
        c.Pluggy.CadastrarConta(pluggyDoBruno, Corrente with { Id = "conta-sintetica-do-bruno" });

        var conta = ContaEncontrada(await DescobertaAsync(c.Bruno, itemDoBruno), "conta-sintetica-do-bruno");

        Assert.Equal("Ana", conta.GetProperty("jaVemDe").GetString());
    }

    [Fact]
    public async Task Mesmo_banco_por_dois_items_ainda_trava()
    {
        await using var c = await IniciarAsync();
        var (itemDaAna, _) = await ConectarAsync(c, c.Ana, 1, "341");
        await VincularAsync(c, itemDaAna, "CONTA", Corrente);
        var (itemDoBruno, pluggyDoBruno) = await ConectarAsync(c, c.Bruno, 2, "341");
        c.Pluggy.CadastrarConta(pluggyDoBruno, Corrente with { Id = "conta-sintetica-do-bruno", AgenciaEConta = "0001 12345 6" });

        var conta = ContaEncontrada(await DescobertaAsync(c.Bruno, itemDoBruno), "conta-sintetica-do-bruno");

        Assert.Equal("Ana", conta.GetProperty("jaVemDe").GetString());
        Assert.Equal("Itaú Corrente · Bruno", conta.GetProperty("apelidoSugerido").GetString());
    }

    /// <summary>D18, item 1: a identidade inclui o código do banco; agência e conta iguais em bancos diferentes não colidem.</summary>
    [Fact]
    public async Task Bancos_diferentes_com_a_mesma_agencia_e_conta_nascem_os_dois()
    {
        await using var c = await IniciarAsync();
        var (itemDaAna, pluggyDaAna) = await ConectarAsync(c, c.Ana, 1, "341");
        c.Pluggy.CadastrarConta(pluggyDaAna, Corrente);
        var (itemDoBruno, pluggyDoBruno) = await ConectarAsync(c, c.Bruno, 2, "260");
        c.Pluggy.CadastrarConta(pluggyDoBruno, Corrente with { Id = "conta-sintetica-do-bruno" });
        await EsperarAsync(NascerAsync(c.Ana, itemDaAna, [Criar(Corrente.Id, "Itaú Corrente · Ana", c.AnaId)]), HttpStatusCode.NoContent);

        var conta = ContaEncontrada(await DescobertaAsync(c.Bruno, itemDoBruno), "conta-sintetica-do-bruno");
        Assert.Equal(JsonValueKind.Null, conta.GetProperty("jaVemDe").ValueKind);
        await EsperarAsync(NascerAsync(c.Bruno, itemDoBruno, [Criar("conta-sintetica-do-bruno", "Nubank Corrente · Bruno", c.BrunoId)]),
            HttpStatusCode.NoContent);

        Assert.Equal(2L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM contas.contas WHERE ligada_em IS NOT NULL"));
        Assert.Equal(2L, await EscalarAsync<long>(c.Banco, "SELECT count(DISTINCT identidade_hmac) FROM integracoes.vinculos"));
    }

    /// <summary>D18, item 1: o mesmo banco por dois items (conjunta pelos dois cônjuges) continua recusado na confirmação.</summary>
    [Fact]
    public async Task Mesma_conta_do_mesmo_banco_por_dois_items_e_recusada_na_confirmacao()
    {
        await using var c = await IniciarAsync();
        var (itemDaAna, pluggyDaAna) = await ConectarAsync(c, c.Ana, 1, "341");
        c.Pluggy.CadastrarConta(pluggyDaAna, Corrente);
        var (itemDoBruno, pluggyDoBruno) = await ConectarAsync(c, c.Bruno, 2, "341");
        c.Pluggy.CadastrarConta(pluggyDoBruno, Corrente with { Id = "conta-sintetica-do-bruno" });
        await EsperarAsync(NascerAsync(c.Ana, itemDaAna, [Criar(Corrente.Id, "Itaú Corrente · Conjunta")]), HttpStatusCode.NoContent);

        var r = await EsperarAsync(NascerAsync(c.Bruno, itemDoBruno, [Criar("conta-sintetica-do-bruno", "Itaú Corrente · Bruno")]),
            HttpStatusCode.Conflict);

        Assert.Equal("Esta conta já vem da conexão de Ana.", await TituloAsync(r));
        Assert.Equal(1L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM contas.contas WHERE ligada_em IS NOT NULL"));
    }

    private const string TituloEscolhaOBanco = "Escolha o banco desta conexão antes de ver as contas.";

    private static string RotaBancoDoItem(Guid item) => $"/api/v1/integracoes/items/{item}/banco";

    /// <summary>D18, item 1: item cadastrado antes do código (os dois de /opt) não abre a descoberta até o titular escolher o banco.</summary>
    [Fact]
    public async Task Item_sem_codigo_do_banco_pede_a_escolha_antes_das_contas()
    {
        await using var c = await IniciarAsync();
        var (item, itemPluggy) = await ConectarAsync(c, c.Ana, 1);
        c.Pluggy.CadastrarConta(itemPluggy, Corrente);
        await EscalarAsync<int>(c.Banco, $"UPDATE integracoes.items SET codigo_banco = NULL WHERE id = '{item}' RETURNING 1");

        var descoberta = await EsperarAsync(c.Ana.GetAsync(RotaDescoberta(item)), HttpStatusCode.Conflict);
        Assert.Equal(TituloEscolhaOBanco, await TituloAsync(descoberta));
        var nascimento = await EsperarAsync(NascerAsync(c.Ana, item, [Criar(Corrente.Id, "Itaú Corrente · Ana", c.AnaId)]),
            HttpStatusCode.Conflict);
        Assert.Equal(TituloEscolhaOBanco, await TituloAsync(nascimento));

        // Só o titular escolhe; o código tem 3 dígitos; "Outro" precisa do nome.
        await EsperarAsync(c.Bruno.PutAsJsonAsync(RotaBancoDoItem(item), new { codigoBanco = "341" }), HttpStatusCode.Forbidden);
        await EsperarAsync(c.Ana.PutAsJsonAsync(RotaBancoDoItem(item), new { codigoBanco = "41" }), HttpStatusCode.BadRequest);
        await EsperarAsync(c.Ana.PutAsJsonAsync(RotaBancoDoItem(item), new { codigoBanco = "999" }), HttpStatusCode.BadRequest);
        await EsperarAsync(c.Ana.PutAsJsonAsync(RotaBancoDoItem(Guid.CreateVersion7()), new { codigoBanco = "341" }), HttpStatusCode.NotFound);

        await EsperarAsync(c.Ana.PutAsJsonAsync(RotaBancoDoItem(item), new { codigoBanco = "341" }), HttpStatusCode.NoContent);

        Assert.Equal("341|Itaú", await EscalarAsync<string>(c.Banco,
            $"SELECT codigo_banco || '|' || banco FROM integracoes.items WHERE id = '{item}'"));
        await DescobertaAsync(c.Ana, item);
        // Escolhido uma vez, não troca (as identidades já gravadas dependem dele): tire o banco e cadastre de novo.
        var deNovo = await EsperarAsync(c.Ana.PutAsJsonAsync(RotaBancoDoItem(item), new { codigoBanco = "260" }), HttpStatusCode.Conflict);
        Assert.Equal("O banco desta conexão já foi escolhido.", await TituloAsync(deNovo));
    }

    /// <summary>
    /// D18, item 2: o id opaco da conta na Pluggy só aparece nas respostas autenticadas de descoberta e confirmação; nunca
    /// em log, de nenhuma categoria e em nenhum nível (padrão do LogDaPluggyTests).
    /// </summary>
    [Fact]
    public async Task Descoberta_e_confirmacao_nao_logam_o_id_da_conta()
    {
        var logs = new LogsCapturados();
        await using var c = await IniciarAsync(s => s.AddSingleton<ILoggerProvider>(logs));
        var (item, itemPluggy) = await ConectarAsync(c, c.Ana, 1);
        c.Pluggy.CadastrarConta(itemPluggy, Corrente);
        c.Pluggy.CadastrarConta(itemPluggy, CartaoDaAna);
        c.Pluggy.CadastrarInvestimento(itemPluggy, new InvestimentoPluggy("cdb-sintetico-1", "FIXED_INCOME", "CDB", "CDB", 1_000));

        await DescobertaAsync(c.Ana, item);
        await EsperarAsync(NascerAsync(c.Ana, item, [new { idExterno = "conta-sintetica-inexistente", acao = "CRIAR", apelido = "X" }]),
            HttpStatusCode.BadRequest);
        await EsperarAsync(NascerAsync(c.Ana, item,
            [Criar(Corrente.Id, "Itaú Corrente · Ana", c.AnaId), new { idExterno = CartaoDaAna.Id, acao = "NAO_IMPORTAR" }],
            [Grupo("cdb-sintetico-1", "CDBs Casa")]), HttpStatusCode.NoContent);
        await EsperarAsync(c.Ana.GetAsync(RotaDescoberta(item)), HttpStatusCode.OK);

        Assert.NotEmpty(logs.Linhas);
        string[] proibidos = [Corrente.Id, CartaoDaAna.Id, "cdb-sintetico-1", "conta-sintetica-inexistente", itemPluggy];
        foreach (var linha in logs.Linhas)
            foreach (var p in proibidos)
                Assert.DoesNotContain(p, linha, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Descoberta_lista_aplicacoes()
    {
        await using var c = await IniciarAsync();
        var (item, itemPluggy) = await ConectarAsync(c, c.Ana, 1);
        c.Pluggy.CadastrarInvestimento(itemPluggy, new InvestimentoPluggy("cdb-sintetico-1", "FIXED_INCOME", "CDB", "CDB Banco 110%", 2_000_000));

        var aplicacao = (await DescobertaAsync(c.Ana, item)).GetProperty("aplicacoes").EnumerateArray().Single();

        Assert.Equal("cdb-sintetico-1", aplicacao.GetProperty("idExterno").GetString());
        Assert.Equal("CDB Banco 110%", aplicacao.GetProperty("nome").GetString());
        Assert.Equal(2_000_000, aplicacao.GetProperty("saldoCentavos").GetInt64());
    }

    [Fact]
    public async Task Descoberta_nao_grava_nada()
    {
        await using var c = await IniciarAsync();
        var (item, itemPluggy) = await ConectarAsync(c, c.Ana, 1);
        c.Pluggy.CadastrarConta(itemPluggy, Corrente);
        c.Pluggy.CadastrarConta(itemPluggy, CartaoDaAna);
        c.Pluggy.CadastrarInvestimento(itemPluggy, new InvestimentoPluggy("cdb-sintetico-1", "FIXED_INCOME", "CDB", "CDB", 1_000));
        var antes = await ContagensAsync(c.Banco);

        await DescobertaAsync(c.Ana, item);

        Assert.Equal(antes, await ContagensAsync(c.Banco));
    }

    [Fact]
    public async Task Descoberta_e_so_do_titular_e_nao_traz_o_item_da_pluggy()
    {
        await using var c = await IniciarAsync();
        var (item, itemPluggy) = await ConectarAsync(c, c.Ana, 1);
        c.Pluggy.CadastrarConta(itemPluggy, Corrente);
        c.Pluggy.CadastrarConta(itemPluggy, CartaoDaAna);

        var doBruno = await c.Bruno.GetAsync(RotaDescoberta(item));
        var daAna = await c.Ana.GetStringAsync(RotaDescoberta(item));

        Assert.Equal(HttpStatusCode.Forbidden, doBruno.StatusCode);
        Assert.DoesNotContain(itemPluggy, daAna, StringComparison.Ordinal);
        Assert.DoesNotContain("12345", daAna, StringComparison.Ordinal);
        // Sem os GUIDs (aleatórios, só hexadecimais), que podem conter "4821" por acaso.
        var semGuids = Regex.Replace(daAna, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", "");
        Assert.DoesNotContain("4821", semGuids, StringComparison.Ordinal);
        Assert.DoesNotContain("ANA TESTE", daAna, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ Tarefa 3.3: nascimento

    private const string TituloArquiveAntes = "Arquive antes as contas que vêm deste banco.";

    private static string RotaNascimento(Guid item) => $"/api/v1/integracoes/items/{item}/nascimento";

    private static Task<HttpResponseMessage> NascerAsync(HttpClient cliente, Guid item, object[] contas, object[]? aplicacoes = null) =>
        cliente.PostAsJsonAsync(RotaNascimento(item), new { contas, aplicacoes = aplicacoes ?? [] });

    private static object Criar(string idExterno, string apelido, Guid? titular = null, int? fechamento = null, int? vencimento = null) =>
        new { idExterno, acao = "CRIAR", apelido, titularUsuarioId = titular, diaFechamento = fechamento, diaVencimento = vencimento };

    private static object Grupo(string idExterno, string? nome, Guid? metaId = null) =>
        new { idExterno, grupo = nome is null ? null : new { nome, metaId } };

    /// <summary>Ana conectada ao Itaú, com corrente e cartão sintéticos no banco.</summary>
    private static async Task<Guid> ItemComContasAsync(Ctx c)
    {
        var (item, itemPluggy) = await ConectarAsync(c, c.Ana, 1);
        c.Pluggy.CadastrarConta(itemPluggy, Corrente);
        c.Pluggy.CadastrarConta(itemPluggy, CartaoDaAna);
        return item;
    }

    private static object[] CorrenteECartao(Ctx c) =>
    [
        Criar(Corrente.Id, "Itaú Corrente · Ana", c.AnaId),
        Criar(CartaoDaAna.Id, "Itaú Cartão · Ana", c.AnaId, fechamento: 3, vencimento: 10),
    ];

    private static async Task<HttpResponseMessage> EsperarAsync(Task<HttpResponseMessage> chamada, HttpStatusCode esperado)
    {
        var r = await chamada;
        Assert.True(r.StatusCode == esperado, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return r;
    }

    private static async Task<string> TituloAsync(HttpResponseMessage r) => (await JsonAsync(r)).GetProperty("title").GetString()!;

    [Fact]
    public async Task Nada_e_criado_sem_confirmacao()
    {
        await using var c = await IniciarAsync();
        var item = await ItemComContasAsync(c);

        await DescobertaAsync(c.Ana, item);
        await DescobertaAsync(c.Ana, item);

        Assert.Equal("0|0", await EscalarAsync<string>(c.Banco,
            "SELECT (SELECT count(*) FROM integracoes.vinculos) || '|' || (SELECT count(*) FROM contas.contas)"));
    }

    [Fact]
    public async Task Confirmacao_cria_contas_ligadas_e_vinculos()
    {
        await using var c = await IniciarAsync();
        var item = await ItemComContasAsync(c);

        await EsperarAsync(NascerAsync(c.Ana, item, CorrenteECartao(c)), HttpStatusCode.NoContent);

        Assert.Equal("Itaú Cartão · Ana|CARTAO|Itaú|3|10|500000|0|2026-01-01;Itaú Corrente · Ana|CORRENTE|Itaú||||0|2026-01-01",
            await EscalarAsync<string>(c.Banco,
                "SELECT string_agg(apelido || '|' || tipo || '|' || instituicao || '|' || coalesce(dia_fechamento::text, '') || '|' || " +
                "coalesce(dia_vencimento::text, '') || '|' || coalesce(limite_centavos::text, '') || '|' || saldo_inicial_centavos || '|' || " +
                "saldo_inicial_em, ';' ORDER BY apelido) FROM contas.contas WHERE ligada_em IS NOT NULL"));
        Assert.Equal(2L, await EscalarAsync<long>(c.Banco,
            "SELECT count(*) FROM integracoes.vinculos v JOIN contas.contas k ON k.id = v.conta_id WHERE v.identidade_hmac IS NOT NULL"));
        Assert.Empty((await DescobertaAsync(c.Ana, item)).GetProperty("contas").EnumerateArray());
    }

    [Fact]
    public async Task Repetir_o_POST_nao_duplica()
    {
        await using var c = await IniciarAsync();
        var item = await ItemComContasAsync(c);

        await EsperarAsync(NascerAsync(c.Ana, item, CorrenteECartao(c)), HttpStatusCode.NoContent);
        await EsperarAsync(NascerAsync(c.Ana, item, CorrenteECartao(c)), HttpStatusCode.NoContent);

        Assert.Equal("2|2", await EscalarAsync<string>(c.Banco,
            "SELECT (SELECT count(*) FROM integracoes.vinculos) || '|' || (SELECT count(*) FROM contas.contas)"));
    }

    /// <summary>O Contas falha na segunda conta uma vez, e depois volta.</summary>
    private sealed class FalhaNaSegunda(IContasLigadas real, int[] chamadas) : IContasLigadas
    {
        public Task<ResultadoDaContaLigada> CriarAsync(NovaContaLigada pedido, CancellationToken ct) =>
            Interlocked.Increment(ref chamadas[0]) == 2 ? throw new InvalidOperationException("falha sintética") : real.CriarAsync(pedido, ct);

        public Task<bool> MarcarLigadaAsync(Guid contaId, CancellationToken ct) => real.MarcarLigadaAsync(contaId, ct);

        public Task DefinirSaldoInicialAsync(Guid contaId, long centavos, DateOnly em, CancellationToken ct) =>
            real.DefinirSaldoInicialAsync(contaId, centavos, em, ct);
    }

    [Fact]
    public async Task Falha_no_meio_e_retomada_completa_sem_duplicar()
    {
        var chamadas = new int[1];
        await using var c = await IniciarAsync(s => s.Replace(ServiceDescriptor.Scoped<IContasLigadas>(sp =>
            new FalhaNaSegunda(ActivatorUtilities.CreateInstance<ContasLigadas>(sp), chamadas))));
        var item = await ItemComContasAsync(c);

        await EsperarAsync(NascerAsync(c.Ana, item, CorrenteECartao(c)), HttpStatusCode.InternalServerError);
        Assert.Equal("2|1", await EscalarAsync<string>(c.Banco,
            "SELECT (SELECT count(*) FROM integracoes.vinculos) || '|' || (SELECT count(*) FROM contas.contas)"));

        await EsperarAsync(NascerAsync(c.Ana, item, CorrenteECartao(c)), HttpStatusCode.NoContent);

        Assert.Equal("2|2|2", await EscalarAsync<string>(c.Banco,
            "SELECT (SELECT count(*) FROM integracoes.vinculos) || '|' || (SELECT count(*) FROM contas.contas) || '|' || " +
            "(SELECT count(*) FROM integracoes.vinculos v JOIN contas.contas k ON k.id = v.conta_id)"));
    }

    [Fact]
    public async Task Dois_CDBs_no_mesmo_grupo_viram_uma_conta()
    {
        await using var c = await IniciarAsync();
        var (item, itemPluggy) = await ConectarAsync(c, c.Ana, 1);
        c.Pluggy.CadastrarInvestimento(itemPluggy, new InvestimentoPluggy("cdb-sintetico-1", "FIXED_INCOME", "CDB", "CDB 110%", 2_000_000));
        c.Pluggy.CadastrarInvestimento(itemPluggy, new InvestimentoPluggy("cdb-sintetico-2", "FIXED_INCOME", "CDB", "CDB 120%", 500_050));

        await EsperarAsync(NascerAsync(c.Ana, item, [], [Grupo("cdb-sintetico-1", "CDBs Casa"), Grupo("cdb-sintetico-2", " cdbs casa ")]),
            HttpStatusCode.NoContent);

        var hoje = ConversaoPluggy.DiaDeSaoPaulo(new RelogioFixo().GetUtcNow());
        Assert.Equal($"CDBs Casa|INVESTIMENTO|2500050|{hoje:yyyy-MM-dd}", await EscalarAsync<string>(c.Banco,
            "SELECT string_agg(apelido || '|' || tipo || '|' || saldo_inicial_centavos || '|' || saldo_inicial_em, ';') FROM contas.contas"));
        Assert.Equal(1L, await EscalarAsync<long>(c.Banco, "SELECT count(DISTINCT conta_id) FROM integracoes.vinculos WHERE tipo = 'INVESTIMENTO'"));
        Assert.Equal(2L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM integracoes.vinculos WHERE tipo = 'INVESTIMENTO'"));
    }

    [Fact]
    public async Task Aplicacao_sem_grupo_fica_aguardando()
    {
        await using var c = await IniciarAsync();
        var (item, itemPluggy) = await ConectarAsync(c, c.Ana, 1);
        c.Pluggy.CadastrarInvestimento(itemPluggy, new InvestimentoPluggy("cdb-sintetico-1", "FIXED_INCOME", "CDB", "CDB", 1_000));

        await EsperarAsync(NascerAsync(c.Ana, item, [], [Grupo("cdb-sintetico-1", null)]), HttpStatusCode.NoContent);

        Assert.Equal("0|1", await EscalarAsync<string>(c.Banco,
            "SELECT (SELECT count(*) FROM contas.contas) || '|' || (SELECT count(*) FROM integracoes.vinculos WHERE conta_id IS NULL)"));
        Assert.Single((await DescobertaAsync(c.Ana, item)).GetProperty("aplicacoes").EnumerateArray());
    }

    [Fact]
    public async Task Grupo_com_meta_liga_a_conta_a_meta()
    {
        await using var c = await IniciarAsync();
        var poupanca = await c.Ana.PostAsJsonAsync("/api/v1/contas", new
        {
            tipo = "POUPANCA", apelido = "Poupança", saldoInicialCentavos = 0L, saldoInicialEm = "2026-01-01",
        });
        var poupancaId = Guid.Parse(poupanca.Headers.Location!.ToString().Split('/').Last());
        var meta = await EsperarAsync(c.Ana.PostAsJsonAsync("/api/v1/metas", new
        {
            nome = "Casa", valorAlvoCentavos = 1_000_000L, aportePlanejadoCentavos = 0L, rendimentoMensalPercentual = 0m,
            contaIds = new[] { poupancaId },
        }), HttpStatusCode.Created);
        var metaId = (await JsonAsync(meta)).GetProperty("id").GetGuid();
        var (item, itemPluggy) = await ConectarAsync(c, c.Ana, 1);
        c.Pluggy.CadastrarInvestimento(itemPluggy, new InvestimentoPluggy("cdb-sintetico-1", "FIXED_INCOME", "CDB", "CDB", 1_000));

        await EsperarAsync(NascerAsync(c.Ana, item, [], [Grupo("cdb-sintetico-1", "CDBs Casa", metaId)]), HttpStatusCode.NoContent);

        Assert.Equal(1L, await EscalarAsync<long>(c.Banco,
            $"SELECT count(*) FROM investimentos.metas_contas m JOIN contas.contas k ON k.id = m.conta_id " +
            $"WHERE m.meta_id = '{metaId}' AND k.apelido = 'CDBs Casa' AND NOT m.is_deleted"));
    }

    [Fact]
    public async Task Ligar_a_conta_existente_marca_ligada()
    {
        await using var c = await IniciarAsync();
        var manual = await EsperarAsync(c.Ana.PostAsJsonAsync("/api/v1/contas", new
        {
            tipo = "CORRENTE", apelido = "Minha corrente", instituicao = "Itaú", saldoInicialCentavos = 5_000L, saldoInicialEm = "2026-01-01",
        }), HttpStatusCode.Created);
        var contaId = Guid.Parse(manual.Headers.Location!.ToString().Split('/').Last());
        var item = await ItemComContasAsync(c);

        await EsperarAsync(NascerAsync(c.Ana, item, [new { idExterno = Corrente.Id, acao = "LIGAR", contaExistenteId = contaId }]),
            HttpStatusCode.NoContent);

        Assert.Equal("1|true", await EscalarAsync<string>(c.Banco,
            $"SELECT (SELECT count(*) FROM contas.contas) || '|' || (SELECT ligada_em IS NOT NULL FROM contas.contas WHERE id = '{contaId}')"));
        Assert.Equal(contaId, await EscalarAsync<Guid>(c.Banco, $"SELECT conta_id FROM integracoes.vinculos WHERE id_externo = '{Corrente.Id}'"));
    }

    [Fact]
    public async Task Ligar_cartao_a_conta_corrente_e_recusado()
    {
        await using var c = await IniciarAsync();
        var manual = await EsperarAsync(c.Ana.PostAsJsonAsync("/api/v1/contas", new
        {
            tipo = "CORRENTE", apelido = "Minha corrente", saldoInicialCentavos = 0L, saldoInicialEm = "2026-01-01",
        }), HttpStatusCode.Created);
        var contaId = Guid.Parse(manual.Headers.Location!.ToString().Split('/').Last());
        var item = await ItemComContasAsync(c);

        await EsperarAsync(NascerAsync(c.Ana, item, [new { idExterno = CartaoDaAna.Id, acao = "LIGAR", contaExistenteId = contaId }]),
            HttpStatusCode.BadRequest);

        Assert.Equal(0L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM integracoes.vinculos"));
    }

    [Fact]
    public async Task Outro_conjuge_recebe_403()
    {
        await using var c = await IniciarAsync();
        var item = await ItemComContasAsync(c);

        await EsperarAsync(NascerAsync(c.Bruno, item, CorrenteECartao(c)), HttpStatusCode.Forbidden);

        Assert.Equal(0L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM integracoes.vinculos"));
    }

    [Fact]
    public async Task Apelido_repetido_devolve_409_com_a_sugestao()
    {
        await using var c = await IniciarAsync();
        await EsperarAsync(c.Ana.PostAsJsonAsync("/api/v1/contas", new
        {
            tipo = "DINHEIRO", apelido = "Itaú Corrente · Ana", saldoInicialCentavos = 0L, saldoInicialEm = "2026-01-01",
        }), HttpStatusCode.Created);
        var item = await ItemComContasAsync(c);

        var r = await EsperarAsync(NascerAsync(c.Ana, item, [Criar(Corrente.Id, "Itaú Corrente · Ana", c.AnaId)]), HttpStatusCode.Conflict);

        var problema = await JsonAsync(r);
        Assert.Equal("Já existe uma conta com esse nome.", problema.GetProperty("title").GetString());
        Assert.Equal("Itaú Corrente · Ana (2)", problema.GetProperty("sugestao").GetString());
        Assert.Equal(Corrente.Id, problema.GetProperty("idExterno").GetString());
    }

    [Fact]
    public async Task Conta_que_ja_vem_da_outra_conexao_nao_nasce_de_novo()
    {
        await using var c = await IniciarAsync();
        var (itemDaAna, _) = await ConectarAsync(c, c.Ana, 1);
        await VincularAsync(c, itemDaAna, "CONTA", Corrente);
        var (itemDoBruno, pluggyDoBruno) = await ConectarAsync(c, c.Bruno, 2);
        c.Pluggy.CadastrarConta(pluggyDoBruno, Corrente with { Id = "conta-sintetica-do-bruno" });

        var r = await EsperarAsync(NascerAsync(c.Bruno, itemDoBruno, [Criar("conta-sintetica-do-bruno", "Itaú Corrente · Bruno")]),
            HttpStatusCode.Conflict);

        Assert.Equal("Esta conta já vem da conexão de Ana.", await TituloAsync(r));
        Assert.Equal(1L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM integracoes.vinculos"));
    }

    [Fact]
    public async Task Nao_importar_registra_e_nao_pergunta_de_novo()
    {
        await using var c = await IniciarAsync();
        var item = await ItemComContasAsync(c);

        await EsperarAsync(NascerAsync(c.Ana, item, [new { idExterno = CartaoDaAna.Id, acao = "NAO_IMPORTAR" }]), HttpStatusCode.NoContent);

        var d = await DescobertaAsync(c.Ana, item);
        Assert.Equal([Corrente.Id], d.GetProperty("contas").EnumerateArray().Select(x => x.GetProperty("idExterno").GetString()));
        var naoImportada = d.GetProperty("naoImportadas").EnumerateArray().Single();
        Assert.Equal(CartaoDaAna.Id, naoImportada.GetProperty("idExterno").GetString());
    }

    [Fact]
    public async Task Conta_nao_importada_nao_vira_conta_nem_sincroniza()
    {
        await using var c = await IniciarAsync();
        var item = await ItemComContasAsync(c);

        await EsperarAsync(NascerAsync(c.Ana, item, [new { idExterno = CartaoDaAna.Id, acao = "NAO_IMPORTAR" }]), HttpStatusCode.NoContent);

        Assert.Equal("0|1|1", await EscalarAsync<string>(c.Banco,
            "SELECT (SELECT count(*) FROM contas.contas) || '|' || " +
            "(SELECT count(*) FROM integracoes.vinculos WHERE conta_id IS NULL AND nao_importar_em IS NOT NULL) || '|' || " +
            "(SELECT count(*) FROM integracoes.vinculos WHERE importado_ate IS NULL AND historico_desde IS NULL)"));
    }

    [Fact]
    public async Task Incluir_depois_cria_a_conta_ligada()
    {
        await using var c = await IniciarAsync();
        var item = await ItemComContasAsync(c);
        await EsperarAsync(NascerAsync(c.Ana, item, [new { idExterno = CartaoDaAna.Id, acao = "NAO_IMPORTAR" }]), HttpStatusCode.NoContent);
        var vinculo = (await DescobertaAsync(c.Ana, item)).GetProperty("naoImportadas")[0].GetProperty("vinculoId").GetGuid();

        await EsperarAsync(c.Ana.PostAsJsonAsync($"/api/v1/integracoes/vinculos/{vinculo}/inclusao", new
        {
            acao = "CRIAR", apelido = "Cartão da Ana", titularUsuarioId = c.AnaId, diaFechamento = 3, diaVencimento = 10,
        }), HttpStatusCode.NoContent);

        Assert.Equal("Cartão da Ana|true|false", await EscalarAsync<string>(c.Banco,
            $"SELECT k.apelido || '|' || (k.ligada_em IS NOT NULL) || '|' || (v.nao_importar_em IS NOT NULL) " +
            $"FROM integracoes.vinculos v JOIN contas.contas k ON k.id = v.conta_id WHERE v.id = '{vinculo}'"));
        Assert.Empty((await DescobertaAsync(c.Ana, item)).GetProperty("naoImportadas").EnumerateArray());
    }

    [Fact]
    public async Task Incluir_e_so_para_conta_nao_importada()
    {
        await using var c = await IniciarAsync();
        var item = await ItemComContasAsync(c);
        await EsperarAsync(NascerAsync(c.Ana, item, CorrenteECartao(c)), HttpStatusCode.NoContent);
        var vinculo = await EscalarAsync<Guid>(c.Banco, $"SELECT id FROM integracoes.vinculos WHERE id_externo = '{Corrente.Id}'");

        await EsperarAsync(c.Ana.PostAsJsonAsync($"/api/v1/integracoes/vinculos/{vinculo}/inclusao", new
        {
            acao = "CRIAR", apelido = "Outra",
        }), HttpStatusCode.Conflict);
    }

    // ------------------------------------------------------------------ Decisão do CEO na E3: apagar com conta ligada

    private static async Task<(Guid Conexao, Guid Item)> NascidoAsync(Ctx c)
    {
        var item = await ItemComContasAsync(c);
        await EsperarAsync(NascerAsync(c.Ana, item, CorrenteECartao(c)), HttpStatusCode.NoContent);
        return (await EscalarAsync<Guid>(c.Banco, $"SELECT conexao_id FROM integracoes.items WHERE id = '{item}'"), item);
    }

    private static async Task ArquivarTodasAsync(Ctx c)
    {
        foreach (var conta in (await JsonAsync(await c.Ana.GetAsync("/api/v1/contas"))).EnumerateArray())
            await EsperarAsync(c.Ana.PutAsJsonAsync($"/api/v1/contas/{conta.GetProperty("id").GetGuid()}", new
            {
                tipo = conta.GetProperty("tipo").GetString(), apelido = conta.GetProperty("apelido").GetString(),
                instituicao = conta.GetProperty("instituicao").GetString(),
                saldoInicialCentavos = conta.GetProperty("saldoInicialCentavos").GetInt64(),
                saldoInicialEm = conta.GetProperty("saldoInicialEm").GetString(),
                diaFechamento = conta.GetProperty("diaFechamento").ValueKind == JsonValueKind.Null ? (int?)null : conta.GetProperty("diaFechamento").GetInt32(),
                diaVencimento = conta.GetProperty("diaVencimento").ValueKind == JsonValueKind.Null ? (int?)null : conta.GetProperty("diaVencimento").GetInt32(),
                arquivada = true,
            }), HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Tirar_banco_com_conta_nao_arquivada_da_409()
    {
        await using var c = await IniciarAsync();
        var (conexao, item) = await NascidoAsync(c);

        var r = await EsperarAsync(c.Ana.DeleteAsync($"{RotaConexoes}/{conexao}/items/{item}"), HttpStatusCode.Conflict);

        Assert.Equal(TituloArquiveAntes, await TituloAsync(r));
        Assert.False(await EscalarAsync<bool>(c.Banco, $"SELECT is_deleted FROM integracoes.items WHERE id = '{item}'"));
    }

    [Fact]
    public async Task Apagar_conexao_com_conta_nao_arquivada_da_409()
    {
        await using var c = await IniciarAsync();
        var (conexao, _) = await NascidoAsync(c);

        var r = await EsperarAsync(c.Ana.DeleteAsync($"{RotaConexoes}/{conexao}"), HttpStatusCode.Conflict);

        Assert.Equal(TituloArquiveAntes, await TituloAsync(r));
        Assert.False(await EscalarAsync<bool>(c.Banco, $"SELECT is_deleted FROM integracoes.conexoes WHERE id = '{conexao}'"));
    }

    [Fact]
    public async Task Com_as_contas_arquivadas_tirar_o_banco_apaga_os_vinculos()
    {
        await using var c = await IniciarAsync();
        var (conexao, item) = await NascidoAsync(c);
        await ArquivarTodasAsync(c);

        await EsperarAsync(c.Ana.DeleteAsync($"{RotaConexoes}/{conexao}/items/{item}"), HttpStatusCode.NoContent);

        Assert.Equal("true|0", await EscalarAsync<string>(c.Banco,
            $"SELECT (SELECT is_deleted FROM integracoes.items WHERE id = '{item}') || '|' || " +
            "(SELECT count(*) FROM integracoes.vinculos WHERE NOT is_deleted)"));
        Assert.Equal(2L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM contas.contas WHERE NOT is_deleted AND arquivada"));
    }

    [Fact]
    public async Task Com_as_contas_arquivadas_apagar_a_conexao_apaga_os_vinculos()
    {
        await using var c = await IniciarAsync();
        var (conexao, _) = await NascidoAsync(c);
        await ArquivarTodasAsync(c);

        await EsperarAsync(c.Ana.DeleteAsync($"{RotaConexoes}/{conexao}"), HttpStatusCode.NoContent);

        Assert.Equal(0L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM integracoes.vinculos WHERE NOT is_deleted"));
    }

    // ------------------------------------------------------------------ Revisão de segurança da E3: retomada do LIGAR

    /// <summary>O Contas falha ao marcar ligada uma vez (exceção) ou responde que a conta não existe.</summary>
    private sealed class MarcarFalha(IContasLigadas real, int[] chamadas, bool responderFalso) : IContasLigadas
    {
        public Task<ResultadoDaContaLigada> CriarAsync(NovaContaLigada pedido, CancellationToken ct) => real.CriarAsync(pedido, ct);

        public Task<bool> MarcarLigadaAsync(Guid contaId, CancellationToken ct) =>
            responderFalso ? Task.FromResult(false)
            : Interlocked.Increment(ref chamadas[0]) == 1 ? throw new InvalidOperationException("falha sintética")
            : real.MarcarLigadaAsync(contaId, ct);

        public Task DefinirSaldoInicialAsync(Guid contaId, long centavos, DateOnly em, CancellationToken ct) =>
            real.DefinirSaldoInicialAsync(contaId, centavos, em, ct);
    }

    private static async Task<Guid> CorrenteManualAsync(Ctx c, string apelido = "Minha corrente")
    {
        var r = await EsperarAsync(c.Ana.PostAsJsonAsync("/api/v1/contas", new
        {
            tipo = "CORRENTE", apelido, saldoInicialCentavos = 0L, saldoInicialEm = "2026-01-01",
        }), HttpStatusCode.Created);
        return Guid.Parse(r.Headers.Location!.ToString().Split('/').Last());
    }

    private static object LigarA(Guid conta) => new { idExterno = Corrente.Id, acao = "LIGAR", contaExistenteId = conta };

    [Fact]
    public async Task Retomar_o_LIGAR_que_falhou_completa_a_ligacao()
    {
        var chamadas = new int[1];
        await using var c = await IniciarAsync(s => s.Replace(ServiceDescriptor.Scoped<IContasLigadas>(sp =>
            new MarcarFalha(ActivatorUtilities.CreateInstance<ContasLigadas>(sp), chamadas, responderFalso: false))));
        var conta = await CorrenteManualAsync(c);
        var item = await ItemComContasAsync(c);

        await EsperarAsync(NascerAsync(c.Ana, item, [LigarA(conta)]), HttpStatusCode.InternalServerError);
        Assert.Contains(Corrente.Id, (await DescobertaAsync(c.Ana, item)).GetProperty("contas").EnumerateArray()
            .Select(x => x.GetProperty("idExterno").GetString()));

        await EsperarAsync(NascerAsync(c.Ana, item, [LigarA(conta)]), HttpStatusCode.NoContent);

        Assert.Equal("1|true|1", await EscalarAsync<string>(c.Banco,
            $"SELECT (SELECT count(*) FROM contas.contas) || '|' || (SELECT ligada_em IS NOT NULL FROM contas.contas WHERE id = '{conta}') " +
            $"|| '|' || (SELECT count(*) FROM integracoes.vinculos WHERE conta_id = '{conta}')"));
        Assert.DoesNotContain(Corrente.Id, (await DescobertaAsync(c.Ana, item)).GetProperty("contas").EnumerateArray()
            .Select(x => x.GetProperty("idExterno").GetString()));
    }

    [Fact]
    public async Task Criar_sobre_um_LIGAR_pela_metade_e_recusado()
    {
        var chamadas = new int[1];
        await using var c = await IniciarAsync(s => s.Replace(ServiceDescriptor.Scoped<IContasLigadas>(sp =>
            new MarcarFalha(ActivatorUtilities.CreateInstance<ContasLigadas>(sp), chamadas, responderFalso: false))));
        var conta = await CorrenteManualAsync(c);
        var item = await ItemComContasAsync(c);
        await EsperarAsync(NascerAsync(c.Ana, item, [LigarA(conta)]), HttpStatusCode.InternalServerError);

        var r = await EsperarAsync(NascerAsync(c.Ana, item, [Criar(Corrente.Id, "Itaú Corrente · Ana", c.AnaId)]), HttpStatusCode.Conflict);

        Assert.Equal("Esta conta do banco já está ligada a outra conta.", await TituloAsync(r));
        Assert.Equal(1L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM contas.contas"));
    }

    [Fact]
    public async Task Marcar_ligada_de_conta_que_sumiu_nao_responde_sucesso()
    {
        await using var c = await IniciarAsync(s => s.Replace(ServiceDescriptor.Scoped<IContasLigadas>(sp =>
            new MarcarFalha(ActivatorUtilities.CreateInstance<ContasLigadas>(sp), new int[1], responderFalso: true))));
        var conta = await CorrenteManualAsync(c);
        var item = await ItemComContasAsync(c);

        var r = await EsperarAsync(NascerAsync(c.Ana, item, [LigarA(conta)]), HttpStatusCode.Conflict);

        Assert.Equal("A conta escolhida já não existe. Atualize a página.", await TituloAsync(r));
    }

    [Fact]
    public async Task Vinculo_que_aponta_para_conta_apagada_ganha_conta_nova()
    {
        await using var c = await IniciarAsync();
        var apagada = await CorrenteManualAsync(c, "Vai ser apagada");
        await EsperarAsync(c.Ana.DeleteAsync($"/api/v1/contas/{apagada}"), HttpStatusCode.NoContent);
        var item = await ItemComContasAsync(c);
        await VincularAsync(c, item, "CONTA", Corrente, contaId: apagada);

        await EsperarAsync(NascerAsync(c.Ana, item, [Criar(Corrente.Id, "Itaú Corrente · Ana", c.AnaId)]), HttpStatusCode.NoContent);

        var nova = await EscalarAsync<Guid>(c.Banco, $"SELECT conta_id FROM integracoes.vinculos WHERE id_externo = '{Corrente.Id}' AND NOT is_deleted");
        Assert.NotEqual(apagada, nova);
        Assert.True(await EscalarAsync<bool>(c.Banco,
            $"SELECT ligada_em IS NOT NULL AND NOT is_deleted AND apelido = 'Itaú Corrente · Ana' FROM contas.contas WHERE id = '{nova}'"));
    }

    [Fact]
    public async Task Reagrupar_aplicacao_ja_nascida_e_recusado()
    {
        await using var c = await IniciarAsync();
        var (item, itemPluggy) = await ConectarAsync(c, c.Ana, 1);
        c.Pluggy.CadastrarInvestimento(itemPluggy, new InvestimentoPluggy("cdb-sintetico-1", "FIXED_INCOME", "CDB", "CDB", 1_000));
        await EsperarAsync(NascerAsync(c.Ana, item, [], [Grupo("cdb-sintetico-1", "CDBs Casa")]), HttpStatusCode.NoContent);

        var r = await EsperarAsync(NascerAsync(c.Ana, item, [], [Grupo("cdb-sintetico-1", "CDBs Carro")]), HttpStatusCode.Conflict);

        Assert.Equal("Esta aplicação já está em outro grupo.", await TituloAsync(r));
        Assert.Equal(1L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM contas.contas"));
    }

    // ------------------------------------------------------------------ Revisão de segurança da E3: isolamento entre casais

    private sealed record OutroCasal(Guid Casal, Guid Conta, Guid Meta, Guid Item, Guid Vinculo);

    /// <summary>Outro casal, direto no banco (a instalação só cadastra um pela API): conta, meta, conexão, item e vínculo.</summary>
    private static async Task<OutroCasal> OutroCasalAsync(Ctx c)
    {
        var o = new OutroCasal(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var conexao = Guid.CreateVersion7();
        await EscalarAsync<int>(c.Banco, $"""
            INSERT INTO identidade.casais (id, nome_familia) VALUES ('{o.Casal}', 'Outra Família');
            INSERT INTO contas.contas (id, casal_id, tipo, apelido, saldo_inicial_em) VALUES ('{o.Conta}', '{o.Casal}', 'CORRENTE', 'Corrente de outro casal', '2026-01-01');
            INSERT INTO investimentos.metas (id, casal_id, nome, valor_alvo_centavos, aporte_planejado_centavos, taxa_mensal)
                VALUES ('{o.Meta}', '{o.Casal}', 'Meta de outro casal', 1000, 0, 0);
            INSERT INTO integracoes.conexoes (id, casal_id, usuario_id, slot) VALUES ('{conexao}', '{o.Casal}', gen_random_uuid(), 1);
            INSERT INTO integracoes.items (id, casal_id, conexao_id, item_id_cifrado, item_id_hmac, banco, estado)
                VALUES ('{o.Item}', '{o.Casal}', '{conexao}', '\x00', '\x01', 'Itaú', 'ATIVO');
            INSERT INTO integracoes.vinculos (id, casal_id, item_id, tipo, id_externo, nao_importar_em)
                VALUES ('{o.Vinculo}', '{o.Casal}', '{o.Item}', 'CONTA', 'conta-de-outro-casal', now());
            SELECT 1;
            """);
        return o;
    }

    private static Task<long> VinculosDoCasalAsync(Ctx c) =>
        EscalarAsync<long>(c.Banco, "SELECT count(*) FROM integracoes.vinculos v JOIN identidade.casais k ON k.id = v.casal_id WHERE k.nome_familia = 'Família Teste'");

    [Fact]
    public async Task Item_de_outro_casal_nao_existe_para_a_descoberta_nem_para_o_nascimento()
    {
        await using var c = await IniciarAsync();
        var o = await OutroCasalAsync(c);

        await EsperarAsync(c.Ana.GetAsync(RotaDescoberta(o.Item)), HttpStatusCode.NotFound);
        await EsperarAsync(NascerAsync(c.Ana, o.Item, [Criar("conta-de-outro-casal", "Qualquer")]), HttpStatusCode.NotFound);

        Assert.Equal(1L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM integracoes.vinculos"));
    }

    [Fact]
    public async Task Vinculo_de_outro_casal_nao_existe_para_a_inclusao()
    {
        await using var c = await IniciarAsync();
        var o = await OutroCasalAsync(c);

        await EsperarAsync(c.Ana.PostAsJsonAsync($"/api/v1/integracoes/vinculos/{o.Vinculo}/inclusao", new { acao = "CRIAR", apelido = "Minha" }),
            HttpStatusCode.NotFound);

        Assert.True(await EscalarAsync<bool>(c.Banco, $"SELECT nao_importar_em IS NOT NULL AND conta_id IS NULL FROM integracoes.vinculos WHERE id = '{o.Vinculo}'"));
    }

    [Fact]
    public async Task Ligar_a_conta_de_outro_casal_e_recusado()
    {
        await using var c = await IniciarAsync();
        var o = await OutroCasalAsync(c);
        var item = await ItemComContasAsync(c);

        await EsperarAsync(NascerAsync(c.Ana, item, [LigarA(o.Conta)]), HttpStatusCode.BadRequest);

        Assert.Equal(0L, await VinculosDoCasalAsync(c));
        Assert.False(await EscalarAsync<bool>(c.Banco, $"SELECT ligada_em IS NOT NULL FROM contas.contas WHERE id = '{o.Conta}'"));
    }

    [Fact]
    public async Task Meta_de_outro_casal_e_recusada_antes_de_criar_qualquer_coisa()
    {
        await using var c = await IniciarAsync();
        var o = await OutroCasalAsync(c);
        var (item, itemPluggy) = await ConectarAsync(c, c.Ana, 1);
        c.Pluggy.CadastrarInvestimento(itemPluggy, new InvestimentoPluggy("cdb-sintetico-1", "FIXED_INCOME", "CDB", "CDB", 1_000));

        await EsperarAsync(NascerAsync(c.Ana, item, [], [Grupo("cdb-sintetico-1", "CDBs Casa", o.Meta)]), HttpStatusCode.BadRequest);

        Assert.Equal("0|1|0", await EscalarAsync<string>(c.Banco,
            "SELECT (SELECT count(*) FROM investimentos.metas_contas) || '|' || (SELECT count(*) FROM contas.contas) || '|' || " +
            "(SELECT count(*) FROM integracoes.vinculos WHERE tipo = 'INVESTIMENTO')"));
    }

    [Fact]
    public async Task Titular_fora_do_casal_e_recusado_antes_de_gravar()
    {
        await using var c = await IniciarAsync();
        var item = await ItemComContasAsync(c);

        await EsperarAsync(NascerAsync(c.Ana, item, [Criar(Corrente.Id, "Itaú Corrente · X", Guid.CreateVersion7())]), HttpStatusCode.BadRequest);

        Assert.Equal("0|0", await EscalarAsync<string>(c.Banco,
            "SELECT (SELECT count(*) FROM integracoes.vinculos) || '|' || (SELECT count(*) FROM contas.contas)"));
    }

    [Fact]
    public async Task Vinculo_com_casal_diferente_do_item_e_recusado_pelo_banco()
    {
        await using var c = await IniciarAsync();
        var o = await OutroCasalAsync(c);
        var (item, _) = await ConectarAsync(c, c.Ana, 1);

        var erro = await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() => VincularAsync(c, item, "CONTA", Corrente, casal: o.Casal));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, (erro.InnerException as PostgresException)?.SqlState);
    }

    // ------------------------------------------------------------------ Revisão de segurança da E3, segunda rodada

    private static async Task<Guid> AplicacoesAsync(Ctx c)
    {
        var (item, itemPluggy) = await ConectarAsync(c, c.Ana, 1);
        c.Pluggy.CadastrarInvestimento(itemPluggy, new InvestimentoPluggy("cdb-sintetico-1", "FIXED_INCOME", "CDB", "CDB 1", 1_000));
        c.Pluggy.CadastrarInvestimento(itemPluggy, new InvestimentoPluggy("cdb-sintetico-2", "FIXED_INCOME", "CDB", "CDB 2", 2_000));
        // Um apelido já usado derruba a criação do grupo "Investimentos Itaú" com 409, depois de os vínculos apontarem para ele.
        await EsperarAsync(c.Ana.PostAsJsonAsync("/api/v1/contas", new
        {
            tipo = "DINHEIRO", apelido = "Investimentos Itaú", saldoInicialCentavos = 0L, saldoInicialEm = "2026-01-01",
        }), HttpStatusCode.Created);
        await EsperarAsync(NascerAsync(c.Ana, item, [], [Grupo("cdb-sintetico-1", "Investimentos Itaú"), Grupo("cdb-sintetico-2", "Investimentos Itaú")]),
            HttpStatusCode.Conflict);
        return item;
    }

    [Fact]
    public async Task Separar_um_grupo_que_nao_chegou_a_nascer_cria_uma_conta_por_grupo()
    {
        await using var c = await IniciarAsync();
        var item = await AplicacoesAsync(c);

        await EsperarAsync(NascerAsync(c.Ana, item, [], [Grupo("cdb-sintetico-1", "CDBs Casa"), Grupo("cdb-sintetico-2", "CDBs Carro")]),
            HttpStatusCode.NoContent);

        Assert.Equal("CDBs Carro|2000;CDBs Casa|1000", await EscalarAsync<string>(c.Banco,
            "SELECT string_agg(apelido || '|' || saldo_inicial_centavos, ';' ORDER BY apelido) FROM contas.contas WHERE tipo = 'INVESTIMENTO'"));
        Assert.Equal(2L, await EscalarAsync<long>(c.Banco,
            "SELECT count(DISTINCT v.conta_id) FROM integracoes.vinculos v JOIN contas.contas k ON k.id = v.conta_id WHERE v.tipo = 'INVESTIMENTO'"));
    }

    [Fact]
    public async Task Aplicacao_tirada_de_um_grupo_que_nao_nasceu_volta_a_aguardar()
    {
        await using var c = await IniciarAsync();
        var item = await AplicacoesAsync(c);

        await EsperarAsync(NascerAsync(c.Ana, item, [], [Grupo("cdb-sintetico-1", "CDBs Casa"), Grupo("cdb-sintetico-2", null)]),
            HttpStatusCode.NoContent);

        Assert.Equal("CDBs Casa|1000", await EscalarAsync<string>(c.Banco,
            "SELECT string_agg(apelido || '|' || saldo_inicial_centavos, ';') FROM contas.contas WHERE tipo = 'INVESTIMENTO'"));
        Assert.True(await EscalarAsync<bool>(c.Banco, "SELECT conta_id IS NULL FROM integracoes.vinculos WHERE id_externo = 'cdb-sintetico-2'"));
        Assert.Equal(["cdb-sintetico-2"], (await DescobertaAsync(c.Ana, item)).GetProperty("aplicacoes").EnumerateArray()
            .Select(x => x.GetProperty("idExterno").GetString()));
    }

    /// <summary>Segura as duas confirmações depois do IdEmUso, para as duas trocarem o id ao mesmo tempo.</summary>
    private sealed class Barreira(IContasLigadas real, Guid idDaBarreira, int[] chegaram, TaskCompletionSource juntas) : IContasLigadas
    {
        public async Task<ResultadoDaContaLigada> CriarAsync(NovaContaLigada pedido, CancellationToken ct)
        {
            var r = await real.CriarAsync(pedido, ct);
            if (pedido.ContaId != idDaBarreira) return r;
            if (Interlocked.Increment(ref chegaram[0]) == 2) juntas.TrySetResult();
            await juntas.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            return r;
        }

        public Task<bool> MarcarLigadaAsync(Guid contaId, CancellationToken ct) => real.MarcarLigadaAsync(contaId, ct);

        public Task DefinirSaldoInicialAsync(Guid contaId, long centavos, DateOnly em, CancellationToken ct) =>
            real.DefinirSaldoInicialAsync(contaId, centavos, em, ct);
    }

    [Fact]
    public async Task Duas_confirmacoes_juntas_sobre_conta_apagada_nao_criam_conta_orfa()
    {
        var apagada = new Guid[1];
        var chegaram = new int[1];
        var juntas = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var c = await IniciarAsync(s => s.Replace(ServiceDescriptor.Scoped<IContasLigadas>(sp =>
            new Barreira(ActivatorUtilities.CreateInstance<ContasLigadas>(sp), apagada[0], chegaram, juntas))));
        apagada[0] = await CorrenteManualAsync(c, "Vai ser apagada");
        await EsperarAsync(c.Ana.DeleteAsync($"/api/v1/contas/{apagada[0]}"), HttpStatusCode.NoContent);
        var item = await ItemComContasAsync(c);
        await VincularAsync(c, item, "CONTA", Corrente, contaId: apagada[0]);

        var respostas = await Task.WhenAll(
            NascerAsync(c.Ana, item, [Criar(Corrente.Id, "Itaú Corrente · Ana", c.AnaId)]),
            NascerAsync(c.Ana, item, [Criar(Corrente.Id, "Itaú Corrente · Ana", c.AnaId)]));

        foreach (var r in respostas)
            Assert.True(r.StatusCode == HttpStatusCode.NoContent, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        Assert.Equal(1L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM contas.contas WHERE NOT is_deleted"));
        Assert.True(await EscalarAsync<bool>(c.Banco,
            $"SELECT k.ligada_em IS NOT NULL FROM integracoes.vinculos v JOIN contas.contas k ON k.id = v.conta_id AND NOT k.is_deleted " +
            $"WHERE v.id_externo = '{Corrente.Id}'"));
    }
}
