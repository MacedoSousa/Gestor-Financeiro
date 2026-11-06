using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GestorFinanceiro.IntegrationTests.Infra;
using GestorFinanceiro.Modules.Contas.Contracts;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.UnitTests;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GestorFinanceiro.IntegrationTests;

/// <summary>Contas e cartões: criação, máscara dos dados bancários, titular, apelido único e arquivar/apagar.</summary>
[Collection("postgres")]
public class ContasTests(PostgresFixture pg)
{
    private const string Rota = "/api/v1/contas";
    private const string SqlAna = "SELECT id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'";
    private const string SqlBruno = "SELECT id FROM identidade.usuarios WHERE nome_completo = 'Bruno Teste'";
    private const string AgenciaEmClaro = "1234";
    private const string NumeroEmClaro = "98765-7";

    private static async Task CadastrarAsync(ApiDeTeste api)
    {
        var pedido = new CadastroDoCasalDto(
            api.CodigoInstalacao, "Família Teste",
            new ConjugeNovoDto("Ana Teste", CpfSintetico.Gerar(1), "senha-da-ana-123"),
            new ConjugeNovoDto("Bruno Teste", CpfSintetico.Gerar(2), "senha-do-bruno-1"));
        var resposta = await api.CreateClient().PostAsJsonAsync("/api/v1/casal", pedido);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
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

    private static object Corrente(string apelido, Guid? titular = null, string? agencia = null, string? numero = null) => new
    {
        tipo = "CORRENTE", apelido, instituicao = "Itaú", titularUsuarioId = titular,
        agencia, numero, saldoInicialCentavos = 150_000L, saldoInicialEm = "2026-11-01",
    };

    private static object Cartao(string apelido, Guid? titular, int? fechamento = 5, int? vencimento = 12) => new
    {
        tipo = "CARTAO", apelido, instituicao = "Nubank", titularUsuarioId = titular,
        saldoInicialCentavos = 0L, saldoInicialEm = "2026-11-01",
        diaFechamento = fechamento, diaVencimento = vencimento, limiteCentavos = 800_000L,
    };

    private static async Task<Guid> CriarAsync(HttpClient cliente, object corpo)
    {
        var resposta = await cliente.PostAsJsonAsync(Rota, corpo);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return Guid.Parse(resposta.Headers.Location!.ToString().Split('/').Last());
    }

    private async Task<(ApiDeTeste Api, BancoDeTeste Banco, HttpClient Cliente, Guid Ana, Guid Bruno)> IniciarAsync(Action<IServiceCollection>? servicos = null)
    {
        var banco = await pg.NovoBancoAsync();
        var api = new ApiDeTeste(banco, servicos: servicos);
        await CadastrarAsync(api);
        var ana = await EscalarAsync<Guid>(banco, SqlAna);
        var bruno = await EscalarAsync<Guid>(banco, SqlBruno);
        return (api, banco, await api.ClienteLogadoAsync(ana), ana, bruno);
    }

    [Fact]
    public async Task Cria_conta_conjunta_e_cartao()
    {
        var (api, _, cliente, ana, bruno) = await IniciarAsync();
        await using var _ = api;

        await CriarAsync(cliente, Corrente("Itaú conjunta"));
        await CriarAsync(cliente, Cartao("Nubank do Bruno", bruno));

        var lista = (await JsonAsync(await cliente.GetAsync(Rota))).EnumerateArray().ToArray();
        Assert.Equal(2, lista.Length);
        var conjunta = lista.Single(c => c.GetProperty("apelido").GetString() == "Itaú conjunta");
        Assert.Equal(JsonValueKind.Null, conjunta.GetProperty("titular").ValueKind);
        Assert.Equal("CORRENTE", conjunta.GetProperty("tipo").GetString());
        Assert.Equal(150_000L, conjunta.GetProperty("saldoInicialCentavos").GetInt64());
        var cartao = lista.Single(c => c.GetProperty("tipo").GetString() == "CARTAO");
        Assert.Equal(bruno, cartao.GetProperty("titular").GetProperty("usuarioId").GetGuid());
        Assert.Equal("Bruno", cartao.GetProperty("titular").GetProperty("apelido").GetString());
        Assert.Equal(5, cartao.GetProperty("diaFechamento").GetInt32());
        Assert.Equal(12, cartao.GetProperty("diaVencimento").GetInt32());
        Assert.Equal(800_000L, cartao.GetProperty("limiteCentavos").GetInt64());
        Assert.NotEqual(ana, bruno);
    }

    [Fact]
    public async Task Numero_e_agencia_so_saem_mascarados_e_ficam_cifrados_no_banco()
    {
        var (api, banco, cliente, _, _) = await IniciarAsync();
        await using var _ = api;

        var id = await CriarAsync(cliente, Corrente("Itaú conjunta", agencia: AgenciaEmClaro, numero: NumeroEmClaro));

        var resposta = await cliente.GetAsync(Rota);
        var texto = await resposta.Content.ReadAsStringAsync();
        Assert.DoesNotContain(AgenciaEmClaro, texto);
        Assert.DoesNotContain("98765", texto);
        var conta = JsonDocument.Parse(texto).RootElement.EnumerateArray().Single();
        Assert.Equal("••••", conta.GetProperty("agenciaMascarada").GetString());
        Assert.Equal("••••-7", conta.GetProperty("numeroMascarado").GetString());

        var bytes = await EscalarAsync<byte[]>(banco, $"SELECT numero_cifrado FROM contas.contas WHERE id = '{id}'");
        Assert.NotEmpty(bytes);
        Assert.DoesNotContain("98765", System.Text.Encoding.UTF8.GetString(bytes));
        var agenciaBytes = await EscalarAsync<byte[]>(banco, $"SELECT agencia_cifrada FROM contas.contas WHERE id = '{id}'");
        Assert.DoesNotContain(AgenciaEmClaro, System.Text.Encoding.UTF8.GetString(agenciaBytes));
    }

    [Fact]
    public async Task Atualizar_sem_reenviar_numero_mantem_o_numero_cifrado()
    {
        var (api, banco, cliente, _, _) = await IniciarAsync();
        await using var _ = api;
        var id = await CriarAsync(cliente, Corrente("Itaú conjunta", agencia: AgenciaEmClaro, numero: NumeroEmClaro));
        var antes = await EscalarAsync<byte[]>(banco, $"SELECT numero_cifrado FROM contas.contas WHERE id = '{id}'");

        var resposta = await cliente.PutAsJsonAsync($"{Rota}/{id}", Corrente("Itaú renomeada"));

        Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);
        Assert.Equal(antes, await EscalarAsync<byte[]>(banco, $"SELECT numero_cifrado FROM contas.contas WHERE id = '{id}'"));
        Assert.Equal("Itaú renomeada", await EscalarAsync<string>(banco, $"SELECT apelido FROM contas.contas WHERE id = '{id}'"));
    }

    [Fact]
    public async Task Cartao_sem_dias_responde_400()
    {
        var (api, _, cliente, ana, _) = await IniciarAsync();
        await using var _ = api;

        var semDias = await cliente.PostAsJsonAsync(Rota, Cartao("Cartão", ana, fechamento: null, vencimento: null));
        var diaInvalido = await cliente.PostAsJsonAsync(Rota, Cartao("Cartão", ana, fechamento: 32, vencimento: 0));
        var corrente = await cliente.PostAsJsonAsync(Rota, new
        {
            tipo = "CORRENTE", apelido = "Com dias", saldoInicialCentavos = 0L, saldoInicialEm = "2026-11-01", diaFechamento = 5, diaVencimento = 12,
        });

        Assert.Equal(HttpStatusCode.BadRequest, semDias.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, diaInvalido.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, corrente.StatusCode);
    }

    [Fact]
    public async Task Titular_de_outro_casal_responde_400()
    {
        var (api, banco, cliente, _, _) = await IniciarAsync();
        await using var _ = api;
        var casalB = Guid.NewGuid();
        var estranho = Guid.NewGuid();
        await ExecutarAsync(banco, $"INSERT INTO identidade.casais (id, nome_familia) VALUES ('{casalB}', 'Outra')");
        await ExecutarAsync(banco,
            $"INSERT INTO identidade.usuarios (id, casal_id, nome_completo, cpf_cifrado, cpf_hmac) VALUES ('{estranho}', '{casalB}', 'Carla Outra', '\\x01', '\\x02')");

        var resposta = await cliente.PostAsJsonAsync(Rota, Corrente("Conta do estranho", titular: estranho));
        var inexistente = await cliente.PostAsJsonAsync(Rota, Corrente("Conta do fantasma", titular: Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, inexistente.StatusCode);
        Assert.Equal(0L, await EscalarAsync<long>(banco, "SELECT count(*) FROM contas.contas"));
    }

    [Fact]
    public async Task Apelido_repetido_responde_409()
    {
        var (api, _, cliente, _, _) = await IniciarAsync();
        await using var _ = api;
        await CriarAsync(cliente, Corrente("Itaú conjunta"));

        var resposta = await cliente.PostAsJsonAsync(Rota, Corrente("ITAÚ CONJUNTA"));

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Equal("Já existe uma conta com esse nome.", await TituloAsync(resposta));
    }

    [Fact]
    public async Task Conta_de_outro_casal_responde_404()
    {
        var (api, banco, cliente, _, _) = await IniciarAsync();
        await using var _ = api;
        var casalB = Guid.NewGuid();
        var contaB = Guid.NewGuid();
        await ExecutarAsync(banco, $"INSERT INTO identidade.casais (id, nome_familia) VALUES ('{casalB}', 'Outra')");
        await ExecutarAsync(banco,
            $"INSERT INTO contas.contas (id, casal_id, tipo, apelido, saldo_inicial_em) VALUES ('{contaB}', '{casalB}', 'CORRENTE', 'Alheia', '2026-11-01')");

        Assert.Equal(HttpStatusCode.NotFound, (await cliente.PutAsJsonAsync($"{Rota}/{contaB}", Corrente("Invadida"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cliente.DeleteAsync($"{Rota}/{contaB}")).StatusCode);
        Assert.Empty((await JsonAsync(await cliente.GetAsync(Rota))).EnumerateArray());
        Assert.Equal("Alheia", await EscalarAsync<string>(banco, $"SELECT apelido FROM contas.contas WHERE id = '{contaB}'"));
    }

    [Fact]
    public async Task Apagar_conta_e_soft_delete()
    {
        var (api, banco, cliente, _, _) = await IniciarAsync();
        await using var _ = api;
        var id = await CriarAsync(cliente, Corrente("Itaú conjunta"));

        Assert.Equal(HttpStatusCode.NoContent, (await cliente.DeleteAsync($"{Rota}/{id}")).StatusCode);

        Assert.True(await EscalarAsync<bool>(banco, $"SELECT is_deleted FROM contas.contas WHERE id = '{id}'"));
        Assert.Empty((await JsonAsync(await cliente.GetAsync(Rota))).EnumerateArray());
        // O apelido volta a ficar livre (índice parcial).
        await CriarAsync(cliente, Corrente("Itaú conjunta"));
    }

    [Fact]
    public async Task Conta_arquivada_continua_listada()
    {
        var (api, _, cliente, _, _) = await IniciarAsync();
        await using var _ = api;
        var id = await CriarAsync(cliente, Corrente("Itaú conjunta"));

        var corpo = new
        {
            tipo = "CORRENTE", apelido = "Itaú conjunta", saldoInicialCentavos = 150_000L, saldoInicialEm = "2026-11-01", arquivada = true,
        };
        Assert.Equal(HttpStatusCode.NoContent, (await cliente.PutAsJsonAsync($"{Rota}/{id}", corpo)).StatusCode);

        var conta = (await JsonAsync(await cliente.GetAsync(Rota))).EnumerateArray().Single();
        Assert.True(conta.GetProperty("arquivada").GetBoolean());
    }

    [Fact]
    public async Task Apagar_conta_em_uso_responde_409()
    {
        var verificador = new VerificadorFalso();
        var (api, banco, cliente, _, _) = await IniciarAsync(s => s.AddSingleton<IVerificadorDeUsoDaConta>(verificador));
        await using var _ = api;
        var id = await CriarAsync(cliente, Corrente("Itaú conjunta"));
        verificador.EmUso.Add(id);

        var resposta = await cliente.DeleteAsync($"{Rota}/{id}");

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Equal("Conta com lançamentos: arquive em vez de apagar.", await TituloAsync(resposta));
        Assert.False(await EscalarAsync<bool>(banco, $"SELECT is_deleted FROM contas.contas WHERE id = '{id}'"));
    }

    [Fact]
    public async Task Atualizar_sem_arquivada_mantem_o_estado()
    {
        var (api, _, cliente, _, _) = await IniciarAsync();
        await using var _ = api;
        var id = await CriarAsync(cliente, Corrente("Itaú conjunta"));
        var arquivar = new { tipo = "CORRENTE", apelido = "Itaú conjunta", saldoInicialCentavos = 150_000L, saldoInicialEm = "2026-11-01", arquivada = true };
        Assert.Equal(HttpStatusCode.NoContent, (await cliente.PutAsJsonAsync($"{Rota}/{id}", arquivar)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await cliente.PutAsJsonAsync($"{Rota}/{id}", Corrente("Itaú renomeada"))).StatusCode);

        var conta = (await JsonAsync(await cliente.GetAsync(Rota))).EnumerateArray().Single();
        Assert.Equal("Itaú renomeada", conta.GetProperty("apelido").GetString());
        Assert.True(conta.GetProperty("arquivada").GetBoolean());
    }

    [Fact]
    public async Task Data_do_saldo_inicial_e_obrigatoria_e_nao_pode_ser_futura()
    {
        var (api, _, cliente, _, _) = await IniciarAsync();
        await using var _ = api;

        var semData = await cliente.PostAsJsonAsync(Rota, new { tipo = "CORRENTE", apelido = "Sem data", saldoInicialCentavos = 0L });
        var futura = await cliente.PostAsJsonAsync(Rota, new { tipo = "CORRENTE", apelido = "Futura", saldoInicialCentavos = 0L, saldoInicialEm = "2999-01-01" });

        Assert.Equal(HttpStatusCode.BadRequest, semData.StatusCode);
        Assert.Equal("Informe a data do saldo inicial.", (await JsonAsync(semData)).GetProperty("errors").GetProperty("saldoInicialEm")[0].GetString());
        Assert.Equal(HttpStatusCode.BadRequest, futura.StatusCode);
        Assert.Equal("A data do saldo inicial não pode ser no futuro.", (await JsonAsync(futura)).GetProperty("errors").GetProperty("saldoInicialEm")[0].GetString());
    }

    [Fact]
    public async Task Tipo_nao_muda_no_put()
    {
        var (api, banco, cliente, _, _) = await IniciarAsync();
        await using var _ = api;
        var id = await CriarAsync(cliente, Corrente("Itaú conjunta"));

        var resposta = await cliente.PutAsJsonAsync($"{Rota}/{id}", new { tipo = "POUPANCA", apelido = "Itaú conjunta", saldoInicialCentavos = 0L, saldoInicialEm = "2026-11-01" });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal("CORRENTE", await EscalarAsync<string>(banco, $"SELECT tipo FROM contas.contas WHERE id = '{id}'"));
    }

    [Fact]
    public async Task Sem_sessao_responde_401()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync(Rota)).StatusCode);
    }

    private sealed class VerificadorFalso : IVerificadorDeUsoDaConta
    {
        public HashSet<Guid> EmUso { get; } = [];
        public Task<bool> EstaEmUsoAsync(Guid contaId, CancellationToken ct) => Task.FromResult(EmUso.Contains(contaId));
    }
}
