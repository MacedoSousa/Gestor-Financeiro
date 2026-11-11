using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using GestorFinanceiro.Modules.Integracoes.Pluggy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GestorFinanceiro.UnitTests;

/// <summary>
/// Cliente da Pluggy contra respostas gravadas SINTÉTICAS, no formato medido em 11/11 (cursor do /v2/transactions,
/// 403 na listagem de items, 410 no endpoint antigo). Nenhum teste toca a rede.
/// </summary>
public class ClientePluggyTests
{
    private const string Chave = "chave-temporaria-sintetica";
    private static readonly CredenciaisPluggy Credenciais = new(1, "id-sintetico", "segredo-sintetico");

    /// <summary>Pluggy falsa: responde por rota e registra cada pedido (método, caminho, cabeçalho da chave, corpo).</summary>
    private sealed class PluggyFalsa : HttpMessageHandler
    {
        public readonly ConcurrentQueue<(string Metodo, string Caminho, string? Chave, string Corpo)> Pedidos = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } = _ => Json(200, "{}");
        public int Autenticacoes => Pedidos.Count(p => p.Caminho == "/auth");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage pedido, CancellationToken ct)
        {
            var corpo = pedido.Content is null ? "" : await pedido.Content.ReadAsStringAsync(ct);
            var chave = pedido.Headers.TryGetValues("X-API-KEY", out var v) ? v.Single() : null;
            Pedidos.Enqueue((pedido.Method.Method, pedido.RequestUri!.PathAndQuery, chave, corpo));
            if (pedido.RequestUri.AbsolutePath == "/auth" && pedido.Method == HttpMethod.Post)
                return Json(200, $$"""{"apiKey":"{{Chave}}"}""");
            return Responder(pedido);
        }
    }

    private sealed class LogDeTeste : ILogger<ClientePluggy>
    {
        public readonly ConcurrentQueue<string> Linhas = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel nivel) => true;
        public void Log<TState>(LogLevel nivel, EventId id, TState estado, Exception? e, Func<TState, Exception?, string> formatar) =>
            Linhas.Enqueue(formatar(estado, e) + (e is null ? "" : " " + e));
    }

    private static HttpResponseMessage Json(int status, string corpo, TimeSpan? retryAfter = null)
    {
        var r = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(corpo, Encoding.UTF8, "application/json") };
        if (retryAfter is { } ra) r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(ra);
        return r;
    }

    private sealed record Montagem(ClientePluggy Cliente, PluggyFalsa Pluggy, RelogioFixo Relogio, LogDeTeste Log, List<TimeSpan> Esperas);

    private static Montagem Montar(CacheDeChavesDaPluggy? cache = null)
    {
        var pluggy = new PluggyFalsa();
        var relogio = new RelogioFixo();
        var log = new LogDeTeste();
        var esperas = new List<TimeSpan>();
        var http = new HttpClient(pluggy) { BaseAddress = new Uri("https://pluggy.invalid") };
        var cliente = new ClientePluggy(http, cache ?? new CacheDeChavesDaPluggy(), relogio, log,
            new EsperaDeTeste(esperas));
        return new Montagem(cliente, pluggy, relogio, log, esperas);
    }

    private sealed class EsperaDeTeste(List<TimeSpan> esperas) : IEsperaDaPluggy
    {
        public Task EsperarAsync(TimeSpan tempo, CancellationToken ct) { esperas.Add(tempo); return Task.CompletedTask; }
    }

    private const string ItemJson = """
        {"id":"item-sintetico","status":"UPDATED","executionStatus":"SUCCESS","connector":{"id":600,"name":"MeuPluggy"},
         "consentExpiresAt":"2027-11-11T00:00:00.000Z","lastUpdatedAt":"2026-11-10T09:00:00.000Z"}
        """;

    [Fact]
    public async Task Auth_manda_credenciais_no_corpo_e_guarda_a_chave_so_em_memoria()
    {
        var m = Montar();
        m.Pluggy.Responder = _ => Json(200, ItemJson);

        await m.Cliente.ObterItemAsync(Credenciais, "item-sintetico", default);
        await m.Cliente.ObterItemAsync(Credenciais, "item-sintetico", default);

        Assert.Equal(1, m.Pluggy.Autenticacoes);
        var auth = m.Pluggy.Pedidos.Single(p => p.Caminho == "/auth");
        Assert.Equal("POST", auth.Metodo);
        using var corpo = JsonDocument.Parse(auth.Corpo);
        Assert.Equal("id-sintetico", corpo.RootElement.GetProperty("clientId").GetString());
        Assert.Equal("segredo-sintetico", corpo.RootElement.GetProperty("clientSecret").GetString());
        Assert.All(m.Pluggy.Pedidos.Where(p => p.Caminho != "/auth"), p => Assert.Equal(Chave, p.Chave));
        // Nada público guarda a chave: nem o cliente nem o cache expõem propriedades.
        Assert.Empty(typeof(ClientePluggy).GetProperties(BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(typeof(CacheDeChavesDaPluggy).GetProperties(BindingFlags.Public | BindingFlags.Instance));
        Assert.DoesNotContain(m.Log.Linhas, l => l.Contains(Chave, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Chave_expirada_reautentica()
    {
        var m = Montar();
        m.Pluggy.Responder = _ => Json(200, ItemJson);

        await m.Cliente.ObterItemAsync(Credenciais, "item-sintetico", default);
        m.Relogio.Agora += TimeSpan.FromHours(2);
        await m.Cliente.ObterItemAsync(Credenciais, "item-sintetico", default);

        Assert.Equal(2, m.Pluggy.Autenticacoes);
    }

    [Fact]
    public async Task Chave_recusada_reautentica_uma_vez()
    {
        var m = Montar();
        var recusas = 0;
        m.Pluggy.Responder = _ => recusas++ == 0 ? Json(401, """{"code":401,"message":"expirada"}""") : Json(200, ItemJson);

        var item = await m.Cliente.ObterItemAsync(Credenciais, "item-sintetico", default);

        Assert.Equal("item-sintetico", item.Id);
        Assert.Equal(2, m.Pluggy.Autenticacoes);
    }

    [Fact]
    public async Task Item_traz_estado_e_consentimento()
    {
        var m = Montar();
        m.Pluggy.Responder = _ => Json(200, ItemJson);

        var item = await m.Cliente.ObterItemAsync(Credenciais, "item-sintetico", default);

        Assert.Equal(new ItemPluggy("item-sintetico", "UPDATED", "SUCCESS",
            DateTimeOffset.Parse("2027-11-11T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-11-10T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture)), item);
        Assert.Equal("GET", m.Pluggy.Pedidos.Last().Metodo);
        Assert.Equal("/items/item-sintetico", m.Pluggy.Pedidos.Last().Caminho);
    }

    [Fact]
    public async Task Consentimento_vencido_vem_no_item()
    {
        var m = Montar();
        m.Pluggy.Responder = _ => Json(200, ItemJson.Replace("2027-11-11", "2026-01-01", StringComparison.Ordinal));

        var item = await m.Cliente.ObterItemAsync(Credenciais, "item-sintetico", default);

        Assert.True(item.ConsentimentoVenceEm < m.Relogio.Agora);
    }

    [Fact]
    public async Task Contas_mapeiam_saldo_limite_agencia_e_final_do_cartao()
    {
        var m = Montar();
        m.Pluggy.Responder = _ => Json(200, """
            {"results":[
              {"id":"k1","type":"BANK","subtype":"CHECKING_ACCOUNT","name":"Conta Sintética","number":"99999-9","balance":12345.67,
               "bankData":{"transferNumber":"0001/99999-9"}},
              {"id":"c1","type":"CREDIT","subtype":"CREDIT_CARD","name":"Cartão Sintético","number":"5555 4444 3333 4321","balance":1500.1,
               "creditData":{"creditLimit":8000,"holder":"PORTADOR SINTETICO"}}
            ]}
            """);

        var contas = await m.Cliente.ListarContasAsync(Credenciais, "item-sintetico", default);

        Assert.Equal("/accounts?itemId=item-sintetico", m.Pluggy.Pedidos.Last().Caminho);
        Assert.Equal(new ContaPluggy("k1", "BANK", "CHECKING_ACCOUNT", "Conta Sintética", "99999-9", "0001/99999-9", null,
            1234567, null, null, null), contas[0]);
        Assert.Equal(new ContaPluggy("c1", "CREDIT", "CREDIT_CARD", "Cartão Sintético", "5555 4444 3333 4321", null, null,
            150010, 800000, "PORTADOR SINTETICO", "4321"), contas[1]);
    }

    [Fact]
    public async Task Transacoes_seguem_o_cursor_after()
    {
        var m = Montar();
        m.Pluggy.Responder = p =>
        {
            var q = p.RequestUri!.Query;
            var t = (string id, string data, string valor) =>
                $$"""{"id":"{{id}}","date":"{{data}}","amount":{{valor}},"type":"DEBIT","status":"POSTED","description":"Compra sintética","categoryId":"01000000","category":"Groceries"}""";
            if (q.Contains("after=p3", StringComparison.Ordinal))
                return Json(200, $$"""{"results":[{{t("t5", "2026-03-01T03:00:00.000Z", "-5")}}],"next":null}""");
            if (q.Contains("after=p2", StringComparison.Ordinal))
                return Json(200, $$"""{"results":[{{t("t3", "2026-02-01T03:00:00.000Z", "-3")}},{{t("t4", "2026-02-02T03:00:00.000Z", "-4")}}],"next":"https://api.pluggy.ai/v2/transactions?accountId=k1&after=p3"}""");
            return Json(200, $$"""{"results":[{{t("t1", "2026-01-01T03:00:00.000Z", "-1")}},{{t("t2", "2026-01-02T03:00:00.000Z", "-2.5")}}],"next":"/v2/transactions?accountId=k1&dateFrom=2026-01-01&after=p2"}""");
        };

        var lidas = new List<TransacaoPluggy>();
        await foreach (var t in m.Cliente.ListarTransacoesAsync(Credenciais, "k1", new DateOnly(2026, 1, 1), default))
            lidas.Add(t);

        Assert.Equal(["t1", "t2", "t3", "t4", "t5"], lidas.Select(t => t.Id));
        Assert.Equal(-250, lidas[1].ValorCentavos);
        var caminhos = m.Pluggy.Pedidos.Where(p => p.Caminho != "/auth").Select(p => p.Caminho).ToList();
        Assert.Equal("/v2/transactions?accountId=k1&dateFrom=2026-01-01", caminhos[0]);
        Assert.Equal("/v2/transactions?accountId=k1&dateFrom=2026-01-01&after=p2", caminhos[1]);
        Assert.Equal("/v2/transactions?accountId=k1&dateFrom=2026-01-01&after=p3", caminhos[2]);
        Assert.DoesNotContain(m.Pluggy.Pedidos, p => p.Caminho.StartsWith("/transactions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Transacao_mapeia_data_parcela_fatura_e_categoria()
    {
        var m = Montar();
        m.Pluggy.Responder = _ => Json(200, """
            {"results":[{"id":"c-5","date":"2026-02-01T02:30:00.000Z","amount":300,"type":"DEBIT","status":"PENDING",
              "description":null,"categoryId":"05100000","category":"Credit card payment",
              "creditCardMetadata":{"installmentNumber":2,"totalInstallments":3,"billId":"b1"}}],"next":null}
            """);

        var t = await m.Cliente.ListarTransacoesAsync(Credenciais, "c1", new DateOnly(2026, 1, 1), default).SingleAsync();

        Assert.Equal(new TransacaoPluggy("c-5", new DateOnly(2026, 1, 31), 30000, "DEBIT", "PENDING", null,
            "05100000", "Credit card payment", 2, 3, "b1"), t);
    }

    [Fact]
    public async Task Faturas_e_investimentos()
    {
        var m = Montar();
        m.Pluggy.Responder = p => p.RequestUri!.AbsolutePath == "/bills"
            ? Json(200, """{"results":[{"id":"b1","dueDate":"2026-10-10T00:00:00.000Z","totalAmount":500.5}]}""")
            : Json(200, """{"results":[{"id":"i1","type":"FIXED_INCOME","subtype":"CDB","name":"CDB Sintético","balance":10000.01}]}""");

        var faturas = await m.Cliente.ListarFaturasAsync(Credenciais, "c1", default);
        var investimentos = await m.Cliente.ListarInvestimentosAsync(Credenciais, "item-sintetico", default);

        // Vencimento é dia do calendário: meia-noite UTC não recua para o dia anterior em São Paulo.
        Assert.Equal(new FaturaPluggy("b1", new DateOnly(2026, 10, 10), null, 50050), faturas.Single());
        Assert.Equal(new InvestimentoPluggy("i1", "FIXED_INCOME", "CDB", "CDB Sintético", 1000001), investimentos.Single());
        Assert.Contains(m.Pluggy.Pedidos, p => p.Caminho == "/bills?accountId=c1");
        Assert.Contains(m.Pluggy.Pedidos, p => p.Caminho == "/investments?itemId=item-sintetico");
    }

    [Fact]
    public async Task Erro_401_vira_ErroDaPluggy_sem_corpo()
    {
        var m = Montar();
        m.Pluggy.Responder = _ => Json(401, """{"code":"INVALID_CREDENTIALS","message":"segredo-sintetico vazou no corpo"}""");

        var erro = await Assert.ThrowsAsync<ErroDaPluggy>(() => m.Cliente.ObterItemAsync(Credenciais, "item-sintetico", default));

        Assert.Equal(401, erro.Status);
        Assert.Equal("INVALID_CREDENTIALS", erro.Codigo);
        Assert.DoesNotContain("segredo-sintetico", erro.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("vazou", erro.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Log_so_tem_status_e_codigo()
    {
        var m = Montar();
        m.Pluggy.Responder = _ => Json(403, """{"code":403,"message":"corpo sintético com item-sintetico e segredo-sintetico"}""");

        await Assert.ThrowsAsync<ErroDaPluggy>(() => m.Cliente.ObterItemAsync(Credenciais, "item-sintetico", default));

        Assert.Contains(m.Log.Linhas, l => l.Contains("403", StringComparison.Ordinal) && l.Contains("/items/{id}", StringComparison.Ordinal));
        Assert.All(m.Log.Linhas, l =>
        {
            Assert.DoesNotContain("corpo sintético", l, StringComparison.Ordinal);
            Assert.DoesNotContain("item-sintetico", l, StringComparison.Ordinal);
            Assert.DoesNotContain("segredo-sintetico", l, StringComparison.Ordinal);
            Assert.DoesNotContain(Chave, l, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Erro_429_respeita_Retry_After_ate_3_vezes()
    {
        var m = Montar();
        var chamadas = 0;
        m.Pluggy.Responder = _ => ++chamadas <= 2 ? Json(429, "{}", TimeSpan.FromSeconds(1)) : Json(200, ItemJson);

        await m.Cliente.ObterItemAsync(Credenciais, "item-sintetico", default);

        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)], m.Esperas);
    }

    [Fact]
    public async Task Quatro_429_seguidos_desistem()
    {
        var m = Montar();
        m.Pluggy.Responder = _ => Json(429, "{}", TimeSpan.FromSeconds(120));

        var erro = await Assert.ThrowsAsync<ErroDaPluggy>(() => m.Cliente.ObterItemAsync(Credenciais, "item-sintetico", default));

        Assert.Equal(429, erro.Status);
        Assert.Equal(3, m.Esperas.Count);
        Assert.All(m.Esperas, e => Assert.Equal(TimeSpan.FromSeconds(60), e)); // teto de 60 s por espera
        Assert.Equal(TimeSpan.FromSeconds(120), erro.TentarDepois);
    }

    [Fact]
    public async Task Endpoint_antigo_410_vira_ErroDaPluggy()
    {
        var m = Montar();
        m.Pluggy.Responder = _ => Json(410, """{"message":"gone"}""");

        var erro = await Assert.ThrowsAsync<ErroDaPluggy>(async () =>
        {
            await foreach (var _ in m.Cliente.ListarTransacoesAsync(Credenciais, "k1", new DateOnly(2026, 1, 1), default)) { }
        });

        Assert.Equal(410, erro.Status);
        Assert.Null(erro.Codigo);
    }

    [Fact]
    public async Task Cache_e_por_slot()
    {
        var cache = new CacheDeChavesDaPluggy();
        var m = Montar(cache);
        m.Pluggy.Responder = _ => Json(200, ItemJson);

        await m.Cliente.ObterItemAsync(Credenciais, "item-sintetico", default);
        await m.Cliente.ObterItemAsync(Credenciais with { Slot = 2, ClientId = "outro-id" }, "item-sintetico", default);

        Assert.Equal(2, m.Pluggy.Autenticacoes);
    }

    [Fact]
    public async Task Resposta_sem_results_vira_lista_vazia()
    {
        var m = Montar();
        m.Pluggy.Responder = _ => Json(200, "{}");

        Assert.Empty(await m.Cliente.ListarContasAsync(Credenciais, "item-sintetico", default));
        Assert.Empty(await m.Cliente.ListarInvestimentosAsync(Credenciais, "item-sintetico", default));
    }

    // ---------------------------------------------------------------- segredos

    private static SegredosDaPluggy Segredos(string pasta) =>
        new(Options.Create(new OpcoesDaPluggy { PastaDeSegredos = pasta }));

    [Fact]
    public void Segredo_vazio_e_slot_nao_configurado()
    {
        var pasta = Directory.CreateTempSubdirectory("gf-pluggy-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(pasta, "pluggy_1_client_id"), "");
            File.WriteAllText(Path.Combine(pasta, "pluggy_1_client_secret"), "");

            Assert.Null(Segredos(pasta).Ler(1));
            Assert.Null(Segredos(pasta).Ler(2)); // arquivos ausentes
        }
        finally { Directory.Delete(pasta, recursive: true); }
    }

    [Fact]
    public void Segredo_preenchido_e_lido_sem_a_quebra_de_linha()
    {
        var pasta = Directory.CreateTempSubdirectory("gf-pluggy-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(pasta, "pluggy_2_client_id"), "id-sintetico\n");
            File.WriteAllText(Path.Combine(pasta, "pluggy_2_client_secret"), "segredo-sintetico\n");

            Assert.Equal(new CredenciaisPluggy(2, "id-sintetico", "segredo-sintetico"), Segredos(pasta).Ler(2));
            Assert.Throws<ArgumentOutOfRangeException>(() => Segredos(pasta).Ler(3));
        }
        finally { Directory.Delete(pasta, recursive: true); }
    }

    [Fact]
    public void Credenciais_nao_aparecem_no_ToString()
    {
        Assert.DoesNotContain("segredo-sintetico", Credenciais.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("id-sintetico", Credenciais.ToString(), StringComparison.Ordinal);
    }
}
