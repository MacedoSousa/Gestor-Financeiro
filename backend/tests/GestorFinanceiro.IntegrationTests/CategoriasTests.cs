using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GestorFinanceiro.IntegrationTests.Infra;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.UnitTests;
using Npgsql;

namespace GestorFinanceiro.IntegrationTests;

/// <summary>Categorias: padrão por casal sem duplicar, nome único, arquivar e apagar; e conta com lançamento não apaga.</summary>
[Collection("postgres")]
public class CategoriasTests(PostgresFixture pg)
{
    private const string Rota = "/api/v1/categorias";
    private const string SqlAna = "SELECT id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'";
    private const string SqlCasal = "SELECT casal_id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'";

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

    private async Task<(ApiDeTeste Api, BancoDeTeste Banco, HttpClient Cliente, Guid Casal)> IniciarAsync()
    {
        var banco = await pg.NovoBancoAsync();
        var api = new ApiDeTeste(banco);
        var pedido = new CadastroDoCasalDto(
            api.CodigoInstalacao, "Família Teste",
            new ConjugeNovoDto("Ana Teste", CpfSintetico.Gerar(1), "senha-da-ana-123"),
            new ConjugeNovoDto("Bruno Teste", CpfSintetico.Gerar(2), "senha-do-bruno-1"));
        Assert.Equal(HttpStatusCode.Created, (await api.CreateClient().PostAsJsonAsync("/api/v1/casal", pedido)).StatusCode);
        var ana = await EscalarAsync<Guid>(banco, SqlAna);
        var casal = await EscalarAsync<Guid>(banco, SqlCasal);
        return (api, banco, await api.ClienteLogadoAsync(ana), casal);
    }

    private static async Task<JsonElement[]> ListarAsync(HttpClient cliente) =>
        (await JsonAsync(await cliente.GetAsync(Rota))).EnumerateArray().ToArray();

    private static async Task<Guid> CriarAsync(HttpClient cliente, string nome, string tipo = "DESPESA")
    {
        var resposta = await cliente.PostAsJsonAsync(Rota, new { nome, tipo });
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return Guid.Parse(resposta.Headers.Location!.ToString().Split('/').Last());
    }

    private static async Task InserirLancamentoAsync(BancoDeTeste banco, Guid casal, Guid conta, Guid? categoria) =>
        await ExecutarAsync(banco,
            $"INSERT INTO lancamentos.transacoes (id, casal_id, conta_id, categoria_id, data, valor_centavos, natureza, descricao_cifrada, origem, id_externo) " +
            $"VALUES ('{Guid.NewGuid()}', '{casal}', '{conta}', {(categoria is null ? "NULL" : $"'{categoria}'")}, '2026-11-01', -1000, 'DESPESA', '\\x01', 'MANUAL', '{Guid.NewGuid()}')");

    [Fact]
    public async Task Categorias_padrao_nascem_com_papel()
    {
        var (api, _, cliente, _) = await IniciarAsync();
        await using var _ = api;

        var lista = await ListarAsync(cliente);

        string? Papel(string nome) => lista.Single(c => c.GetProperty("nome").GetString() == nome).GetProperty("papel").GetString();
        Assert.Equal("INVESTIMENTO", Papel("Investimento"));
        Assert.Equal("DIVIDAS", Papel("Dívidas e financiamentos"));
        Assert.Equal(10, lista.Count(c => c.GetProperty("papel").ValueKind == JsonValueKind.Null));
    }

    [Fact]
    public async Task Renomear_mantem_papel()
    {
        var (api, _, cliente, _) = await IniciarAsync();
        await using var _ = api;
        var lista = await ListarAsync(cliente);
        var id = lista.Single(c => c.GetProperty("nome").GetString() == "Investimento").GetProperty("id").GetGuid();

        var resposta = await cliente.PutAsJsonAsync($"{Rota}/{id}", new { nome = "Investimentos", tipo = "DESPESA", papel = "DIVIDAS" });

        Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);
        var depois = (await ListarAsync(cliente)).Single(c => c.GetProperty("id").GetGuid() == id);
        Assert.Equal("Investimentos", depois.GetProperty("nome").GetString());
        Assert.Equal("INVESTIMENTO", depois.GetProperty("papel").GetString());
    }

    [Fact]
    public async Task Categoria_com_papel_nao_vira_receita()
    {
        var (api, banco, cliente, _) = await IniciarAsync();
        await using var _ = api;
        var id = (await ListarAsync(cliente)).Single(c => c.GetProperty("nome").GetString() == "Investimento").GetProperty("id").GetGuid();

        var resposta = await cliente.PutAsJsonAsync($"{Rota}/{id}", new { nome = "Investimento", tipo = "RECEITA" });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal("Categoria de investimento ou de dívidas continua sendo de despesa.",
            (await JsonAsync(resposta)).GetProperty("errors").GetProperty("tipo")[0].GetString());
        Assert.Equal("DESPESA", await EscalarAsync<string>(banco, $"SELECT tipo FROM lancamentos.categorias WHERE id = '{id}'"));
    }

    [Fact]
    public async Task Primeira_consulta_cria_as_categorias_padrao()
    {
        var (api, banco, cliente, _) = await IniciarAsync();
        await using var _ = api;

        var lista = await ListarAsync(cliente);

        Assert.Equal(12, lista.Length);
        Assert.Equal(10, lista.Count(c => c.GetProperty("tipo").GetString() == "DESPESA"));
        Assert.Equal(2, lista.Count(c => c.GetProperty("tipo").GetString() == "RECEITA"));
        var nomes = lista.Select(c => c.GetProperty("nome").GetString()).ToHashSet();
        Assert.Contains("Mercado", nomes);
        Assert.Contains("Dívidas e financiamentos", nomes);
        Assert.Contains("Salário", nomes);
        Assert.All(lista, c => Assert.False(c.GetProperty("arquivada").GetBoolean()));
        Assert.Equal(12, (await ListarAsync(cliente)).Length);
        Assert.Equal(12L, await EscalarAsync<long>(banco, "SELECT count(*) FROM lancamentos.categorias"));
    }

    [Fact]
    public async Task Consultas_simultaneas_nao_duplicam_padrao()
    {
        var (api, banco, cliente, _) = await IniciarAsync();
        await using var _ = api;

        var respostas = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => cliente.GetAsync(Rota)));

        Assert.All(respostas, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(12L, await EscalarAsync<long>(banco, "SELECT count(*) FROM lancamentos.categorias"));
    }

    [Fact]
    public async Task Nome_repetido_responde_409()
    {
        var (api, _, cliente, _) = await IniciarAsync();
        await using var _ = api;
        await ListarAsync(cliente);

        var criar = await cliente.PostAsJsonAsync(Rota, new { nome = "mercado", tipo = "DESPESA" });
        Assert.Equal(HttpStatusCode.Conflict, criar.StatusCode);

        var id = await CriarAsync(cliente, "Pets");
        var renomear = await cliente.PutAsJsonAsync($"{Rota}/{id}", new { nome = "MERCADO", tipo = "DESPESA" });
        Assert.Equal(HttpStatusCode.Conflict, renomear.StatusCode);
    }

    [Fact]
    public async Task Categoria_de_outro_casal_responde_404()
    {
        var (api, banco, cliente, _) = await IniciarAsync();
        await using var _ = api;
        var outroCasal = Guid.NewGuid();
        var categoria = Guid.NewGuid();
        await ExecutarAsync(banco, $"INSERT INTO identidade.casais (id, nome_familia) VALUES ('{outroCasal}', 'Outra')");
        await ExecutarAsync(banco, $"INSERT INTO lancamentos.categorias (id, casal_id, nome, tipo) VALUES ('{categoria}', '{outroCasal}', 'Alheia', 'DESPESA')");

        Assert.Equal(HttpStatusCode.NotFound, (await cliente.PutAsJsonAsync($"{Rota}/{categoria}", new { nome = "Minha", tipo = "DESPESA" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cliente.DeleteAsync($"{Rota}/{categoria}")).StatusCode);
        Assert.DoesNotContain(await ListarAsync(cliente), c => c.GetProperty("nome").GetString() == "Alheia");
    }

    [Fact]
    public async Task Arquivar_categoria_mantem_na_lista_com_flag()
    {
        var (api, banco, cliente, casal) = await IniciarAsync();
        await using var _ = api;
        var id = await CriarAsync(cliente, "Pets");
        await InserirLancamentoAsync(banco, casal, Guid.NewGuid(), id);

        var resposta = await cliente.PutAsJsonAsync($"{Rota}/{id}", new { nome = "Pets", tipo = "DESPESA", arquivada = true });

        Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);
        var pets = (await ListarAsync(cliente)).Single(c => c.GetProperty("nome").GetString() == "Pets");
        Assert.True(pets.GetProperty("arquivada").GetBoolean());
        Assert.False(await EscalarAsync<bool>(banco, $"SELECT is_deleted FROM lancamentos.categorias WHERE id = '{id}'"));

        var reativar = await cliente.PutAsJsonAsync($"{Rota}/{id}", new { nome = "Pets", tipo = "DESPESA" });
        Assert.Equal(HttpStatusCode.NoContent, reativar.StatusCode);
        Assert.True(await EscalarAsync<bool>(banco, $"SELECT arquivada FROM lancamentos.categorias WHERE id = '{id}'"));
    }

    [Fact]
    public async Task Apagar_categoria_sem_lancamentos_faz_soft_delete()
    {
        var (api, banco, cliente, _) = await IniciarAsync();
        await using var _ = api;
        var id = await CriarAsync(cliente, "Pets");

        Assert.Equal(HttpStatusCode.NoContent, (await cliente.DeleteAsync($"{Rota}/{id}")).StatusCode);

        Assert.DoesNotContain(await ListarAsync(cliente), c => c.GetProperty("nome").GetString() == "Pets");
        Assert.True(await EscalarAsync<bool>(banco, $"SELECT is_deleted FROM lancamentos.categorias WHERE id = '{id}'"));
    }

    [Fact]
    public async Task Apagar_categoria_com_lancamentos_responde_409()
    {
        var (api, banco, cliente, casal) = await IniciarAsync();
        await using var _ = api;
        var id = await CriarAsync(cliente, "Pets");
        await InserirLancamentoAsync(banco, casal, Guid.NewGuid(), id);

        var resposta = await cliente.DeleteAsync($"{Rota}/{id}");

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Equal("Categoria com lançamentos: arquive em vez de apagar.", (await JsonAsync(resposta)).GetProperty("title").GetString());
        Assert.False(await EscalarAsync<bool>(banco, $"SELECT is_deleted FROM lancamentos.categorias WHERE id = '{id}'"));
    }

    [Fact]
    public async Task Apagar_categoria_com_papel_responde_409()
    {
        var (api, banco, cliente, _) = await IniciarAsync();
        await using var _ = api;
        var id = (await ListarAsync(cliente)).Single(c => c.GetProperty("nome").GetString() == "Investimento").GetProperty("id").GetGuid();

        var resposta = await cliente.DeleteAsync($"{Rota}/{id}");

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Equal("Categoria de investimento ou de dívidas não pode ser apagada: arquive ou renomeie.",
            (await JsonAsync(resposta)).GetProperty("title").GetString());
        Assert.False(await EscalarAsync<bool>(banco, $"SELECT is_deleted FROM lancamentos.categorias WHERE id = '{id}'"));
    }

    [Fact]
    public async Task Tipo_invalido_ou_nome_fora_do_tamanho_responde_400()
    {
        var (api, _, cliente, _) = await IniciarAsync();
        await using var _ = api;

        Assert.Equal(HttpStatusCode.BadRequest, (await cliente.PostAsJsonAsync(Rota, new { nome = "X", tipo = "OUTRO" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await cliente.PostAsJsonAsync(Rota, new { nome = " ", tipo = "DESPESA" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await cliente.PostAsJsonAsync(Rota, new { nome = new string('a', 41), tipo = "DESPESA" })).StatusCode);
    }

    [Fact]
    public async Task Conta_com_lancamento_nao_pode_ser_apagada()
    {
        var (api, banco, cliente, casal) = await IniciarAsync();
        await using var _ = api;
        var conta = await cliente.PostAsJsonAsync("/api/v1/contas", new
        {
            tipo = "CORRENTE", apelido = "Itaú conjunta", saldoInicialCentavos = 0L, saldoInicialEm = "2026-11-01",
        });
        Assert.Equal(HttpStatusCode.Created, conta.StatusCode);
        var contaId = Guid.Parse(conta.Headers.Location!.ToString().Split('/').Last());
        await InserirLancamentoAsync(banco, casal, contaId, null);

        var resposta = await cliente.DeleteAsync($"/api/v1/contas/{contaId}");

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Equal("Conta com lançamentos: arquive em vez de apagar.", (await JsonAsync(resposta)).GetProperty("title").GetString());
        Assert.False(await EscalarAsync<bool>(banco, $"SELECT is_deleted FROM contas.contas WHERE id = '{contaId}'"));
    }
}
