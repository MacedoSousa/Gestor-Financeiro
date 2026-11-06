using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GestorFinanceiro.IntegrationTests.Infra;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.UnitTests;
using Npgsql;

namespace GestorFinanceiro.IntegrationTests;

/// <summary>
/// Metas do casal: CRUD com as contas da meta (uma conta em no máximo uma meta), limites, data desejada,
/// o painel (guardado, aporte do mês, projeção) e a simulação sem efeito colateral. Relógio: 05/11/2026.
/// </summary>
[Collection("postgres")]
public class MetasTests(PostgresFixture pg)
{
    private const string SqlAna = "SELECT id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'";
    private const string SqlCasal = "SELECT casal_id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'";
    private const string TituloContaEmOutraMeta = "Esta conta já pertence a outra meta.";
    private const string TituloNomeRepetido = "Já existe uma meta com esse nome.";
    private const string TituloOutroTipo = "Só contas de poupança ou investimento entram numa meta.";
    private const string TituloArquivada = "Conta arquivada: escolha outra.";

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

    private static async Task<string> TituloAsync(HttpResponseMessage r) => (await JsonAsync(r)).GetProperty("title").GetString()!;

    private static async Task<JsonElement> ErrosAsync(HttpResponseMessage r) => (await JsonAsync(r)).GetProperty("errors");

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

    private static async Task<Guid> CriarContaAsync(
        HttpClient cliente, string tipo = "POUPANCA", long saldo = 100_000, string? apelido = null, string? instituicao = null)
    {
        var resposta = await cliente.PostAsJsonAsync("/api/v1/contas", new
        {
            tipo, apelido = apelido ?? $"{tipo} {Guid.NewGuid():N}"[..20], instituicao,
            saldoInicialCentavos = saldo, saldoInicialEm = "2026-10-01",
        });
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return Guid.Parse(resposta.Headers.Location!.ToString().Split('/').Last());
    }

    private static object Meta(string nome, Guid[] contas, long alvo = 1_000_000, long aporte = 100_000,
        decimal rendimento = 0m, string? data = null) => new
        {
            nome, valorAlvoCentavos = alvo, dataDesejada = data, aportePlanejadoCentavos = aporte,
            rendimentoMensalPercentual = rendimento, contaIds = contas,
        };

    private static async Task<Guid> CriarMetaAsync(HttpClient cliente, object meta)
    {
        var resposta = await cliente.PostAsJsonAsync("/api/v1/metas", meta);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return (await JsonAsync(resposta)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> PainelAsync(HttpClient cliente)
    {
        var resposta = await cliente.GetAsync("/api/v1/metas");
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        return await JsonAsync(resposta);
    }

    private static JsonElement MetaDoPainel(JsonElement painel, Guid id) =>
        painel.GetProperty("metas").EnumerateArray().Single(m => m.GetProperty("id").GetGuid() == id);

    private static JsonElement ContaDoPainel(JsonElement painel, Guid id) =>
        painel.GetProperty("contas").EnumerateArray().Single(c => c.GetProperty("contaId").GetGuid() == id);

    private static Guid[] ContasDaMeta(JsonElement meta) =>
        meta.GetProperty("contas").EnumerateArray().Select(c => c.GetProperty("contaId").GetGuid()).Order().ToArray();

    private static async Task TransferirAsync(HttpClient cliente, Guid origem, Guid destino, long valor, string data)
    {
        var resposta = await cliente.PostAsJsonAsync("/api/v1/transferencias", new
        {
            chaveIdempotente = Guid.NewGuid(), contaOrigemId = origem, contaDestinoId = destino, valorCentavos = valor, data,
            descricao = "Transferência",
        });
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
    }

    private static Task<long> ContarAsync(BancoDeTeste banco, string sql) => EscalarAsync<long>(banco, sql);

    private static async Task<(Guid Casal, Guid Conta, Guid Meta)> OutroCasalComMetaAsync(BancoDeTeste banco)
    {
        var casal = Guid.NewGuid();
        var conta = Guid.NewGuid();
        var meta = Guid.NewGuid();
        await ExecutarAsync(banco, $"INSERT INTO identidade.casais (id, nome_familia) VALUES ('{casal}', 'Outra')");
        await ExecutarAsync(banco,
            "INSERT INTO contas.contas (id, casal_id, tipo, apelido, saldo_inicial_centavos, saldo_inicial_em) " +
            $"VALUES ('{conta}', '{casal}', 'POUPANCA', 'Alheia', 900000, '2026-10-01')");
        await ExecutarAsync(banco,
            "INSERT INTO investimentos.metas (id, casal_id, nome, valor_alvo_centavos, aporte_planejado_centavos, taxa_mensal) " +
            $"VALUES ('{meta}', '{casal}', 'Casa', 5000000, 10000, 0)");
        await ExecutarAsync(banco,
            $"INSERT INTO investimentos.metas_contas (id, casal_id, meta_id, conta_id) VALUES ('{Guid.NewGuid()}', '{casal}', '{meta}', '{conta}')");
        return (casal, conta, meta);
    }

    // ---------------------------------------------------------------- CRUD e contas

    [Fact]
    public async Task Cria_lista_edita_e_apaga_meta()
    {
        await using var c = await IniciarAsync();
        var p1 = await CriarContaAsync(c.Cliente, "POUPANCA", 100_000, "Poupança 1", "Banco Teste");
        var p2 = await CriarContaAsync(c.Cliente, "POUPANCA", 200_000, "Poupança 2");
        var i1 = await CriarContaAsync(c.Cliente, "INVESTIMENTO", 300_000, "Tesouro");
        var corrente = await CriarContaAsync(c.Cliente, "CORRENTE", 50_000, "Corrente");

        var criada = await c.Cliente.PostAsJsonAsync("/api/v1/metas",
            Meta("Casa", [p1, p2], alvo: 50_000_000, aporte: 300_000, rendimento: 0.85m, data: "2029-03"));

        Assert.Equal(HttpStatusCode.Created, criada.StatusCode);
        Assert.Equal("no-store", criada.Headers.CacheControl?.ToString());
        var dto = await JsonAsync(criada);
        var id = dto.GetProperty("id").GetGuid();
        Assert.Equal($"/api/v1/metas/{id}", criada.Headers.Location!.ToString());
        Assert.Equal("Casa", dto.GetProperty("nome").GetString());
        Assert.Equal(50_000_000, dto.GetProperty("valorAlvoCentavos").GetInt64());
        Assert.Equal("2029-03", dto.GetProperty("dataDesejada").GetString());
        Assert.Equal(300_000, dto.GetProperty("aportePlanejadoCentavos").GetInt64());
        Assert.Equal(0.85m, dto.GetProperty("rendimentoMensalPercentual").GetDecimal());
        Assert.Equal(300_000, dto.GetProperty("guardadoCentavos").GetInt64());
        Assert.Equal(0, dto.GetProperty("progressoPercentual").GetInt32());
        Assert.Equal(new[] { p1, p2 }.Order().ToArray(), ContasDaMeta(dto));
        Assert.Equal("CHEGA", dto.GetProperty("projecao").GetProperty("situacao").GetString());
        Assert.Equal(0.0085m, await EscalarAsync<decimal>(c.Banco, $"SELECT taxa_mensal FROM investimentos.metas WHERE id = '{id}'"));
        Assert.Equal(new DateOnly(2029, 3, 1), await EscalarAsync<DateOnly>(c.Banco, $"SELECT data_desejada FROM investimentos.metas WHERE id = '{id}'"));

        var resposta = await c.Cliente.GetAsync("/api/v1/metas");
        Assert.Equal("no-store", resposta.Headers.CacheControl?.ToString());
        var painel = await JsonAsync(resposta);
        Assert.Equal("2026-11", painel.GetProperty("mes").GetString());
        Assert.Single(painel.GetProperty("metas").EnumerateArray());
        Assert.Equal(3, painel.GetProperty("contas").GetArrayLength()); // a corrente fica fora.
        Assert.DoesNotContain(painel.GetProperty("contas").EnumerateArray(), x => x.GetProperty("contaId").GetGuid() == corrente);
        var conta1 = ContaDoPainel(painel, p1);
        Assert.Equal("Poupança 1", conta1.GetProperty("apelido").GetString());
        Assert.Equal("Banco Teste", conta1.GetProperty("instituicao").GetString());
        Assert.Equal("POUPANCA", conta1.GetProperty("tipo").GetString());
        Assert.Equal(100_000, conta1.GetProperty("saldoCentavos").GetInt64());
        Assert.Equal(id, conta1.GetProperty("metaId").GetGuid());
        Assert.Equal("Casa", conta1.GetProperty("metaNome").GetString());
        Assert.Equal(JsonValueKind.Null, ContaDoPainel(painel, i1).GetProperty("metaId").ValueKind);

        // PUT substitui o conjunto: p1 sai (vínculo is_deleted), p2 fica, i1 entra.
        var editada = await c.Cliente.PutAsJsonAsync($"/api/v1/metas/{id}",
            Meta("Casa própria", [p2, i1], alvo: 60_000_000, aporte: 400_000, rendimento: 1m, data: null));
        Assert.Equal(HttpStatusCode.NoContent, editada.StatusCode);
        Assert.Equal(1L, await ContarAsync(c.Banco, $"SELECT count(*) FROM investimentos.metas_contas WHERE conta_id = '{p1}' AND is_deleted"));
        Assert.Equal(0L, await ContarAsync(c.Banco, $"SELECT count(*) FROM investimentos.metas_contas WHERE conta_id = '{p1}' AND NOT is_deleted"));
        Assert.Equal(1L, await ContarAsync(c.Banco, $"SELECT count(*) FROM investimentos.metas_contas WHERE conta_id = '{p2}'"));
        Assert.Equal(2L, await ContarAsync(c.Banco, $"SELECT count(*) FROM investimentos.metas_contas WHERE meta_id = '{id}' AND NOT is_deleted"));

        painel = await PainelAsync(c.Cliente);
        var meta = MetaDoPainel(painel, id);
        Assert.Equal("Casa própria", meta.GetProperty("nome").GetString());
        Assert.Equal(60_000_000, meta.GetProperty("valorAlvoCentavos").GetInt64());
        Assert.Equal(JsonValueKind.Null, meta.GetProperty("dataDesejada").ValueKind);
        Assert.Equal(1m, meta.GetProperty("rendimentoMensalPercentual").GetDecimal());
        Assert.Equal(500_000, meta.GetProperty("guardadoCentavos").GetInt64());
        Assert.Equal(new[] { p2, i1 }.Order().ToArray(), ContasDaMeta(meta));
        Assert.Equal(JsonValueKind.Null, ContaDoPainel(painel, p1).GetProperty("metaId").ValueKind);

        // DELETE: soft delete da meta e dos vínculos; as contas ficam livres.
        Assert.Equal(HttpStatusCode.NoContent, (await c.Cliente.DeleteAsync($"/api/v1/metas/{id}")).StatusCode);
        Assert.True(await EscalarAsync<bool>(c.Banco, $"SELECT is_deleted FROM investimentos.metas WHERE id = '{id}'"));
        Assert.Equal(0L, await ContarAsync(c.Banco, $"SELECT count(*) FROM investimentos.metas_contas WHERE meta_id = '{id}' AND NOT is_deleted"));
        Assert.Equal(3L, await ContarAsync(c.Banco, $"SELECT count(*) FROM investimentos.metas_contas WHERE meta_id = '{id}'"));
        painel = await PainelAsync(c.Cliente);
        Assert.Empty(painel.GetProperty("metas").EnumerateArray());
        Assert.All(painel.GetProperty("contas").EnumerateArray(), x => Assert.Equal(JsonValueKind.Null, x.GetProperty("metaId").ValueKind));
        Assert.Equal(HttpStatusCode.NotFound, (await c.Cliente.DeleteAsync($"/api/v1/metas/{id}")).StatusCode);
        await CriarMetaAsync(c.Cliente, Meta("Casa", [p2, i1]));
    }

    [Fact]
    public async Task Conta_em_outra_meta_409()
    {
        await using var c = await IniciarAsync();
        var p1 = await CriarContaAsync(c.Cliente);
        var p2 = await CriarContaAsync(c.Cliente);
        await CriarMetaAsync(c.Cliente, Meta("Casa", [p1]));

        var nova = await c.Cliente.PostAsJsonAsync("/api/v1/metas", Meta("Carro", [p2, p1]));
        Assert.Equal(HttpStatusCode.Conflict, nova.StatusCode);
        Assert.Equal(TituloContaEmOutraMeta, await TituloAsync(nova));
        Assert.Equal(1L, await ContarAsync(c.Banco, "SELECT count(*) FROM investimentos.metas"));

        var carro = await CriarMetaAsync(c.Cliente, Meta("Carro", [p2]));
        var editada = await c.Cliente.PutAsJsonAsync($"/api/v1/metas/{carro}", Meta("Carro", [p2, p1]));
        Assert.Equal(HttpStatusCode.Conflict, editada.StatusCode);
        Assert.Equal(TituloContaEmOutraMeta, await TituloAsync(editada));
        Assert.Equal(1L, await ContarAsync(c.Banco, $"SELECT count(*) FROM investimentos.metas_contas WHERE conta_id = '{p1}'"));
        Assert.Equal(1L, await ContarAsync(c.Banco, $"SELECT count(*) FROM investimentos.metas_contas WHERE meta_id = '{carro}'"));
    }

    [Fact]
    public async Task Duas_metas_em_paralelo_com_a_mesma_conta()
    {
        await using var c = await IniciarAsync();
        var p1 = await CriarContaAsync(c.Cliente);
        await PainelAsync(c.Cliente);

        var respostas = await Task.WhenAll(
            c.Cliente.PostAsJsonAsync("/api/v1/metas", Meta("Casa", [p1])),
            c.Cliente.PostAsJsonAsync("/api/v1/metas", Meta("Carro", [p1])));

        Assert.Equal(1L, await ContarAsync(c.Banco, $"SELECT count(*) FROM investimentos.metas_contas WHERE conta_id = '{p1}' AND NOT is_deleted"));
        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], respostas.Select(r => r.StatusCode).OrderBy(s => s).ToArray());
        Assert.Equal(TituloContaEmOutraMeta, await TituloAsync(respostas.Single(r => r.StatusCode == HttpStatusCode.Conflict)));
        Assert.Equal(1L, await ContarAsync(c.Banco, "SELECT count(*) FROM investimentos.metas WHERE NOT is_deleted"));
    }

    [Fact]
    public async Task Conta_de_outro_tipo_400()
    {
        await using var c = await IniciarAsync();
        var p1 = await CriarContaAsync(c.Cliente);
        var meta = await CriarMetaAsync(c.Cliente, Meta("Casa", [p1]));
        foreach (var tipo in new[] { "CORRENTE", "DINHEIRO" })
        {
            var conta = await CriarContaAsync(c.Cliente, tipo);
            var nova = await c.Cliente.PostAsJsonAsync("/api/v1/metas", Meta("Carro", [conta]));
            Assert.Equal(HttpStatusCode.BadRequest, nova.StatusCode);
            Assert.Equal(TituloOutroTipo, await TituloAsync(nova));
            var editada = await c.Cliente.PutAsJsonAsync($"/api/v1/metas/{meta}", Meta("Casa", [p1, conta]));
            Assert.Equal(HttpStatusCode.BadRequest, editada.StatusCode);
            Assert.Equal(TituloOutroTipo, await TituloAsync(editada));
        }
        Assert.Equal(1L, await ContarAsync(c.Banco, "SELECT count(*) FROM investimentos.metas_contas"));
    }

    [Fact]
    public async Task Conta_arquivada_entrando_400_mas_pode_ficar()
    {
        await using var c = await IniciarAsync();
        var p1 = await CriarContaAsync(c.Cliente);
        var p2 = await CriarContaAsync(c.Cliente);
        var meta = await CriarMetaAsync(c.Cliente, Meta("Casa", [p1]));
        await ExecutarAsync(c.Banco, $"UPDATE contas.contas SET arquivada = true WHERE id IN ('{p1}', '{p2}')");

        var nova = await c.Cliente.PostAsJsonAsync("/api/v1/metas", Meta("Carro", [p2]));
        Assert.Equal(HttpStatusCode.BadRequest, nova.StatusCode);
        Assert.Equal(TituloArquivada, await TituloAsync(nova));

        var entrando = await c.Cliente.PutAsJsonAsync($"/api/v1/metas/{meta}", Meta("Casa", [p1, p2]));
        Assert.Equal(HttpStatusCode.BadRequest, entrando.StatusCode);
        Assert.Equal(TituloArquivada, await TituloAsync(entrando));

        // Já vinculada antes de arquivar: continua na meta e conta no guardado.
        var ficando = await c.Cliente.PutAsJsonAsync($"/api/v1/metas/{meta}", Meta("Casa", [p1], alvo: 2_000_000));
        Assert.Equal(HttpStatusCode.NoContent, ficando.StatusCode);
        var dto = MetaDoPainel(await PainelAsync(c.Cliente), meta);
        Assert.Equal(2_000_000, dto.GetProperty("valorAlvoCentavos").GetInt64());
        Assert.True(dto.GetProperty("contas")[0].GetProperty("arquivada").GetBoolean());
        Assert.Equal(100_000, dto.GetProperty("guardadoCentavos").GetInt64());
    }

    [Fact]
    public async Task Conta_ou_meta_de_outro_casal_404()
    {
        await using var c = await IniciarAsync();
        var p1 = await CriarContaAsync(c.Cliente);
        var nossa = await CriarMetaAsync(c.Cliente, Meta("Nossa", [p1]));
        var (_, contaAlheia, metaAlheia) = await OutroCasalComMetaAsync(c.Banco);

        Assert.Equal(HttpStatusCode.NotFound, (await c.Cliente.PostAsJsonAsync("/api/v1/metas", Meta("Carro", [contaAlheia]))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Cliente.PostAsJsonAsync("/api/v1/metas", Meta("Carro", [Guid.NewGuid()]))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Cliente.PutAsJsonAsync($"/api/v1/metas/{nossa}", Meta("Nossa", [p1, contaAlheia]))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Cliente.PutAsJsonAsync($"/api/v1/metas/{metaAlheia}", Meta("Casa", [p1]))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Cliente.DeleteAsync($"/api/v1/metas/{metaAlheia}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Cliente.GetAsync($"/api/v1/metas/{metaAlheia}/simulacao?aporte=1000")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Cliente.GetAsync($"/api/v1/metas/{Guid.NewGuid()}/simulacao?aporte=1000")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Cliente.PutAsJsonAsync($"/api/v1/metas/{Guid.NewGuid()}", Meta("X", [p1]))).StatusCode);

        Assert.False(await EscalarAsync<bool>(c.Banco, $"SELECT is_deleted FROM investimentos.metas WHERE id = '{metaAlheia}'"));
        Assert.Equal("Casa", await EscalarAsync<string>(c.Banco, $"SELECT nome FROM investimentos.metas WHERE id = '{metaAlheia}'"));
        Assert.Equal(1L, await ContarAsync(c.Banco, $"SELECT count(*) FROM investimentos.metas_contas WHERE meta_id = '{nossa}'"));
    }

    [Fact]
    public async Task Nome_repetido_sem_caixa_409()
    {
        await using var c = await IniciarAsync();
        var p1 = await CriarContaAsync(c.Cliente);
        var p2 = await CriarContaAsync(c.Cliente);
        var p3 = await CriarContaAsync(c.Cliente);
        var casa = await CriarMetaAsync(c.Cliente, Meta("Casa", [p1]));

        var repetida = await c.Cliente.PostAsJsonAsync("/api/v1/metas", Meta("CASA", [p2]));
        Assert.Equal(HttpStatusCode.Conflict, repetida.StatusCode);
        Assert.Equal(TituloNomeRepetido, await TituloAsync(repetida));

        var carro = await CriarMetaAsync(c.Cliente, Meta("Carro", [p2]));
        var renomeada = await c.Cliente.PutAsJsonAsync($"/api/v1/metas/{carro}", Meta("casa", [p2]));
        Assert.Equal(HttpStatusCode.Conflict, renomeada.StatusCode);
        Assert.Equal(TituloNomeRepetido, await TituloAsync(renomeada));

        // A própria meta pode mudar só a caixa do nome.
        Assert.Equal(HttpStatusCode.NoContent, (await c.Cliente.PutAsJsonAsync($"/api/v1/metas/{casa}", Meta("CASA", [p1]))).StatusCode);
        // Apagada, o nome fica livre (índice parcial).
        Assert.Equal(HttpStatusCode.NoContent, (await c.Cliente.DeleteAsync($"/api/v1/metas/{casa}")).StatusCode);
        await CriarMetaAsync(c.Cliente, Meta("casa", [p3]));
    }

    [Fact]
    public async Task Nome_e_aparado_antes_de_validar_e_comparar()
    {
        await using var c = await IniciarAsync();
        var p1 = await CriarContaAsync(c.Cliente);
        var p2 = await CriarContaAsync(c.Cliente);
        var p3 = await CriarContaAsync(c.Cliente);

        var vazio = await c.Cliente.PostAsJsonAsync("/api/v1/metas", Meta("   ", [p1]));
        Assert.Equal(HttpStatusCode.BadRequest, vazio.StatusCode);
        Assert.True((await ErrosAsync(vazio)).TryGetProperty("nome", out _));

        await CriarMetaAsync(c.Cliente, Meta("casa", [p1]));
        var comEspaco = await c.Cliente.PostAsJsonAsync("/api/v1/metas", Meta("Casa ", [p2]));
        Assert.Equal(HttpStatusCode.Conflict, comEspaco.StatusCode);
        Assert.Equal(TituloNomeRepetido, await TituloAsync(comEspaco));

        var carro = await CriarMetaAsync(c.Cliente, Meta("  Carro  ", [p2]));
        Assert.Equal("Carro", await EscalarAsync<string>(c.Banco, $"SELECT nome FROM investimentos.metas WHERE id = '{carro}'"));

        // 60 caracteres depois de aparar valem; 61 não.
        var sessenta = new string('x', 60);
        var longa = await CriarMetaAsync(c.Cliente, Meta($"  {sessenta}  ", [p3]));
        Assert.Equal(sessenta, await EscalarAsync<string>(c.Banco, $"SELECT nome FROM investimentos.metas WHERE id = '{longa}'"));
        var demais = await c.Cliente.PutAsJsonAsync($"/api/v1/metas/{longa}", Meta(sessenta + "y", [p3]));
        Assert.Equal(HttpStatusCode.BadRequest, demais.StatusCode);
        Assert.True((await ErrosAsync(demais)).TryGetProperty("nome", out _));
        var editadoVazio = await c.Cliente.PutAsJsonAsync($"/api/v1/metas/{carro}", Meta(" ", [p2]));
        Assert.Equal(HttpStatusCode.BadRequest, editadoVazio.StatusCode);
    }

    [Fact]
    public async Task Sem_contas_400()
    {
        await using var c = await IniciarAsync();
        var p1 = await CriarContaAsync(c.Cliente);
        var meta = await CriarMetaAsync(c.Cliente, Meta("Casa", [p1]));

        foreach (var resposta in new[]
                 {
                     await c.Cliente.PostAsJsonAsync("/api/v1/metas", Meta("Carro", [])),
                     await c.Cliente.PostAsJsonAsync("/api/v1/metas", new
                     {
                         nome = "Carro", valorAlvoCentavos = 1000L, aportePlanejadoCentavos = 0L, rendimentoMensalPercentual = 0m,
                     }),
                     await c.Cliente.PutAsJsonAsync($"/api/v1/metas/{meta}", Meta("Casa", [])),
                 })
        {
            Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
            Assert.Equal("Escolha ao menos uma conta.", (await ErrosAsync(resposta)).GetProperty("contaIds")[0].GetString());
        }
        Assert.Equal(1L, await ContarAsync(c.Banco, "SELECT count(*) FROM investimentos.metas"));
        Assert.Equal(1L, await ContarAsync(c.Banco, "SELECT count(*) FROM investimentos.metas_contas WHERE NOT is_deleted"));
    }

    // ---------------------------------------------------------------- limites

    [Fact]
    public async Task Limites()
    {
        await using var c = await IniciarAsync();
        var p1 = await CriarContaAsync(c.Cliente);
        var p2 = await CriarContaAsync(c.Cliente);
        var meta = await CriarMetaAsync(c.Cliente, Meta("Casa", [p1]));

        async Task Confere(object pedido, string? campoComErro)
        {
            var resposta = await c.Cliente.PutAsJsonAsync($"/api/v1/metas/{meta}", pedido);
            if (campoComErro is null)
            {
                Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);
                return;
            }
            Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
            Assert.True((await ErrosAsync(resposta)).TryGetProperty(campoComErro, out _), campoComErro);
        }

        await Confere(Meta("Casa", [p1], alvo: 0), "valorAlvoCentavos");
        await Confere(Meta("Casa", [p1], alvo: 1), null);
        await Confere(Meta("Casa", [p1], alvo: 100_000_000_000), null);
        await Confere(Meta("Casa", [p1], alvo: 100_000_000_001), "valorAlvoCentavos");
        await Confere(Meta("Casa", [p1], aporte: -1), "aportePlanejadoCentavos");
        await Confere(Meta("Casa", [p1], aporte: 0), null);
        await Confere(Meta("Casa", [p1], aporte: 100_000_000_000), null);
        await Confere(Meta("Casa", [p1], aporte: 100_000_000_001), "aportePlanejadoCentavos");
        await Confere(Meta("Casa", [p1], rendimento: 0m), null);
        await Confere(Meta("Casa", [p1], rendimento: 5m), null);
        Assert.Equal(0.05m, await EscalarAsync<decimal>(c.Banco, $"SELECT taxa_mensal FROM investimentos.metas WHERE id = '{meta}'"));
        Assert.Equal(5m, MetaDoPainel(await PainelAsync(c.Cliente), meta).GetProperty("rendimentoMensalPercentual").GetDecimal());
        await Confere(Meta("Casa", [p1], rendimento: 5.0001m), "rendimentoMensalPercentual");
        await Confere(Meta("Casa", [p1], rendimento: 0.12345m), "rendimentoMensalPercentual");
        await Confere(Meta("Casa", [p1], rendimento: -0.0001m), "rendimentoMensalPercentual");
        await Confere(Meta("Casa", [p1], rendimento: 4.9999m), null);
        Assert.Equal(0.049999m, await EscalarAsync<decimal>(c.Banco, $"SELECT taxa_mensal FROM investimentos.metas WHERE id = '{meta}'"));
        Assert.Equal(4.9999m, MetaDoPainel(await PainelAsync(c.Cliente), meta).GetProperty("rendimentoMensalPercentual").GetDecimal());

        // O POST valida igual e não grava nada.
        var antes = await ContarAsync(c.Banco, "SELECT count(*) FROM investimentos.metas");
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Cliente.PostAsJsonAsync("/api/v1/metas", Meta("Carro", [p2], alvo: 0))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Cliente.PostAsJsonAsync("/api/v1/metas", Meta("Carro", [p2], aporte: -1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Cliente.PostAsJsonAsync("/api/v1/metas", Meta("Carro", [p2], rendimento: 5.0001m))).StatusCode);
        Assert.Equal(antes, await ContarAsync(c.Banco, "SELECT count(*) FROM investimentos.metas"));
    }

    [Fact]
    public async Task Campo_numerico_ausente_400()
    {
        await using var c = await IniciarAsync();
        var p1 = await CriarContaAsync(c.Cliente);
        var meta = await CriarMetaAsync(c.Cliente, Meta("Casa", [p1]));
        var semNumeros = new { nome = "Carro", contaIds = new[] { p1 } };

        foreach (var resposta in new[]
                 {
                     await c.Cliente.PostAsJsonAsync("/api/v1/metas", semNumeros),
                     await c.Cliente.PutAsJsonAsync($"/api/v1/metas/{meta}", semNumeros),
                 })
        {
            Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
            var erros = await ErrosAsync(resposta);
            Assert.True(erros.TryGetProperty("valorAlvoCentavos", out _));
            Assert.True(erros.TryGetProperty("aportePlanejadoCentavos", out _));
            Assert.True(erros.TryGetProperty("rendimentoMensalPercentual", out _));
        }

        var soAporte = await c.Cliente.PostAsJsonAsync("/api/v1/metas",
            new { nome = "Carro", valorAlvoCentavos = 1000L, rendimentoMensalPercentual = 0m, contaIds = new[] { p1 } });
        Assert.Equal(HttpStatusCode.BadRequest, soAporte.StatusCode);
        Assert.True((await ErrosAsync(soAporte)).TryGetProperty("aportePlanejadoCentavos", out _));
        Assert.Equal("Casa", await EscalarAsync<string>(c.Banco, $"SELECT nome FROM investimentos.metas WHERE id = '{meta}'"));
        Assert.Equal(1L, await ContarAsync(c.Banco, "SELECT count(*) FROM investimentos.metas"));
    }

    // ---------------------------------------------------------------- data desejada

    [Fact]
    public async Task Data_desejada_no_mes_corrente_ok_e_anterior_400()
    {
        await using var c = await IniciarAsync();
        var p1 = await CriarContaAsync(c.Cliente);
        var p2 = await CriarContaAsync(c.Cliente);

        var corrente = await c.Cliente.PostAsJsonAsync("/api/v1/metas", Meta("Casa", [p1], data: "2026-11"));
        Assert.Equal(HttpStatusCode.Created, corrente.StatusCode);
        var projecao = (await JsonAsync(corrente)).GetProperty("projecao");
        Assert.False(projecao.GetProperty("dataDesejadaPassou").GetBoolean());

        foreach (var data in new[] { "2026-10", "2026-13", "2026-11-01", "nov/2026" })
        {
            var resposta = await c.Cliente.PostAsJsonAsync("/api/v1/metas", Meta("Carro", [p2], data: data));
            Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
            Assert.True((await ErrosAsync(resposta)).TryGetProperty("dataDesejada", out _), data);
        }
        Assert.Equal(1L, await ContarAsync(c.Banco, "SELECT count(*) FROM investimentos.metas"));
    }

    [Fact]
    public async Task Editar_meta_mantendo_data_passada_e_aceito()
    {
        await using var c = await IniciarAsync();
        var p1 = await CriarContaAsync(c.Cliente);
        var meta = await CriarMetaAsync(c.Cliente, Meta("Casa", [p1], data: "2027-01"));
        await ExecutarAsync(c.Banco, $"UPDATE investimentos.metas SET data_desejada = '2026-01-01' WHERE id = '{meta}'");

        var resposta = await c.Cliente.PutAsJsonAsync($"/api/v1/metas/{meta}", Meta("Casa", [p1], alvo: 3_000_000, data: "2026-01"));

        Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);
        var dto = MetaDoPainel(await PainelAsync(c.Cliente), meta);
        Assert.Equal(3_000_000, dto.GetProperty("valorAlvoCentavos").GetInt64());
        Assert.Equal("2026-01", dto.GetProperty("dataDesejada").GetString());
        Assert.True(dto.GetProperty("projecao").GetProperty("dataDesejadaPassou").GetBoolean());
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("projecao").GetProperty("aporteNecessarioCentavos").ValueKind);
    }

    [Fact]
    public async Task Alterar_para_outra_data_passada_400()
    {
        await using var c = await IniciarAsync();
        var p1 = await CriarContaAsync(c.Cliente);
        var meta = await CriarMetaAsync(c.Cliente, Meta("Casa", [p1], data: "2027-01"));
        await ExecutarAsync(c.Banco, $"UPDATE investimentos.metas SET data_desejada = '2026-01-01' WHERE id = '{meta}'");

        foreach (var data in new[] { "2026-02", "2025-12", "2026-10" })
        {
            var resposta = await c.Cliente.PutAsJsonAsync($"/api/v1/metas/{meta}", Meta("Casa", [p1], data: data));
            Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
            Assert.True((await ErrosAsync(resposta)).TryGetProperty("dataDesejada", out _), data);
        }
        Assert.Equal(new DateOnly(2026, 1, 1), await EscalarAsync<DateOnly>(c.Banco, $"SELECT data_desejada FROM investimentos.metas WHERE id = '{meta}'"));

        // Para uma data a partir de M, ou sem data, pode.
        Assert.Equal(HttpStatusCode.NoContent, (await c.Cliente.PutAsJsonAsync($"/api/v1/metas/{meta}", Meta("Casa", [p1], data: "2026-11"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.Cliente.PutAsJsonAsync($"/api/v1/metas/{meta}", Meta("Casa", [p1], data: null))).StatusCode);
        Assert.Equal(DBNull.Value, await EscalarAsync<object>(c.Banco, $"SELECT data_desejada FROM investimentos.metas WHERE id = '{meta}'"));
    }

    // ---------------------------------------------------------------- painel

    [Fact]
    public async Task Guardado_soma_saldos_inclusive_conta_arquivada()
    {
        await using var c = await IniciarAsync();
        var p1 = await CriarContaAsync(c.Cliente, "POUPANCA", 100_000);
        var i1 = await CriarContaAsync(c.Cliente, "INVESTIMENTO", 250_001);
        var meta = await CriarMetaAsync(c.Cliente, Meta("Casa", [p1, i1], alvo: 1_000_000));
        await ExecutarAsync(c.Banco, $"UPDATE contas.contas SET arquivada = true WHERE id = '{i1}'");

        var painel = await PainelAsync(c.Cliente);

        var dto = MetaDoPainel(painel, meta);
        Assert.Equal(350_001, dto.GetProperty("guardadoCentavos").GetInt64());
        Assert.Equal(35, dto.GetProperty("progressoPercentual").GetInt32());
        var arquivada = dto.GetProperty("contas").EnumerateArray().Single(x => x.GetProperty("contaId").GetGuid() == i1);
        Assert.True(arquivada.GetProperty("arquivada").GetBoolean());
        Assert.Equal(250_001, arquivada.GetProperty("saldoCentavos").GetInt64());
        Assert.Equal("INVESTIMENTO", arquivada.GetProperty("tipo").GetString());
        Assert.True(ContaDoPainel(painel, i1).GetProperty("arquivada").GetBoolean());
        Assert.Equal(meta, ContaDoPainel(painel, i1).GetProperty("metaId").GetGuid());
    }

    [Fact]
    public async Task Aporte_do_mes_por_meta()
    {
        await using var c = await IniciarAsync();
        var corrente = await CriarContaAsync(c.Cliente, "CORRENTE", 1_000_000);
        var p1 = await CriarContaAsync(c.Cliente, "POUPANCA", 100_000);
        var i1 = await CriarContaAsync(c.Cliente, "INVESTIMENTO", 100_000);
        var casa = await CriarMetaAsync(c.Cliente, Meta("Casa", [p1], alvo: 10_000_000, aporte: 50_000));
        var carro = await CriarMetaAsync(c.Cliente, Meta("Carro", [i1], alvo: 10_000_000, aporte: 50_000));
        await TransferirAsync(c.Cliente, corrente, p1, 30_000, "2026-11-04");
        await TransferirAsync(c.Cliente, corrente, i1, 20_000, "2026-11-02");
        await TransferirAsync(c.Cliente, corrente, p1, 10_000, "2026-10-15"); // mês passado: guardado, não aporte.

        var painel = await PainelAsync(c.Cliente);

        var a = MetaDoPainel(painel, casa);
        Assert.Equal(30_000, a.GetProperty("aporteDoMesCentavos").GetInt64());
        Assert.Equal(140_000, a.GetProperty("guardadoCentavos").GetInt64());
        Assert.Equal(50_000, a.GetProperty("projecao").GetProperty("aporteMensalCentavos").GetInt64());
        var b = MetaDoPainel(painel, carro);
        Assert.Equal(20_000, b.GetProperty("aporteDoMesCentavos").GetInt64());
        Assert.Equal(120_000, b.GetProperty("guardadoCentavos").GetInt64());
    }

    [Fact]
    public async Task Movimento_entre_metas_nao_e_aporte()
    {
        await using var c = await IniciarAsync();
        var p1 = await CriarContaAsync(c.Cliente, "POUPANCA", 100_000);
        var i1 = await CriarContaAsync(c.Cliente, "INVESTIMENTO", 100_000);
        var casa = await CriarMetaAsync(c.Cliente, Meta("Casa", [p1]));
        var carro = await CriarMetaAsync(c.Cliente, Meta("Carro", [i1]));
        await TransferirAsync(c.Cliente, p1, i1, 25_000, "2026-11-04");

        var painel = await PainelAsync(c.Cliente);

        var a = MetaDoPainel(painel, casa);
        var b = MetaDoPainel(painel, carro);
        Assert.Equal(0, a.GetProperty("aporteDoMesCentavos").GetInt64());
        Assert.Equal(0, b.GetProperty("aporteDoMesCentavos").GetInt64());
        Assert.Equal(75_000, a.GetProperty("guardadoCentavos").GetInt64());
        Assert.Equal(125_000, b.GetProperty("guardadoCentavos").GetInt64());
    }

    [Fact]
    public async Task Rendimento_conciliado_aumenta_guardado_e_nao_aporte()
    {
        await using var c = await IniciarAsync();
        var p1 = await CriarContaAsync(c.Cliente, "POUPANCA", 100_000);
        var meta = await CriarMetaAsync(c.Cliente, Meta("Casa", [p1]));

        var conciliacao = await c.Cliente.PostAsJsonAsync($"/api/v1/contas/{p1}/conciliacao",
            new { saldoInformadoCentavos = 103_500L, saldoExibidoCentavos = 100_000L });
        Assert.Equal(HttpStatusCode.OK, conciliacao.StatusCode);

        var dto = MetaDoPainel(await PainelAsync(c.Cliente), meta);
        Assert.Equal(103_500, dto.GetProperty("guardadoCentavos").GetInt64());
        Assert.Equal(0, dto.GetProperty("aporteDoMesCentavos").GetInt64());
    }

    [Fact]
    public async Task Conta_apagada_some_da_meta_sem_erro()
    {
        await using var c = await IniciarAsync();
        var p1 = await CriarContaAsync(c.Cliente, "POUPANCA", 100_000);
        var p2 = await CriarContaAsync(c.Cliente, "POUPANCA", 200_000);
        var meta = await CriarMetaAsync(c.Cliente, Meta("Casa", [p1, p2]));
        Assert.Equal(HttpStatusCode.NoContent, (await c.Cliente.DeleteAsync($"/api/v1/contas/{p2}")).StatusCode);

        var painel = await PainelAsync(c.Cliente);

        var dto = MetaDoPainel(painel, meta);
        Assert.Equal([p1], ContasDaMeta(dto));
        Assert.Equal(100_000, dto.GetProperty("guardadoCentavos").GetInt64());
        Assert.DoesNotContain(painel.GetProperty("contas").EnumerateArray(), x => x.GetProperty("contaId").GetGuid() == p2);
        Assert.Equal(HttpStatusCode.OK, (await c.Cliente.GetAsync($"/api/v1/metas/{meta}/simulacao?aporte=1000")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.Cliente.PutAsJsonAsync($"/api/v1/metas/{meta}", Meta("Casa", [p1]))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Cliente.PutAsJsonAsync($"/api/v1/metas/{meta}", Meta("Casa", [p1, p2]))).StatusCode);
    }

    [Fact]
    public async Task Painel_nao_mostra_outro_casal()
    {
        await using var c = await IniciarAsync();
        var p1 = await CriarContaAsync(c.Cliente);
        var nossa = await CriarMetaAsync(c.Cliente, Meta("Nossa", [p1]));
        var (_, contaAlheia, metaAlheia) = await OutroCasalComMetaAsync(c.Banco);

        var painel = await PainelAsync(c.Cliente);

        Assert.Equal([nossa], painel.GetProperty("metas").EnumerateArray().Select(m => m.GetProperty("id").GetGuid()).ToArray());
        Assert.Equal([p1], painel.GetProperty("contas").EnumerateArray().Select(x => x.GetProperty("contaId").GetGuid()).ToArray());
        Assert.DoesNotContain(metaAlheia.ToString(), painel.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(contaAlheia.ToString(), painel.GetRawText(), StringComparison.Ordinal);
        // Mesmo nome "Casa" no outro casal não colide com o nosso.
        await CriarMetaAsync(c.Cliente, Meta("Casa", [await CriarContaAsync(c.Cliente)]));
    }

    // ---------------------------------------------------------------- simulação

    private static Task<long> LinhasAsync(BancoDeTeste banco) => ContarAsync(banco,
        "SELECT (SELECT count(*) FROM investimentos.metas) + (SELECT count(*) FROM investimentos.metas_contas) " +
        "+ (SELECT count(*) FROM audit.eventos)");

    [Fact]
    public async Task Simulacao_sem_efeito_colateral()
    {
        await using var c = await IniciarAsync();
        var p1 = await CriarContaAsync(c.Cliente, "POUPANCA", 100_000);
        var meta = await CriarMetaAsync(c.Cliente, Meta("Casa", [p1], alvo: 1_000_000, aporte: 100_000));
        await PainelAsync(c.Cliente);
        var antes = await LinhasAsync(c.Banco);
        var eventosAntes = await ContarAsync(c.Banco, "SELECT count(*) FROM audit.eventos");
        var atualizadaAntes = await EscalarAsync<DateTime>(c.Banco, $"SELECT atualizado_em FROM investimentos.metas WHERE id = '{meta}'");

        var resposta = await c.Cliente.GetAsync($"/api/v1/metas/{meta}/simulacao?aporte=200000");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("no-store", resposta.Headers.CacheControl?.ToString());
        var projecao = await JsonAsync(resposta);
        Assert.Equal(200_000, projecao.GetProperty("aporteMensalCentavos").GetInt64());
        Assert.Equal("CHEGA", projecao.GetProperty("situacao").GetString());
        Assert.Equal(5, projecao.GetProperty("mesesAteMeta").GetInt32());
        Assert.Equal("2027-03", projecao.GetProperty("chegaEm").GetString());
        Assert.Equal(antes, await LinhasAsync(c.Banco));
        Assert.Equal(eventosAntes, await ContarAsync(c.Banco, "SELECT count(*) FROM audit.eventos"));
        Assert.Equal(atualizadaAntes, await EscalarAsync<DateTime>(c.Banco, $"SELECT atualizado_em FROM investimentos.metas WHERE id = '{meta}'"));
        Assert.Equal(100_000, MetaDoPainel(await PainelAsync(c.Cliente), meta).GetProperty("aportePlanejadoCentavos").GetInt64());
    }

    [Fact]
    public async Task Simulacao_aporte_invalido_400()
    {
        await using var c = await IniciarAsync();
        var p1 = await CriarContaAsync(c.Cliente, "POUPANCA", 100_000);
        var meta = await CriarMetaAsync(c.Cliente, Meta("Casa", [p1]));

        foreach (var consulta in new[] { "?aporte=-1", "?aporte=100000000001", "" })
        {
            var resposta = await c.Cliente.GetAsync($"/api/v1/metas/{meta}/simulacao{consulta}");
            Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
            Assert.True((await ErrosAsync(resposta)).TryGetProperty("aporte", out _), consulta);
        }

        var zero = await JsonAsync(await c.Cliente.GetAsync($"/api/v1/metas/{meta}/simulacao?aporte=0"));
        Assert.Equal("NAO_CHEGA", zero.GetProperty("situacao").GetString());
        var maximo = await c.Cliente.GetAsync($"/api/v1/metas/{meta}/simulacao?aporte=100000000000");
        Assert.Equal(HttpStatusCode.OK, maximo.StatusCode);
        Assert.Equal(1, (await JsonAsync(maximo)).GetProperty("mesesAteMeta").GetInt32());
    }
}
