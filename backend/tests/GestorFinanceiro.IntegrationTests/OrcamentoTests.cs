using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GestorFinanceiro.IntegrationTests.Infra;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.UnitTests;
using Npgsql;

namespace GestorFinanceiro.IntegrationTests;

/// <summary>
/// Orçamento pela API: tetos, receita prevista, cópia, fatias, limites, categorias, concorrência e isolamento.
/// Relógio fixo em 05/11/2026 (hoje + 24 meses = 2028-11). Categorias vêm do GET /categorias.
/// </summary>
[Collection("postgres")]
public class OrcamentoTests(PostgresFixture pg)
{
    private const string Rota = "/api/v1/orcamento";
    private const string SqlAna = "SELECT id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'";
    private const string SqlCasal = "SELECT casal_id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'";

    private sealed record Contexto(ApiDeTeste Api, BancoDeTeste Banco, HttpClient Cliente, Guid Casal) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Api.DisposeAsync();
    }

    private static async Task<T> EscalarAsync<T>(BancoDeTeste banco, string sql)
    {
        await using var db = new NpgsqlConnection(banco.ConexaoSuperusuario);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, db);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task ExecutarAsync(BancoDeTeste banco, string sql)
    {
        await using var db = new NpgsqlConnection(banco.ConexaoSuperusuario);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, db);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<Contexto> IniciarAsync()
    {
        var banco = await pg.NovoBancoAsync();
        var api = new ApiDeTeste(banco, relogio: new RelogioFixo());
        var pedido = new CadastroDoCasalDto(
            api.CodigoInstalacao, "Família Teste",
            new ConjugeNovoDto("Ana Teste", CpfSintetico.Gerar(1), "senha-da-ana-123"),
            new ConjugeNovoDto("Bruno Teste", CpfSintetico.Gerar(2), "senha-do-bruno-1"));
        Assert.Equal(HttpStatusCode.Created, (await api.CreateClient().PostAsJsonAsync("/api/v1/casal", pedido)).StatusCode);
        var ana = await EscalarAsync<Guid>(banco, SqlAna);
        var casal = await EscalarAsync<Guid>(banco, SqlCasal);
        return new Contexto(api, banco, await api.ClienteLogadoAsync(ana), casal);
    }

    private static async Task<Guid> CategoriaAsync(HttpClient cliente, string nome)
    {
        var lista = await JsonAsync(await cliente.GetAsync("/api/v1/categorias"));
        return lista.EnumerateArray().Single(c => c.GetProperty("nome").GetString() == nome).GetProperty("id").GetGuid();
    }

    private static async Task ArquivarAsync(HttpClient cliente, Guid categoria, string nome)
    {
        var resposta = await cliente.PutAsJsonAsync($"/api/v1/categorias/{categoria}", new { nome, tipo = "DESPESA", arquivada = true });
        Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);
    }

    private static async Task<Guid> CriarContaAsync(HttpClient cliente, object corpo)
    {
        var resposta = await cliente.PostAsJsonAsync("/api/v1/contas", corpo);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return Guid.Parse(resposta.Headers.Location!.ToString().Split('/').Last());
    }

    private static Task<Guid> CriarContaSimplesAsync(HttpClient cliente, string tipo, string apelido) =>
        CriarContaAsync(cliente, new { tipo, apelido, saldoInicialCentavos = 0L, saldoInicialEm = "2026-01-01" });

    private static Task<Guid> CriarCartaoAsync(HttpClient cliente) =>
        CriarContaAsync(cliente, new
        {
            tipo = "CARTAO", apelido = "Nubank", saldoInicialCentavos = 0L, saldoInicialEm = "2026-01-01",
            diaFechamento = 5, diaVencimento = 12, limiteCentavos = 8_000_000L,
        });

    private static async Task LancarAsync(HttpClient cliente, Guid conta, Guid categoria, string natureza, long valor,
        string data, int parcelas = 1)
    {
        var resposta = await cliente.PostAsJsonAsync("/api/v1/lancamentos", new
        {
            chaveIdempotente = Guid.NewGuid(), contaId = conta, natureza, valorCentavos = valor, data,
            descricao = "Teste", contraparte = (string?)null, categoriaId = categoria, parcelas,
        });
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
    }

    private static async Task TransferirAsync(HttpClient cliente, Guid origem, Guid destino, long valor, string data)
    {
        var resposta = await cliente.PostAsJsonAsync("/api/v1/transferencias", new
        {
            chaveIdempotente = Guid.NewGuid(), contaOrigemId = origem, contaDestinoId = destino, valorCentavos = valor, data,
            descricao = "Transferência",
        });
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
    }

    private static Task<HttpResponseMessage> PutTetoAsync(HttpClient cliente, string mes, Guid categoria, long valor) =>
        cliente.PutAsJsonAsync($"{Rota}/tetos/{mes}/{categoria}", new { valorCentavos = valor });

    private static async Task DefinirTetoAsync(HttpClient cliente, string mes, Guid categoria, long valor) =>
        Assert.Equal(HttpStatusCode.NoContent, (await PutTetoAsync(cliente, mes, categoria, valor)).StatusCode);

    private static Task<HttpResponseMessage> PutReceitaAsync(HttpClient cliente, string mes, long valor) =>
        cliente.PutAsJsonAsync($"{Rota}/receita/{mes}", new { valorCentavos = valor });

    [Theory]
    [InlineData("teto")]
    [InlineData("receita")]
    public async Task Corpo_sem_valor_responde_400_e_nao_grava(string rota)
    {
        await using var c = await IniciarAsync();
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        var caminho = rota == "teto" ? $"{Rota}/tetos/2026-11/{mercado}" : $"{Rota}/receita/2026-11";

        var resposta = await c.Cliente.PutAsJsonAsync(caminho, new { });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal("Informe o valor.", (await JsonAsync(resposta)).GetProperty("errors").GetProperty("valorCentavos")[0].GetString());
        Assert.Equal(0L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM orcamento.tetos"));
        Assert.Equal(0L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM orcamento.receitas_previstas"));
    }

    private static async Task<JsonElement> ObterAsync(HttpClient cliente, string mes)
    {
        var resposta = await cliente.GetAsync($"{Rota}?mes={mes}");
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("no-store", resposta.Headers.CacheControl?.ToString());
        return await JsonAsync(resposta);
    }

    private static async Task<JsonElement> CopiarAsync(HttpClient cliente, string mes)
    {
        var resposta = await cliente.PostAsync($"{Rota}/{mes}/copia", null);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        return await JsonAsync(resposta);
    }

    private static JsonElement Linha(JsonElement orcamento, Guid categoria) =>
        orcamento.GetProperty("categorias").EnumerateArray().Single(c => c.GetProperty("categoriaId").GetGuid() == categoria);

    private static long? Teto(JsonElement orcamento, Guid categoria) =>
        Linha(orcamento, categoria).GetProperty("tetoCentavos") is { ValueKind: JsonValueKind.Number } t ? t.GetInt64() : null;

    private static Task<long> TetosAtivosAsync(Contexto c, string mes, Guid categoria) =>
        EscalarAsync<long>(c.Banco,
            $"SELECT count(*) FROM orcamento.tetos WHERE casal_id = '{c.Casal}' AND mes = '{mes}-01' AND categoria_id = '{categoria}' AND NOT is_deleted");

    private static async Task AssertErroNoCampoAsync(HttpResponseMessage resposta, string campo)
    {
        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.True((await JsonAsync(resposta)).GetProperty("errors").TryGetProperty(campo, out _), $"esperava errors.{campo}");
    }

    // ---------- CRUD ----------

    [Fact]
    public async Task Define_atualiza_e_remove_teto()
    {
        await using var c = await IniciarAsync();
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");

        await DefinirTetoAsync(c.Cliente, "2026-11", mercado, 160_000);
        Assert.Equal(160_000, Teto(await ObterAsync(c.Cliente, "2026-11"), mercado));

        await DefinirTetoAsync(c.Cliente, "2026-11", mercado, 175_000);
        Assert.Equal(175_000, Teto(await ObterAsync(c.Cliente, "2026-11"), mercado));
        Assert.Equal(1, await TetosAtivosAsync(c, "2026-11", mercado));

        Assert.Equal(HttpStatusCode.NoContent, (await c.Cliente.DeleteAsync($"{Rota}/tetos/2026-11/{mercado}")).StatusCode);
        Assert.Null(Teto(await ObterAsync(c.Cliente, "2026-11"), mercado));
        Assert.Equal(0, await TetosAtivosAsync(c, "2026-11", mercado));
        // Remover é soft delete: a mesma linha continua no banco, marcada.
        Assert.Equal(1L, await EscalarAsync<long>(c.Banco,
            $"SELECT count(*) FROM orcamento.tetos WHERE categoria_id = '{mercado}' AND is_deleted AND deleted_at IS NOT NULL"));

        Assert.Equal(HttpStatusCode.NotFound, (await c.Cliente.DeleteAsync($"{Rota}/tetos/2026-11/{mercado}")).StatusCode);
    }

    [Fact]
    public async Task Teto_zero_e_diferente_de_sem_teto()
    {
        await using var c = await IniciarAsync();
        var lazer = await CategoriaAsync(c.Cliente, "Lazer");
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");

        await DefinirTetoAsync(c.Cliente, "2026-11", lazer, 0);
        var orcamento = await ObterAsync(c.Cliente, "2026-11");

        Assert.Equal(0, Teto(orcamento, lazer));
        Assert.Equal(0, Linha(orcamento, lazer).GetProperty("restanteCentavos").GetInt64());
        Assert.Null(Teto(orcamento, mercado));
        Assert.Equal(JsonValueKind.Null, Linha(orcamento, mercado).GetProperty("restanteCentavos").ValueKind);
        Assert.Equal(0, orcamento.GetProperty("fatias").GetProperty("tetoDeGastos").GetProperty("tetoCentavos").GetInt64());
    }

    [Fact]
    public async Task Receita_prevista_e_sugestao()
    {
        await using var c = await IniciarAsync();
        var corrente = await CriarContaSimplesAsync(c.Cliente, "CORRENTE", "Itaú");
        await LancarAsync(c.Cliente, corrente, await CategoriaAsync(c.Cliente, "Salário"), "RECEITA", 900_000, "2026-10-05");

        var antes = await ObterAsync(c.Cliente, "2026-11");
        Assert.Equal(900_000, antes.GetProperty("receitaSugeridaCentavos").GetInt64());
        Assert.Equal(JsonValueKind.Null, antes.GetProperty("receitaPrevistaCentavos").ValueKind);

        Assert.Equal(HttpStatusCode.NoContent, (await PutReceitaAsync(c.Cliente, "2026-11", 1_850_000)).StatusCode);
        Assert.Equal(1_850_000, (await ObterAsync(c.Cliente, "2026-11")).GetProperty("receitaPrevistaCentavos").GetInt64());

        Assert.Equal(HttpStatusCode.NoContent, (await PutReceitaAsync(c.Cliente, "2026-11", 1_900_000)).StatusCode);
        Assert.Equal(1L, await EscalarAsync<long>(c.Banco,
            $"SELECT count(*) FROM orcamento.receitas_previstas WHERE casal_id = '{c.Casal}' AND mes = '2026-11-01' AND NOT is_deleted"));

        Assert.Equal(HttpStatusCode.NoContent, (await c.Cliente.DeleteAsync($"{Rota}/receita/2026-11")).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await ObterAsync(c.Cliente, "2026-11")).GetProperty("receitaPrevistaCentavos").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Cliente.DeleteAsync($"{Rota}/receita/2026-11")).StatusCode);
    }

    [Fact]
    public async Task Mes_passado_pode_ser_editado()
    {
        await using var c = await IniciarAsync();
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");

        Assert.Equal(HttpStatusCode.NoContent, (await PutTetoAsync(c.Cliente, "2025-03", mercado, 100_000)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PutReceitaAsync(c.Cliente, "2025-03", 500_000)).StatusCode);
        Assert.Equal(100_000, Teto(await ObterAsync(c.Cliente, "2025-03"), mercado));
    }

    // ---------- Cópia ----------

    [Fact]
    public async Task Copiar_preenche_so_categorias_sem_linha_e_respeita_teto_zero()
    {
        await using var c = await IniciarAsync();
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        var lazer = await CategoriaAsync(c.Cliente, "Lazer");
        await DefinirTetoAsync(c.Cliente, "2026-09", mercado, 160_000);
        await DefinirTetoAsync(c.Cliente, "2026-09", lazer, 50_000);
        await DefinirTetoAsync(c.Cliente, "2026-11", lazer, 0);
        Assert.Equal("2026-09", (await ObterAsync(c.Cliente, "2026-11")).GetProperty("mesParaCopiar").GetString());

        var copia = await CopiarAsync(c.Cliente, "2026-11");

        Assert.Equal(1, copia.GetProperty("copiados").GetInt32());
        Assert.Equal("2026-09", copia.GetProperty("origem").GetString());
        var novembro = await ObterAsync(c.Cliente, "2026-11");
        Assert.Equal(160_000, Teto(novembro, mercado));
        Assert.Equal(0, Teto(novembro, lazer));
    }

    [Fact]
    public async Task Copiar_usa_o_ultimo_mes_com_teto_mesmo_pulando_meses()
    {
        await using var c = await IniciarAsync();
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        await DefinirTetoAsync(c.Cliente, "2026-07", mercado, 90_000);
        await DefinirTetoAsync(c.Cliente, "2026-08", mercado, 120_000);
        await DefinirTetoAsync(c.Cliente, "2026-12", mercado, 130_000);

        Assert.Equal("2026-08", (await ObterAsync(c.Cliente, "2026-11")).GetProperty("mesParaCopiar").GetString());
        var copia = await CopiarAsync(c.Cliente, "2026-11");
        Assert.Equal("2026-08", copia.GetProperty("origem").GetString());
        Assert.Equal(120_000, Teto(await ObterAsync(c.Cliente, "2026-11"), mercado));
    }

    [Fact]
    public async Task Copiar_de_novo_nao_muda_nada()
    {
        await using var c = await IniciarAsync();
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        await DefinirTetoAsync(c.Cliente, "2026-10", mercado, 160_000);
        Assert.Equal(1, (await CopiarAsync(c.Cliente, "2026-11")).GetProperty("copiados").GetInt32());

        var deNovo = await CopiarAsync(c.Cliente, "2026-11");

        Assert.Equal(0, deNovo.GetProperty("copiados").GetInt32());
        Assert.Equal(1, await TetosAtivosAsync(c, "2026-11", mercado));
    }

    [Fact]
    public async Task Copiar_pula_categoria_arquivada_ou_apagada()
    {
        await using var c = await IniciarAsync();
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        var lazer = await CategoriaAsync(c.Cliente, "Lazer");
        var restaurantes = await CategoriaAsync(c.Cliente, "Restaurantes");
        await DefinirTetoAsync(c.Cliente, "2026-10", mercado, 160_000);
        await DefinirTetoAsync(c.Cliente, "2026-10", lazer, 50_000);
        await DefinirTetoAsync(c.Cliente, "2026-10", restaurantes, 40_000);
        await ArquivarAsync(c.Cliente, lazer, "Lazer");
        Assert.Equal(HttpStatusCode.NoContent, (await c.Cliente.DeleteAsync($"/api/v1/categorias/{restaurantes}")).StatusCode);

        var copia = await CopiarAsync(c.Cliente, "2026-11");

        Assert.Equal(1, copia.GetProperty("copiados").GetInt32());
        Assert.Equal(1, await TetosAtivosAsync(c, "2026-11", mercado));
        Assert.Equal(0, await TetosAtivosAsync(c, "2026-11", lazer));
        Assert.Equal(0, await TetosAtivosAsync(c, "2026-11", restaurantes));
    }

    [Fact]
    public async Task Copiar_sem_origem_responde_zero_e_origem_nula()
    {
        await using var c = await IniciarAsync();

        var copia = await CopiarAsync(c.Cliente, "2026-11");

        Assert.Equal(0, copia.GetProperty("copiados").GetInt32());
        Assert.Equal(JsonValueKind.Null, copia.GetProperty("origem").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await ObterAsync(c.Cliente, "2026-11")).GetProperty("mesParaCopiar").ValueKind);
    }

    // ---------- Fatias ----------

    [Fact]
    public async Task Aporte_em_Investimento_e_parcela_em_Dividas_nao_alteram_o_Teto_de_gastos()
    {
        await using var c = await IniciarAsync();
        var corrente = await CriarContaSimplesAsync(c.Cliente, "CORRENTE", "Itaú");
        var corretora = await CriarContaSimplesAsync(c.Cliente, "INVESTIMENTO", "Corretora");
        var cartao = await CriarCartaoAsync(c.Cliente);
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        var investimento = await CategoriaAsync(c.Cliente, "Investimento");
        var dividas = await CategoriaAsync(c.Cliente, "Dívidas e financiamentos");
        await LancarAsync(c.Cliente, corrente, mercado, "DESPESA", 20_000, "2026-11-03");
        var antes = (await ObterAsync(c.Cliente, "2026-11")).GetProperty("fatias").GetProperty("tetoDeGastos").GetProperty("usadoCentavos").GetInt64();

        await TransferirAsync(c.Cliente, corrente, corretora, 50_000, "2026-11-04");
        await LancarAsync(c.Cliente, corrente, investimento, "DESPESA", 10_000, "2026-11-04");
        // Ruling R5: parcelas só existem em cartão.
        await LancarAsync(c.Cliente, cartao, dividas, "DESPESA", 30_000, "2026-11-06", parcelas: 3);

        var fatias = (await ObterAsync(c.Cliente, "2026-11")).GetProperty("fatias");
        Assert.Equal(20_000, antes);
        Assert.Equal(antes, fatias.GetProperty("tetoDeGastos").GetProperty("usadoCentavos").GetInt64());
        Assert.Equal(60_000, fatias.GetProperty("investir").GetProperty("usadoCentavos").GetInt64());
        Assert.Equal(50_000, fatias.GetProperty("investir").GetProperty("aportesCentavos").GetInt64());
        Assert.Equal(10_000, fatias.GetProperty("dividas").GetProperty("usadoCentavos").GetInt64());
    }

    [Fact]
    public async Task Fatia_sem_categoria_de_papel_mostra_so_os_aportes()
    {
        await using var c = await IniciarAsync();
        var corrente = await CriarContaSimplesAsync(c.Cliente, "CORRENTE", "Itaú");
        var poupanca = await CriarContaSimplesAsync(c.Cliente, "POUPANCA", "Reserva");
        var investimento = await CategoriaAsync(c.Cliente, "Investimento");
        // A API já não apaga categoria com papel; o estado (categoria de papel removida) vem de um dado legado.
        await ExecutarAsync(c.Banco, $"UPDATE lancamentos.categorias SET is_deleted = true, deleted_at = now() WHERE id = '{investimento}'");
        await TransferirAsync(c.Cliente, corrente, poupanca, 35_000, "2026-11-04");

        var orcamento = await ObterAsync(c.Cliente, "2026-11");

        var investir = orcamento.GetProperty("fatias").GetProperty("investir");
        Assert.Equal(JsonValueKind.Null, investir.GetProperty("tetoCentavos").ValueKind);
        Assert.Equal(35_000, investir.GetProperty("usadoCentavos").GetInt64());
        Assert.Equal(35_000, investir.GetProperty("aportesCentavos").GetInt64());
        Assert.DoesNotContain(orcamento.GetProperty("categorias").EnumerateArray(), l => l.GetProperty("categoriaId").GetGuid() == investimento);
    }

    // ---------- Limites ----------

    [Fact]
    public async Task Limites_do_valor()
    {
        await using var c = await IniciarAsync();
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");

        foreach (var valor in new[] { 0L, 100_000_000_000L })
        {
            Assert.Equal(HttpStatusCode.NoContent, (await PutTetoAsync(c.Cliente, "2026-11", mercado, valor)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await PutReceitaAsync(c.Cliente, "2026-11", valor)).StatusCode);
        }
        foreach (var valor in new[] { -1L, 100_000_000_001L })
        {
            var teto = await PutTetoAsync(c.Cliente, "2026-11", mercado, valor);
            var corpo = await JsonAsync(teto);
            Assert.Equal(HttpStatusCode.BadRequest, teto.StatusCode);
            Assert.Equal("O valor deve ficar entre 0 e 100.000.000.000 centavos.",
                corpo.GetProperty("errors").GetProperty("valorCentavos")[0].GetString());
            await AssertErroNoCampoAsync(await PutReceitaAsync(c.Cliente, "2026-11", valor), "valorCentavos");
        }
    }

    [Fact]
    public async Task Limites_do_mes()
    {
        await using var c = await IniciarAsync();
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");

        foreach (var mes in new[] { "2020-01", "2028-11" })
        {
            await ObterAsync(c.Cliente, mes);
            Assert.Equal(HttpStatusCode.NoContent, (await PutTetoAsync(c.Cliente, mes, mercado, 1_000)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await PutReceitaAsync(c.Cliente, mes, 1_000)).StatusCode);
        }
        foreach (var mes in new[] { "2019-12", "2028-12", "2026-13", "abc" })
        {
            await AssertErroNoCampoAsync(await c.Cliente.GetAsync($"{Rota}?mes={mes}"), "mes");
            await AssertErroNoCampoAsync(await PutTetoAsync(c.Cliente, mes, mercado, 1_000), "mes");
            await AssertErroNoCampoAsync(await PutReceitaAsync(c.Cliente, mes, 1_000), "mes");
            await AssertErroNoCampoAsync(await c.Cliente.PostAsync($"{Rota}/{mes}/copia", null), "mes");
        }
        // Sem mes: o mês de hoje em Brasília.
        var padrao = await JsonAsync(await c.Cliente.GetAsync(Rota));
        Assert.Equal("2026-11", padrao.GetProperty("mes").GetString());
    }

    [Fact]
    public async Task Sugestao_de_2020_01_olha_2019_12_sem_erro()
    {
        await using var c = await IniciarAsync();
        var corrente = await CriarContaSimplesAsync(c.Cliente, "CORRENTE", "Itaú");
        await LancarAsync(c.Cliente, corrente, await CategoriaAsync(c.Cliente, "Salário"), "RECEITA", 700_000, "2019-12-05");

        var orcamento = await ObterAsync(c.Cliente, "2020-01");

        Assert.Equal(700_000, orcamento.GetProperty("receitaSugeridaCentavos").GetInt64());
    }

    // ---------- Categorias ----------

    [Fact]
    public async Task Categoria_de_outro_casal_responde_404()
    {
        await using var c = await IniciarAsync();
        var outroCasal = Guid.NewGuid();
        var alheia = Guid.NewGuid();
        await ExecutarAsync(c.Banco, $"INSERT INTO identidade.casais (id, nome_familia) VALUES ('{outroCasal}', 'Outra')");
        await ExecutarAsync(c.Banco,
            $"INSERT INTO lancamentos.categorias (id, casal_id, nome, tipo) VALUES ('{alheia}', '{outroCasal}', 'Mercado', 'DESPESA')");

        Assert.Equal(HttpStatusCode.NotFound, (await PutTetoAsync(c.Cliente, "2026-11", alheia, 1_000)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutTetoAsync(c.Cliente, "2026-11", Guid.NewGuid(), 1_000)).StatusCode);
        Assert.Equal(0L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM orcamento.tetos"));
    }

    [Fact]
    public async Task Categoria_de_receita_responde_400()
    {
        await using var c = await IniciarAsync();
        var salario = await CategoriaAsync(c.Cliente, "Salário");

        var resposta = await PutTetoAsync(c.Cliente, "2026-11", salario, 1_000);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal("Escolha uma categoria de despesa.",
            (await JsonAsync(resposta)).GetProperty("errors").GetProperty("categoriaId")[0].GetString());
    }

    [Fact]
    public async Task Arquivada_aceita_atualizar_e_remover_mas_nao_teto_novo()
    {
        await using var c = await IniciarAsync();
        var lazer = await CategoriaAsync(c.Cliente, "Lazer");
        await DefinirTetoAsync(c.Cliente, "2026-11", lazer, 50_000);
        await ArquivarAsync(c.Cliente, lazer, "Lazer");

        await DefinirTetoAsync(c.Cliente, "2026-11", lazer, 40_000);
        var orcamento = await ObterAsync(c.Cliente, "2026-11");
        Assert.Equal(40_000, Teto(orcamento, lazer));
        Assert.True(Linha(orcamento, lazer).GetProperty("arquivada").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await c.Cliente.DeleteAsync($"{Rota}/tetos/2026-11/{lazer}")).StatusCode);

        var novo = await PutTetoAsync(c.Cliente, "2026-11", lazer, 30_000);
        Assert.Equal(HttpStatusCode.BadRequest, novo.StatusCode);
        Assert.Equal("Categoria arquivada: escolha outra.", (await JsonAsync(novo)).GetProperty("title").GetString());
        Assert.Equal(0, await TetosAtivosAsync(c, "2026-11", lazer));
    }

    // ---------- Concorrência ----------

    [Fact]
    public async Task PUT_simultaneo_no_mesmo_teto_grava_uma_linha()
    {
        await using var c = await IniciarAsync();
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");

        var respostas = await Task.WhenAll(Enumerable.Range(1, 10)
            .Select(i => PutTetoAsync(c.Cliente, "2026-11", mercado, i * 1_000L)));

        Assert.All(respostas, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        Assert.Equal(1, await TetosAtivosAsync(c, "2026-11", mercado));
    }

    // ---------- Isolamento ----------

    [Fact]
    public async Task Orcamento_nao_mostra_uso_de_outro_casal()
    {
        await using var c = await IniciarAsync();
        var corrente = await CriarContaSimplesAsync(c.Cliente, "CORRENTE", "Itaú");
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        var salario = await CategoriaAsync(c.Cliente, "Salário");
        await LancarAsync(c.Cliente, corrente, mercado, "DESPESA", 10_000, "2026-11-03");
        var outroCasal = Guid.NewGuid();
        await ExecutarAsync(c.Banco, $"INSERT INTO identidade.casais (id, nome_familia) VALUES ('{outroCasal}', 'Outra')");
        await ExecutarAsync(c.Banco,
            "INSERT INTO lancamentos.transacoes (id, casal_id, conta_id, categoria_id, data, valor_centavos, natureza, descricao_cifrada, origem, id_externo) VALUES " +
            $"('{Guid.NewGuid()}', '{outroCasal}', '{corrente}', '{mercado}', '2026-11-03', -77000, 'DESPESA', '\\x01', 'MANUAL', '{Guid.NewGuid()}'), " +
            $"('{Guid.NewGuid()}', '{outroCasal}', '{corrente}', '{salario}', '2026-10-03', 55000, 'RECEITA', '\\x01', 'MANUAL', '{Guid.NewGuid()}')");
        await ExecutarAsync(c.Banco,
            "INSERT INTO orcamento.tetos (id, casal_id, mes, categoria_id, valor_centavos) VALUES " +
            $"('{Guid.NewGuid()}', '{outroCasal}', '2026-11-01', '{mercado}', 99000), " +
            $"('{Guid.NewGuid()}', '{outroCasal}', '2026-09-01', '{mercado}', 98000)");
        await ExecutarAsync(c.Banco,
            $"INSERT INTO orcamento.receitas_previstas (id, casal_id, mes, valor_centavos) VALUES ('{Guid.NewGuid()}', '{outroCasal}', '2026-11-01', 1234500)");

        var orcamento = await ObterAsync(c.Cliente, "2026-11");

        Assert.Equal(10_000, Linha(orcamento, mercado).GetProperty("usadoCentavos").GetInt64());
        Assert.Null(Teto(orcamento, mercado));
        Assert.Equal(0, orcamento.GetProperty("receitaSugeridaCentavos").GetInt64());
        Assert.Equal(JsonValueKind.Null, orcamento.GetProperty("receitaPrevistaCentavos").ValueKind);
        Assert.Equal(JsonValueKind.Null, orcamento.GetProperty("mesParaCopiar").ValueKind);
        Assert.Equal(10_000, orcamento.GetProperty("fatias").GetProperty("tetoDeGastos").GetProperty("usadoCentavos").GetInt64());
        Assert.Equal(HttpStatusCode.NotFound, (await c.Cliente.DeleteAsync($"{Rota}/tetos/2026-11/{mercado}")).StatusCode);
    }
}
