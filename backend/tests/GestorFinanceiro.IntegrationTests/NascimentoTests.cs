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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        new("cartao-sintetico-1", "CREDIT", "CREDIT_CARD", "Cartão Platinum", null, null, 80_000, 500_000, "ANA TESTE", "1234");

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
    private static async Task<(Guid ItemId, string ItemPluggy)> ConectarAsync(Ctx c, HttpClient cliente, int slot, string banco = "Itaú")
    {
        var itemPluggy = $"item-sintetico-{Guid.NewGuid():N}";
        c.Pluggy.Cadastrar(slot == 1 ? IdSlot1 : IdSlot2, itemPluggy);
        var r = await cliente.PostAsJsonAsync(RotaConexoes, new { slot, itemId = itemPluggy, banco });
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
    private static async Task VincularAsync(Ctx c, Guid item, string tipo, ContaPluggy conta)
    {
        await using var escopo = c.Api.Services.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<IntegracoesDbContext>();
        var cofre = escopo.ServiceProvider.GetRequiredService<ICofre>();
        var casal = await EscalarAsync<Guid>(c.Banco, $"SELECT casal_id FROM integracoes.items WHERE id = '{item}'");
        db.Vinculos.Add(Vinculo.Criar(casal, item, tipo, conta.Id, Guid.CreateVersion7(), Vinculo.IdentidadeDe(conta, cofre)));
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
    public async Task Nome_do_banco_diferente_nas_duas_conexoes_ainda_trava()
    {
        await using var c = await IniciarAsync();
        var (itemDaAna, _) = await ConectarAsync(c, c.Ana, 1, "Itaú");
        await VincularAsync(c, itemDaAna, "CONTA", Corrente);
        var (itemDoBruno, pluggyDoBruno) = await ConectarAsync(c, c.Bruno, 2, "itau unibanco");
        c.Pluggy.CadastrarConta(pluggyDoBruno, Corrente with { Id = "conta-sintetica-do-bruno", AgenciaEConta = "0001 12345 6" });

        var conta = ContaEncontrada(await DescobertaAsync(c.Bruno, itemDoBruno), "conta-sintetica-do-bruno");

        Assert.Equal("Ana", conta.GetProperty("jaVemDe").GetString());
        Assert.Equal("itau unibanco Corrente · Bruno", conta.GetProperty("apelidoSugerido").GetString());
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

        var doBruno = await c.Bruno.GetAsync(RotaDescoberta(item));
        var daAna = await c.Ana.GetStringAsync(RotaDescoberta(item));

        Assert.Equal(HttpStatusCode.Forbidden, doBruno.StatusCode);
        Assert.DoesNotContain(itemPluggy, daAna, StringComparison.Ordinal);
        Assert.DoesNotContain("12345", daAna, StringComparison.Ordinal);
    }
}
