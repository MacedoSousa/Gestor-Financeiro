using System.Collections.Concurrent;
using System.Net;
using System.Text;
using GestorFinanceiro.Modules.Integracoes;
using GestorFinanceiro.Modules.Integracoes.Pluggy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace GestorFinanceiro.UnitTests;

/// <summary>
/// Regra 5 e D7 (achado da revisão do CEO no PR #32): com o cliente montado pelo próprio AddIntegracoes, nenhuma linha
/// de log, de nenhuma categoria e em nenhum nível, contém o id do item nem o da conta. O log padrão do
/// HttpClientFactory ("Start processing HTTP request GET &lt;uri&gt;") gravaria a URL com os ids.
/// </summary>
public class LogDaPluggyTests
{
    private const string ItemId = "item-sintetico-7f3a";
    private const string ContaId = "conta-sintetica-91bc";

    private sealed class TodoLog : ILoggerProvider
    {
        public readonly ConcurrentQueue<string> Linhas = new();
        public ILogger CreateLogger(string categoria) => new Registro(this, categoria);
        public void Dispose() { }

        private sealed class Registro(TodoLog dono, string categoria) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                dono.Linhas.Enqueue($"{categoria} [escopo] {state}");
                return null;
            }

            public bool IsEnabled(LogLevel nivel) => true;

            public void Log<TState>(LogLevel nivel, EventId id, TState estado, Exception? e, Func<TState, Exception?, string> formatar) =>
                dono.Linhas.Enqueue($"{categoria} [{nivel}] {formatar(estado, e)} {estado} {e}");
        }
    }

    private sealed class PluggyFalsa : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage pedido, CancellationToken ct)
        {
            var corpo = pedido.RequestUri!.AbsolutePath switch
            {
                "/auth" => """{"apiKey":"chave-temporaria-sintetica"}""",
                "/bills" => """{"results":[]}""",
                "/accounts" => $$"""{"results":[{"id":"{{ContaId}}","type":"BANK","subtype":"CHECKING_ACCOUNT","name":"Conta Sintética","balance":1}]}""",
                _ => $$"""{"id":"{{ItemId}}","status":"UPDATED"}""",
            };
            // /investments responde 403 para exercitar o log próprio do cliente (rota sem ids, status, código).
            var status = pedido.RequestUri.AbsolutePath == "/investments" ? HttpStatusCode.Forbidden : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(corpo, Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task Nenhum_log_tem_o_id_do_item_nem_o_da_conta()
    {
        var log = new TodoLog();
        var servicos = new ServiceCollection();
        servicos.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Pluggy:Url"] = "https://pluggy.invalid" })
            .Build());
        servicos.AddSingleton(TimeProvider.System);
        servicos.AddLogging(b => b.ClearProviders().AddProvider(log).SetMinimumLevel(LogLevel.Trace));
        servicos.AddIntegracoes("Host=nao-usado");
        // Só a ponta da rede vira a Pluggy falsa; o resto do pipeline do HttpClient é o real.
        servicos.Configure<HttpClientFactoryOptions>(nameof(IClientePluggy),
            o => o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = new PluggyFalsa()));
        await using var provedor = servicos.BuildServiceProvider();

        var cliente = provedor.GetRequiredService<IClientePluggy>();
        var credenciais = new CredenciaisPluggy(1, "id-sintetico", "segredo-sintetico");
        Assert.Equal(ItemId, (await cliente.ObterItemAsync(credenciais, ItemId, default)).Id);
        Assert.Equal(ContaId, Assert.Single(await cliente.ListarContasAsync(credenciais, ItemId, default)).Id);
        Assert.Empty(await cliente.ListarFaturasAsync(credenciais, ContaId, default)); // a URL leva o accountId

        await Assert.ThrowsAsync<ErroDaPluggy>(() => cliente.ListarInvestimentosAsync(credenciais, ItemId, default));

        // O provedor não está surdo, e o log próprio do cliente continua (sem ids).
        Assert.Contains(log.Linhas, l => l.Contains("Pluggy GET /investments respondeu 403", StringComparison.Ordinal));
        Assert.All(log.Linhas, l =>
        {
            Assert.DoesNotContain(ItemId, l, StringComparison.Ordinal);
            Assert.DoesNotContain(ContaId, l, StringComparison.Ordinal);
        });
    }
}
