using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GestorFinanceiro.Api.Infra;
using GestorFinanceiro.IntegrationTests.Infra;
using GestorFinanceiro.Modules.Contas.Contracts;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Contracts;
using GestorFinanceiro.SharedKernel.Aplicacao;
using GestorFinanceiro.UnitTests;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GestorFinanceiro.IntegrationTests;

/// <summary>
/// Fase 2, etapa 5 (Tarefas 5.1 e 5.2): regras por descrição, mapa da categoria do banco e revisão em lote.
/// - A sugestão só preenche a categoria de linha "a revisar" e nunca mexe no que o casal revisou.
/// - O mapa só muda pelo botão "sempre que o banco disser" (decisão 2).
/// - A revisão em lote é atômica.
/// Dados sintéticos; relógio em 05/11/2026.
/// </summary>
[Collection("postgres")]
public class RevisaoTests(PostgresFixture pg)
{
    private static readonly DateOnly Desde = new(2026, 1, 1);
    private const string CodigoPadaria = "01010000";
    private const string CodigoMercado = "01020000";

    private sealed class CasalFixo { public Guid? Id { get; set; } }

    private sealed class UsuarioDoTeste(IHttpContextAccessor acessor, CasalFixo fixo) : IUsuarioAtual
    {
        private IUsuarioAtual Atual => acessor.HttpContext is null ? new UsuarioSistema(fixo.Id) : new UsuarioAtualHttp(acessor);
        public Guid? UsuarioId => Atual.UsuarioId;
        public Guid? CasalId => Atual.CasalId;
        public string? Ip => Atual.Ip;
    }

    private sealed record Ctx(ApiDeTeste Api, BancoDeTeste Banco, HttpClient Cliente, Guid Corrente) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Api.DisposeAsync();
    }

    private async Task<Ctx> IniciarAsync()
    {
        var fixo = new CasalFixo();
        var banco = await pg.NovoBancoAsync();
        var api = new ApiDeTeste(banco, relogio: new RelogioFixo(), servicos: s =>
        {
            s.AddSingleton(fixo);
            s.AddScoped<IUsuarioAtual, UsuarioDoTeste>();
        });
        var pedido = new CadastroDoCasalDto(
            api.CodigoInstalacao, "Família Teste",
            new ConjugeNovoDto("Ana Teste", CpfSintetico.Gerar(1), "senha-da-ana-123"),
            new ConjugeNovoDto("Bruno Teste", CpfSintetico.Gerar(2), "senha-do-bruno-1"));
        Assert.Equal(HttpStatusCode.Created, (await api.CreateClient().PostAsJsonAsync("/api/v1/casal", pedido)).StatusCode);
        var ana = await EscalarAsync<Guid>(banco, "SELECT id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'");
        fixo.Id = await EscalarAsync<Guid>(banco, "SELECT casal_id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'");

        var corrente = Guid.CreateVersion7();
        await using (var escopo = api.Services.CreateAsyncScope())
            Assert.IsType<ResultadoDaContaLigada.Pronta>(await escopo.ServiceProvider.GetRequiredService<IContasLigadas>().CriarAsync(
                new NovaContaLigada(corrente, "CORRENTE", "Itaú Corrente · Ana", "Itaú", ana, 0, Desde, null, null, null), CancellationToken.None));
        return new Ctx(api, banco, await api.ClienteLogadoAsync(ana), corrente);
    }

    private static async Task<T> EscalarAsync<T>(BancoDeTeste banco, string sql)
    {
        await using var db = new NpgsqlConnection(banco.ConexaoSuperusuario);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, db);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task ImportarAsync(Ctx c, params LancamentoImportado[] lote)
    {
        await using var escopo = c.Api.Services.CreateAsyncScope();
        await escopo.ServiceProvider.GetRequiredService<IImportacaoDeLancamentos>()
            .ImportarAsync(c.Corrente, Desde, lote, apagarAusentes: false, CancellationToken.None);
    }

    private static LancamentoImportado Linha(string id, long valor, string descricao, string? codigo = CodigoPadaria, string? nome = "Padaria") =>
        new(id, new DateOnly(2026, 11, 3), valor, descricao, false, null, null, codigo, null, nome);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<Guid> CategoriaAsync(HttpClient cliente, string nome)
    {
        var lista = await JsonAsync(await cliente.GetAsync("/api/v1/categorias"));
        return lista.EnumerateArray().Single(c => c.GetProperty("nome").GetString() == nome).GetProperty("id").GetGuid();
    }

    private static Task<Guid> IdAsync(Ctx c, string idExterno) =>
        EscalarAsync<Guid>(c.Banco, $"SELECT id FROM lancamentos.transacoes WHERE id_externo = '{idExterno}'");

    /// <summary>"categoria (nome ou -)|revisao" da linha do banco.</summary>
    private static Task<string> EstadoAsync(Ctx c, string idExterno) => EscalarAsync<string>(c.Banco,
        "SELECT coalesce(k.nome, '-') || '|' || t.revisao FROM lancamentos.transacoes t " +
        $"LEFT JOIN lancamentos.categorias k ON k.id = t.categoria_id WHERE t.id_externo = '{idExterno}'");

    private static Task<HttpResponseMessage> RevisarAsync(Ctx c, IEnumerable<Guid> ids, Guid categoria, string aplicar) =>
        c.Cliente.PostAsJsonAsync("/api/v1/lancamentos/revisao", new { ids = ids.ToArray(), categoriaId = categoria, aplicar });

    private static async Task RevisarOkAsync(Ctx c, IEnumerable<Guid> ids, Guid categoria, string aplicar)
    {
        var r = await RevisarAsync(c, ids, categoria, aplicar);
        Assert.True(r.StatusCode == HttpStatusCode.NoContent, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
    }

    private static Task<long> MapaAsync(Ctx c) =>
        EscalarAsync<long>(c.Banco, "SELECT count(*) FROM lancamentos.mapa_categoria_externa WHERE NOT is_deleted");

    // ------------------------------------------------------------------ Tarefa 5.1: regras e mapa

    [Fact]
    public async Task Regra_de_descricao_vence_o_mapa()
    {
        await using var c = await IniciarAsync();
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        var restaurantes = await CategoriaAsync(c.Cliente, "Restaurantes");
        await ImportarAsync(c, Linha("tx-1", -1_000, "PADARIA DO ZE 123"), Linha("tx-2", -2_000, "CAFE DA ESQUINA"));
        await RevisarOkAsync(c, [await IdAsync(c, "tx-2")], mercado, "SEMPRE_DO_BANCO");
        await RevisarOkAsync(c, [await IdAsync(c, "tx-1")], restaurantes, "SEMPRE");

        await ImportarAsync(c, Linha("tx-3", -1_500, "PADARIA DO ZE 456"), Linha("tx-4", -900, "OUTRO CAFE"));

        Assert.Equal("Restaurantes|A_REVISAR", await EstadoAsync(c, "tx-3"));
        Assert.Equal("Mercado|A_REVISAR", await EstadoAsync(c, "tx-4"));
    }

    [Fact]
    public async Task Mapa_sugere_quando_nao_ha_regra()
    {
        await using var c = await IniciarAsync();
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        await ImportarAsync(c, Linha("tx-1", -1_000, "SUPERMERCADO A", CodigoMercado, "Supermercado"));
        await RevisarOkAsync(c, [await IdAsync(c, "tx-1")], mercado, "SEMPRE_DO_BANCO");

        await ImportarAsync(c, Linha("tx-2", -3_000, "SUPERMERCADO B", CodigoMercado, "Supermercado"));

        Assert.Equal("Mercado|A_REVISAR", await EstadoAsync(c, "tx-2"));
        // Sugerida, mas "a revisar": fora do orçamento até o casal revisar (D14). Só a revisada (R$ 10,00) conta.
        await using var escopo = c.Api.Services.CreateAsyncScope();
        var uso = await escopo.ServiceProvider.GetRequiredService<IConsultaDeUso>().UsoPorCategoriaAsync(new DateOnly(2026, 11, 1));
        Assert.Equal(1_000, uso.Where(u => u.CategoriaId == mercado).Sum(u => u.UsadoCentavos));
    }

    [Fact]
    public async Task Sem_regra_nem_mapa_fica_sem_categoria()
    {
        await using var c = await IniciarAsync();

        await ImportarAsync(c, Linha("tx-1", -1_000, "LOJA QUALQUER"));

        Assert.Equal("-|A_REVISAR", await EstadoAsync(c, "tx-1"));
    }

    [Fact]
    public async Task Descricao_da_regra_so_existe_cifrada()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, Linha("tx-1", -1_000, "FARMACIA ZULEICA SINTETICA"));
        await RevisarOkAsync(c, [await IdAsync(c, "tx-1")], await CategoriaAsync(c.Cliente, "Saúde"), "SEMPRE");

        Assert.Equal("descricao_hmac,descricao_normalizada_cifrada", await EscalarAsync<string>(c.Banco,
            "SELECT string_agg(column_name, ',' ORDER BY column_name) FROM information_schema.columns " +
            "WHERE table_schema = 'lancamentos' AND table_name = 'regras_de_categoria' AND column_name LIKE 'descricao%'"));
        var cifrada = await EscalarAsync<byte[]>(c.Banco, "SELECT descricao_normalizada_cifrada FROM lancamentos.regras_de_categoria");
        Assert.DoesNotContain("ZULEICA", Encoding.UTF8.GetString(cifrada), StringComparison.Ordinal);
        Assert.Equal(0L, await EscalarAsync<long>(c.Banco,
            "SELECT count(*) FROM audit.eventos WHERE tabela = 'lancamentos.regras_de_categoria' AND alteracoes::text LIKE '%ZULEICA%'"));
    }

    // ------------------------------------------------------------------ Tarefa 5.2: revisão em lote

    [Fact]
    public async Task A_revisar_agrupa_pela_descricao_normalizada()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, Linha("tx-1", -1_000, "PADARIA DO ZE 123"), Linha("tx-2", -2_500, "Padaria do Zé 456"),
            Linha("tx-3", 5_000, "PADARIA DO ZE 789"), Linha("tx-4", -700, "BANCA"));

        var grupos = (await JsonAsync(await c.Cliente.GetAsync("/api/v1/lancamentos/a-revisar"))).EnumerateArray().ToList();

        var padaria = grupos.Single(g => g.GetProperty("natureza").GetString() == "DESPESA" && g.GetProperty("quantidade").GetInt32() == 2);
        Assert.Equal(-3_500, padaria.GetProperty("totalCentavos").GetInt64());
        Assert.Equal(2, padaria.GetProperty("ids").GetArrayLength());
        Assert.Equal(CodigoPadaria, padaria.GetProperty("categoriaDoBanco").GetString());
        Assert.Equal("Padaria", padaria.GetProperty("nomeDaCategoriaDoBanco").GetString());
        // Mesma descrição com sinal oposto é outro grupo (outra natureza, outra categoria).
        Assert.Equal(3, grupos.Count);
    }

    [Fact]
    public async Task Aceitar_em_massa_marca_revisada()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, Linha("tx-1", -1_000, "PADARIA 1"), Linha("tx-2", -2_000, "PADARIA 2"));
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");

        await RevisarOkAsync(c, [await IdAsync(c, "tx-1"), await IdAsync(c, "tx-2")], mercado, "SO_ESTA");

        Assert.Equal("Mercado|REVISADA", await EstadoAsync(c, "tx-1"));
        Assert.Equal("Mercado|REVISADA", await EstadoAsync(c, "tx-2"));
        Assert.Empty((await JsonAsync(await c.Cliente.GetAsync("/api/v1/lancamentos/a-revisar"))).EnumerateArray());
    }

    [Fact]
    public async Task Sempre_cria_regra_e_a_proxima_importacao_sugere()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, Linha("tx-1", -1_000, "UBER TRIP 1234", null, null));
        var transporte = await CategoriaAsync(c.Cliente, "Transporte");

        await RevisarOkAsync(c, [await IdAsync(c, "tx-1")], transporte, "SEMPRE");
        await ImportarAsync(c, Linha("tx-2", -2_000, "UBER TRIP 9876", null, null));

        Assert.Equal(1L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM lancamentos.regras_de_categoria"));
        Assert.Equal("Transporte|A_REVISAR", await EstadoAsync(c, "tx-2"));
    }

    [Fact]
    public async Task So_esta_nao_cria_regra()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, Linha("tx-1", -1_000, "UBER TRIP 1234"));

        await RevisarOkAsync(c, [await IdAsync(c, "tx-1")], await CategoriaAsync(c.Cliente, "Transporte"), "SO_ESTA");
        await ImportarAsync(c, Linha("tx-2", -2_000, "UBER TRIP 9876"));

        Assert.Equal(0L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM lancamentos.regras_de_categoria"));
        Assert.Equal("-|A_REVISAR", await EstadoAsync(c, "tx-2"));
    }

    [Fact]
    public async Task Sempre_que_o_banco_disser_grava_o_mapa()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, Linha("tx-1", -1_000, "PADARIA 1"));
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");

        await RevisarOkAsync(c, [await IdAsync(c, "tx-1")], mercado, "SEMPRE_DO_BANCO");

        var mapa = (await JsonAsync(await c.Cliente.GetAsync("/api/v1/lancamentos/categorias-externas"))).EnumerateArray().Single();
        Assert.Equal(CodigoPadaria, mapa.GetProperty("categoriaExterna").GetString());
        Assert.Equal("Padaria", mapa.GetProperty("nome").GetString());
        Assert.Equal(mercado, mapa.GetProperty("categoriaId").GetGuid());
        Assert.Equal(0L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM lancamentos.regras_de_categoria"));
    }

    [Fact]
    public async Task Mapa_nunca_muda_sem_o_botao()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, [.. Enumerable.Range(1, 5).Select(i => Linha($"tx-{i}", -1_000 * i, $"LOJA {(char)('A' + i)}"))]);
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");

        await RevisarOkAsync(c, [await IdAsync(c, "tx-1"), await IdAsync(c, "tx-2")], mercado, "SO_ESTA");
        foreach (var i in new[] { 3, 4, 5 }) await RevisarOkAsync(c, [await IdAsync(c, $"tx-{i}")], mercado, "SEMPRE");

        Assert.Equal(0L, await MapaAsync(c));
    }

    [Fact]
    public async Task Sempre_que_o_banco_disser_sem_categoria_do_banco_e_recusado()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, Linha("tx-1", -1_000, "PADARIA 1", null, null));

        var r = await RevisarAsync(c, [await IdAsync(c, "tx-1")], await CategoriaAsync(c.Cliente, "Mercado"), "SEMPRE_DO_BANCO");

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("-|A_REVISAR", await EstadoAsync(c, "tx-1"));
        Assert.Equal(0L, await MapaAsync(c));
    }

    [Fact]
    public async Task Revisada_passa_a_contar_no_orcamento()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, Linha("tx-1", -1_234, "PADARIA 1"));
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");

        await RevisarOkAsync(c, [await IdAsync(c, "tx-1")], mercado, "SO_ESTA");

        await using var escopo = c.Api.Services.CreateAsyncScope();
        var uso = await escopo.ServiceProvider.GetRequiredService<IConsultaDeUso>().UsoPorCategoriaAsync(new DateOnly(2026, 11, 1));
        Assert.Equal(1_234, uso.Single(u => u.CategoriaId == mercado).UsadoCentavos);
    }

    [Fact]
    public async Task Criar_categoria_na_revisao()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, Linha("tx-1", -1_000, "PET SHOP"));
        var r = await c.Cliente.PostAsJsonAsync("/api/v1/categorias", new { nome = "Pet", tipo = "DESPESA" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);

        await RevisarOkAsync(c, [await IdAsync(c, "tx-1")], await CategoriaAsync(c.Cliente, "Pet"), "SO_ESTA");

        Assert.Equal("Pet|REVISADA", await EstadoAsync(c, "tx-1"));
    }

    /// <summary>Atômica: um lançamento já revisado no lote recusa o lote inteiro (e não troca a categoria do casal).</summary>
    [Fact]
    public async Task Lote_com_lancamento_ja_revisado_nao_revisa_nenhum()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, Linha("tx-1", -1_000, "PADARIA 1"), Linha("tx-2", -2_000, "PADARIA 2"));
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        await RevisarOkAsync(c, [await IdAsync(c, "tx-2")], mercado, "SO_ESTA");

        var r = await RevisarAsync(c, [await IdAsync(c, "tx-1"), await IdAsync(c, "tx-2")], await CategoriaAsync(c.Cliente, "Restaurantes"), "SEMPRE");

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("-|A_REVISAR", await EstadoAsync(c, "tx-1"));
        Assert.Equal("Mercado|REVISADA", await EstadoAsync(c, "tx-2"));
        Assert.Equal(0L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM lancamentos.regras_de_categoria"));
    }

    /// <summary>Atômica: um id que não é do casal (ou não existe) recusa o lote inteiro.</summary>
    [Fact]
    public async Task Lote_com_id_desconhecido_nao_revisa_nenhum()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, Linha("tx-1", -1_000, "PADARIA 1"));

        var r = await RevisarAsync(c, [await IdAsync(c, "tx-1"), Guid.NewGuid()], await CategoriaAsync(c.Cliente, "Mercado"), "SO_ESTA");

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("-|A_REVISAR", await EstadoAsync(c, "tx-1"));
    }

    [Fact]
    public async Task Categoria_de_tipo_errado_ou_naturezas_misturadas_sao_recusadas()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, Linha("tx-1", -1_000, "PADARIA 1"), Linha("tx-2", 2_000, "PIX RECEBIDO"));
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        var salario = await CategoriaAsync(c.Cliente, "Salário");

        Assert.Equal(HttpStatusCode.BadRequest, (await RevisarAsync(c, [await IdAsync(c, "tx-1")], salario, "SO_ESTA")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await RevisarAsync(c, [await IdAsync(c, "tx-1"), await IdAsync(c, "tx-2")], mercado, "SO_ESTA")).StatusCode);
        Assert.Equal("-|A_REVISAR", await EstadoAsync(c, "tx-1"));
        Assert.Equal("-|A_REVISAR", await EstadoAsync(c, "tx-2"));
    }

    /// <summary>A regra só sugere em linha "a revisar": a linha que o casal revisou continua com a categoria dele.</summary>
    [Fact]
    public async Task Regra_nunca_mexe_no_que_o_casal_revisou()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, Linha("tx-1", -1_000, "PADARIA 1"), Linha("tx-2", -2_000, "PADARIA 2"));
        await RevisarOkAsync(c, [await IdAsync(c, "tx-1")], await CategoriaAsync(c.Cliente, "Mercado"), "SO_ESTA");
        await RevisarOkAsync(c, [await IdAsync(c, "tx-2")], await CategoriaAsync(c.Cliente, "Restaurantes"), "SEMPRE");
        // Já revisada: o lote é recusado, e o mapa fica como estava.
        Assert.Equal(HttpStatusCode.Conflict,
            (await RevisarAsync(c, [await IdAsync(c, "tx-2")], await CategoriaAsync(c.Cliente, "Mercado"), "SEMPRE_DO_BANCO")).StatusCode);
        Assert.Equal(0L, await MapaAsync(c));

        // O banco manda as duas de novo: a revisada pelo casal não muda.
        await ImportarAsync(c, Linha("tx-1", -1_000, "PADARIA 1"), Linha("tx-2", -2_000, "PADARIA 2"));

        Assert.Equal("Mercado|REVISADA", await EstadoAsync(c, "tx-1"));
        Assert.Equal("Restaurantes|REVISADA", await EstadoAsync(c, "tx-2"));
    }

    [Fact]
    public async Task Mapa_e_editavel_na_tela()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, Linha("tx-1", -1_000, "PADARIA 1"));
        await RevisarOkAsync(c, [await IdAsync(c, "tx-1")], await CategoriaAsync(c.Cliente, "Mercado"), "SEMPRE_DO_BANCO");
        var id = (await JsonAsync(await c.Cliente.GetAsync("/api/v1/lancamentos/categorias-externas"))).EnumerateArray().Single()
            .GetProperty("id").GetGuid();
        var restaurantes = await CategoriaAsync(c.Cliente, "Restaurantes");

        var r = await c.Cliente.PutAsJsonAsync($"/api/v1/lancamentos/categorias-externas/{id}", new { categoriaId = restaurantes });
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        await ImportarAsync(c, Linha("tx-2", -2_000, "PADARIA 2"));

        Assert.Equal("Restaurantes|A_REVISAR", await EstadoAsync(c, "tx-2"));
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.Cliente.PutAsJsonAsync($"/api/v1/lancamentos/categorias-externas/{id}", new { categoriaId = Guid.NewGuid() })).StatusCode);
    }

    // ------------------------------------------------------------------ Revisão da E5, rodada 1

    /// <summary>
    /// O botão grava o código que a tela mostrou (o mais comum do grupo), não o do primeiro id; código que não está no lote é
    /// recusado.
    /// </summary>
    [Fact]
    public async Task Sempre_que_o_banco_disser_grava_o_codigo_que_a_tela_mostrou()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, Linha("tx-1", -1_000, "PADARIA 1", CodigoPadaria, "Padaria"), Linha("tx-2", -2_000, "PADARIA 2", CodigoMercado, "Supermercado"),
            Linha("tx-3", -3_000, "PADARIA 3", CodigoMercado, "Supermercado"));
        var ids = new[] { await IdAsync(c, "tx-1"), await IdAsync(c, "tx-2"), await IdAsync(c, "tx-3") };
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");

        var foraDoLote = await c.Cliente.PostAsJsonAsync("/api/v1/lancamentos/revisao",
            new { ids, categoriaId = mercado, aplicar = "SEMPRE_DO_BANCO", categoriaDoBanco = "09090909" });
        Assert.Equal(HttpStatusCode.BadRequest, foraDoLote.StatusCode);
        Assert.Equal(0L, await MapaAsync(c));

        var r = await c.Cliente.PostAsJsonAsync("/api/v1/lancamentos/revisao",
            new { ids, categoriaId = mercado, aplicar = "SEMPRE_DO_BANCO", categoriaDoBanco = CodigoMercado });
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal($"{CodigoMercado}|Supermercado", await EscalarAsync<string>(c.Banco,
            "SELECT categoria_externa || '|' || nome_externo FROM lancamentos.mapa_categoria_externa"));
    }

    /// <summary>Sem o código no pedido, vale o mais comum do lote (o mesmo que a tela mostra).</summary>
    [Fact]
    public async Task Sem_o_codigo_no_pedido_vale_o_mais_comum_do_lote()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, Linha("tx-1", -1_000, "PADARIA 1", null, null), Linha("tx-2", -2_000, "PADARIA 2", CodigoMercado, "Supermercado"));

        await RevisarOkAsync(c, [await IdAsync(c, "tx-1"), await IdAsync(c, "tx-2")], await CategoriaAsync(c.Cliente, "Mercado"), "SEMPRE_DO_BANCO");

        Assert.Equal(CodigoMercado, await EscalarAsync<string>(c.Banco, "SELECT categoria_externa FROM lancamentos.mapa_categoria_externa"));
    }

    /// <summary>
    /// Nunca mexe no que o casal revisou: a pendente revisada que vira definitiva nova leva a categoria do casal, mesmo com
    /// uma regra que sugeriria outra.
    /// </summary>
    [Fact]
    public async Task Pendente_revisada_promovida_fica_com_a_categoria_do_casal_e_nao_com_a_sugestao()
    {
        await using var c = await IniciarAsync();
        var restaurantes = await CategoriaAsync(c.Cliente, "Restaurantes");
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        await ImportarAsync(c, Linha("tx-regra", -500, "PADARIA DO ZE"));
        await RevisarOkAsync(c, [await IdAsync(c, "tx-regra")], restaurantes, "SEMPRE");
        await ImportarAsync(c, new LancamentoImportado("tx-pendente", new DateOnly(2026, 11, 3), -2_550, "PADARIA DO ZE", true, null, null, null, null));
        Assert.Equal("Restaurantes|A_REVISAR", await EstadoAsync(c, "tx-pendente"));
        await RevisarOkAsync(c, [await IdAsync(c, "tx-pendente")], mercado, "SO_ESTA");

        // O banco troca a pendente pela definitiva (outro id), sem mandar a pendente.
        await using (var escopo = c.Api.Services.CreateAsyncScope())
            await escopo.ServiceProvider.GetRequiredService<IImportacaoDeLancamentos>().ImportarAsync(c.Corrente, Desde,
                [Linha("tx-regra", -500, "PADARIA DO ZE"), new LancamentoImportado("tx-definitiva", new DateOnly(2026, 11, 4), -2_550, "PADARIA DO ZE", false, null, null, null, null)],
                apagarAusentes: true, CancellationToken.None);

        Assert.Equal("Mercado|REVISADA", await EstadoAsync(c, "tx-definitiva"));
    }

    /// <summary>Grupo maior que o lote máximo vem em partes de até 500, cada uma revisável.</summary>
    [Fact]
    public async Task Grupo_grande_vem_em_lotes_de_ate_500()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, [.. Enumerable.Range(1, 501).Select(i => Linha($"tx-{i}", -100 - i, $"PADARIA {i}"))]);

        var grupos = (await JsonAsync(await c.Cliente.GetAsync("/api/v1/lancamentos/a-revisar"))).EnumerateArray()
            .Select(g => g.GetProperty("ids").GetArrayLength()).Order().ToList();

        Assert.Equal([1, 500], grupos);
    }

    /// <summary>Isolamento entre casais nas rotas da revisão e na sugestão (filtros Casal e SoftDelete).</summary>
    [Fact]
    public async Task Revisao_de_outro_casal_nao_aparece_nem_e_alterada_nem_sugere()
    {
        await using var c = await IniciarAsync();
        var outro = Guid.NewGuid();
        var categoriaAlheia = Guid.NewGuid();
        var linhaAlheia = Guid.NewGuid();
        var mapaAlheio = Guid.NewGuid();
        await EscalarAsync<int>(c.Banco, $"INSERT INTO identidade.casais (id, nome_familia) VALUES ('{outro}', 'Outra Família') RETURNING 1");
        await EscalarAsync<int>(c.Banco,
            $"INSERT INTO lancamentos.categorias (id, casal_id, nome, tipo) VALUES ('{categoriaAlheia}', '{outro}', 'Alheia', 'DESPESA') RETURNING 1");
        await EscalarAsync<int>(c.Banco,
            "INSERT INTO lancamentos.transacoes (id, casal_id, conta_id, data, valor_centavos, natureza, descricao_cifrada, origem, id_externo, revisao, categoria_externa) " +
            $"VALUES ('{linhaAlheia}', '{outro}', '{Guid.NewGuid()}', '2026-11-03', -777, 'DESPESA', '\\x01', 'OPEN_FINANCE', 'tx-alheia', 'A_REVISAR', '{CodigoPadaria}') RETURNING 1");
        await EscalarAsync<int>(c.Banco,
            "INSERT INTO lancamentos.mapa_categoria_externa (id, casal_id, categoria_externa, categoria_id) " +
            $"VALUES ('{mapaAlheio}', '{outro}', '{CodigoPadaria}', '{categoriaAlheia}') RETURNING 1");
        await ImportarAsync(c, Linha("tx-1", -1_000, "PADARIA 1"));

        // A sugestão não usa o mapa do outro casal.
        Assert.Equal("-|A_REVISAR", await EstadoAsync(c, "tx-1"));
        var grupos = (await JsonAsync(await c.Cliente.GetAsync("/api/v1/lancamentos/a-revisar"))).EnumerateArray().ToList();
        Assert.Equal([await IdAsync(c, "tx-1")], grupos.SelectMany(g => g.GetProperty("ids").EnumerateArray().Select(x => x.GetGuid())).ToList());
        Assert.Empty((await JsonAsync(await c.Cliente.GetAsync("/api/v1/lancamentos/categorias-externas"))).EnumerateArray());

        Assert.Equal(HttpStatusCode.NotFound, (await RevisarAsync(c, [linhaAlheia], await CategoriaAsync(c.Cliente, "Mercado"), "SO_ESTA")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Cliente.PutAsJsonAsync($"/api/v1/lancamentos/categorias-externas/{mapaAlheio}",
            new { categoriaId = await CategoriaAsync(c.Cliente, "Mercado") })).StatusCode);
        Assert.Equal($"A_REVISAR|{categoriaAlheia}", await EscalarAsync<string>(c.Banco,
            $"SELECT t.revisao || '|' || m.categoria_id FROM lancamentos.transacoes t, lancamentos.mapa_categoria_externa m WHERE t.id = '{linhaAlheia}' AND m.id = '{mapaAlheio}'"));
    }

    // ------------------------------------------------------------------ Ordem D23 do CEO

    private const string ReconhecidosNaE6 =
        "Pagamento de fatura e transferência entre as contas de vocês são reconhecidos na próxima etapa; não entram no orçamento.";

    private static readonly string[] CodigosDaE6 = ["05100000", "04000000", "03000000", "03020000", "03060000"];

    private static async Task<string> TituloAsync(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("title").GetString()!;

    /// <summary>
    /// D23: pagamento de fatura, transferência para a mesma pessoa e investimento ficam fora da revisão em lote e da faixa até
    /// a E6; continuam "a revisar" e fora do orçamento.
    /// </summary>
    [Fact]
    public async Task Pagamento_de_fatura_e_transferencia_nao_aparecem_na_revisao()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, [Linha("tx-normal", -1_000, "PADARIA 1"),
            .. CodigosDaE6.Select((codigo, i) => Linha($"tx-e6-{i}", -10_000 - i, $"MOVIMENTO {(char)('A' + i)}", codigo, "Banco"))]);

        var grupos = (await JsonAsync(await c.Cliente.GetAsync("/api/v1/lancamentos/a-revisar"))).EnumerateArray().ToList();

        Assert.Equal([await IdAsync(c, "tx-normal")], grupos.SelectMany(g => g.GetProperty("ids").EnumerateArray().Select(x => x.GetGuid())).ToList());
        Assert.Equal(5L, await EscalarAsync<long>(c.Banco,
            "SELECT count(*) FROM lancamentos.transacoes WHERE id_externo LIKE 'tx-e6-%' AND revisao = 'A_REVISAR' AND categoria_id IS NULL"));
    }

    /// <summary>D23: mesmo pela API, o lote com um desses lançamentos é recusado inteiro, com a frase.</summary>
    [Fact]
    public async Task Revisao_em_lote_recusa_pagamento_de_fatura_e_transferencia()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, Linha("tx-normal", -1_000, "PADARIA 1"), Linha("tx-fatura", -50_000, "PAGAMENTO FATURA", "05100000", "Credit card payment"));

        var r = await RevisarAsync(c, [await IdAsync(c, "tx-normal"), await IdAsync(c, "tx-fatura")], await CategoriaAsync(c.Cliente, "Mercado"), "SO_ESTA");

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(ReconhecidosNaE6, await TituloAsync(r));
        Assert.Equal("-|A_REVISAR", await EstadoAsync(c, "tx-normal"));
        Assert.Equal("-|A_REVISAR", await EstadoAsync(c, "tx-fatura"));
    }

    /// <summary>D23: a edição individual recusa categoria de gasto para esses lançamentos (400, com a frase).</summary>
    [Fact]
    public async Task Edicao_individual_recusa_categoria_de_gasto_para_pagamento_de_fatura_e_transferencia()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, Linha("tx-mesma-pessoa", -20_000, "TED MESMA TITULARIDADE", "04000000", "Same person transfer"));
        var id = await IdAsync(c, "tx-mesma-pessoa");

        var r = await c.Cliente.PutAsJsonAsync($"/api/v1/lancamentos/{id}", new
        {
            natureza = "DESPESA", valorCentavos = 20_000L, data = "2026-11-03", descricao = "TED", categoriaId = await CategoriaAsync(c.Cliente, "Mercado"),
        });

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(ReconhecidosNaE6, await TituloAsync(r));
        Assert.Equal("-|A_REVISAR", await EstadoAsync(c, "tx-mesma-pessoa"));
    }

    /// <summary>D23, item 1: o mapa recusa esses códigos, nem pelo botão, nem pela edição, nem como sugestão.</summary>
    [Fact]
    public async Task Mapa_recusa_os_codigos_reconhecidos_na_E6()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, Linha("tx-1", -1_000, "PADARIA 1"));
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");

        var botao = await c.Cliente.PostAsJsonAsync("/api/v1/lancamentos/revisao",
            new { ids = new[] { await IdAsync(c, "tx-1") }, categoriaId = mercado, aplicar = "SEMPRE_DO_BANCO", categoriaDoBanco = "05100000" });
        Assert.Equal(HttpStatusCode.BadRequest, botao.StatusCode);
        Assert.Equal(ReconhecidosNaE6, await TituloAsync(botao));
        Assert.Equal(0L, await MapaAsync(c));

        // Entrada antiga (gravada direto): a edição é recusada e ela não sugere nada.
        var casal = await EscalarAsync<Guid>(c.Banco, "SELECT casal_id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'");
        var entrada = Guid.NewGuid();
        await EscalarAsync<int>(c.Banco, "INSERT INTO lancamentos.mapa_categoria_externa (id, casal_id, categoria_externa, categoria_id) " +
                                         $"VALUES ('{entrada}', '{casal}', '03000000', '{mercado}') RETURNING 1");
        var put = await c.Cliente.PutAsJsonAsync($"/api/v1/lancamentos/categorias-externas/{entrada}", new { categoriaId = mercado });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Equal(ReconhecidosNaE6, await TituloAsync(put));
        await ImportarAsync(c, Linha("tx-aplicacao", -5_000, "APLICACAO CDB", "03000000", "Investments"));
        Assert.Equal("-|A_REVISAR", await EstadoAsync(c, "tx-aplicacao"));
    }

    /// <summary>
    /// D23, revisão (Bloqueia 1): nem categoria de receita. A TED recebida da mesma pessoa editada como receita em "Salário"
    /// entraria nas receitas do orçamento; continua a revisar e fora dele.
    /// </summary>
    [Fact]
    public async Task Edicao_individual_recusa_qualquer_categoria_e_nada_entra_no_orcamento()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, Linha("tx-ted", 30_000, "TED RECEBIDA MESMA TITULARIDADE", "04000000", "Same person transfer"));
        var id = await IdAsync(c, "tx-ted");

        var r = await c.Cliente.PutAsJsonAsync($"/api/v1/lancamentos/{id}", new
        {
            natureza = "RECEITA", valorCentavos = 30_000L, data = "2026-11-03", descricao = "TED", categoriaId = await CategoriaAsync(c.Cliente, "Salário"),
        });

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(ReconhecidosNaE6, await TituloAsync(r));
        Assert.Equal("-|A_REVISAR", await EstadoAsync(c, "tx-ted"));
        await using var escopo = c.Api.Services.CreateAsyncScope();
        Assert.Equal(0, await escopo.ServiceProvider.GetRequiredService<IConsultaDeUso>().ReceitasAsync(new DateOnly(2026, 11, 1)));
    }

    /// <summary>
    /// D23, revisão (Deveria 2): a pendente que o casal revisou com categoria de gasto não passa a revisão para a definitiva
    /// que chega com código da E6 (pagamento de fatura): ela fica a revisar, sem categoria, fora do orçamento.
    /// </summary>
    [Fact]
    public async Task Definitiva_com_codigo_da_E6_nao_herda_a_revisao_da_pendente()
    {
        await using var c = await IniciarAsync();
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        await ImportarAsync(c, new LancamentoImportado("tx-pendente", new DateOnly(2026, 11, 3), -50_000, "PAGAMENTO FATURA", true, null, null, null, null));
        await RevisarOkAsync(c, [await IdAsync(c, "tx-pendente")], mercado, "SO_ESTA");

        await using (var escopo = c.Api.Services.CreateAsyncScope())
            await escopo.ServiceProvider.GetRequiredService<IImportacaoDeLancamentos>().ImportarAsync(c.Corrente, Desde,
                [new LancamentoImportado("tx-definitiva", new DateOnly(2026, 11, 4), -50_000, "PAGAMENTO FATURA", false, null, null, "05100000", null, "Credit card payment")],
                apagarAusentes: true, CancellationToken.None);

        Assert.Equal("-|A_REVISAR", await EstadoAsync(c, "tx-definitiva"));
        await using var leitura = c.Api.Services.CreateAsyncScope();
        var uso = await leitura.ServiceProvider.GetRequiredService<IConsultaDeUso>().UsoPorCategoriaAsync(new DateOnly(2026, 11, 1));
        Assert.Equal(0, uso.Where(u => u.CategoriaId == mercado).Sum(u => u.UsadoCentavos));
    }
}
