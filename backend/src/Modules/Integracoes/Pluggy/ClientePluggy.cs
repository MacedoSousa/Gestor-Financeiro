using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace GestorFinanceiro.Modules.Integracoes.Pluggy;

/// <summary>
/// Cliente HTTP da Pluggy, só de leitura. A chave temporária (2 h) é pedida em <c>POST /auth</c> com as credenciais no
/// corpo e guardada só em memória (<see cref="CacheDeChavesDaPluggy"/>). Chave recusada (401) é renovada uma vez; 429
/// respeita o <c>Retry-After</c> (até 3 esperas, no máximo 60 s cada). Log e exceção levam só a rota sem ids, o status e
/// o código do erro: nunca o corpo da resposta, a chave ou um id (D26 e regra 5).
/// </summary>
internal sealed partial class ClientePluggy(
    HttpClient http, CacheDeChavesDaPluggy cache, TimeProvider relogio, ILogger<ClientePluggy> log, IEsperaDaPluggy espera)
    : IClientePluggy
{
    private const int EsperasEm429 = 3;
    private static readonly TimeSpan TetoDaEspera = TimeSpan.FromSeconds(60);

    public async Task<ItemPluggy> ObterItemAsync(CredenciaisPluggy c, string itemId, CancellationToken ct)
    {
        using var doc = await GetAsync(c, $"/items/{Uri.EscapeDataString(itemId)}", "/items/{id}", ct);
        var e = doc.RootElement;
        return new ItemPluggy(Texto(e, "id")!, Texto(e, "status") ?? "?", Texto(e, "executionStatus"),
            Instante(e, "consentExpiresAt"), Instante(e, "lastUpdatedAt") ?? Instante(e, "updatedAt"));
    }

    public async Task<IReadOnlyList<ContaPluggy>> ListarContasAsync(CredenciaisPluggy c, string itemId, CancellationToken ct)
    {
        using var doc = await GetAsync(c, $"/accounts?itemId={Uri.EscapeDataString(itemId)}", "/accounts", ct);
        return Resultados(doc).Select(e =>
        {
            var tipo = Texto(e, "type") ?? "?";
            var numero = Texto(e, "number");
            var credito = Objeto(e, "creditData");
            return new ContaPluggy(Texto(e, "id")!, tipo, Texto(e, "subtype"), Texto(e, "name") ?? "", numero,
                Objeto(e, "bankData") is { } banco ? Texto(banco, "transferNumber") : null,
                ConversaoPluggy.Centavos(e.GetProperty("balance")),
                credito is { } cr && cr.TryGetProperty("creditLimit", out var limite) && limite.ValueKind == JsonValueKind.Number
                    ? ConversaoPluggy.Centavos(limite) : null,
                credito is { } h ? Texto(h, "holder") : null,
                tipo == "CREDIT" ? FinalDoNumero(numero) : null);
        }).ToList();
    }

    public async IAsyncEnumerable<TransacaoPluggy> ListarTransacoesAsync(
        CredenciaisPluggy c, string contaId, DateOnly desde, [EnumeratorCancellation] CancellationToken ct)
    {
        var inicio = $"/v2/transactions?accountId={Uri.EscapeDataString(contaId)}&dateFrom={desde:yyyy-MM-dd}";
        string? depois = null;
        do
        {
            var caminho = depois is null ? inicio : $"{inicio}&after={Uri.EscapeDataString(depois)}";
            using var doc = await GetAsync(c, caminho, "/v2/transactions", ct);
            foreach (var e in Resultados(doc))
                yield return Transacao(e);
            depois = Texto(doc.RootElement, "next") is { } proximo && CursorAfter().Match(proximo) is { Success: true } m
                ? Uri.UnescapeDataString(m.Groups[1].Value)
                : null;
        } while (depois is not null);
    }

    public async Task<IReadOnlyList<FaturaPluggy>> ListarFaturasAsync(CredenciaisPluggy c, string contaId, CancellationToken ct)
    {
        using var doc = await GetAsync(c, $"/bills?accountId={Uri.EscapeDataString(contaId)}", "/bills", ct);
        return Resultados(doc).Select(e => new FaturaPluggy(Texto(e, "id")!,
            ConversaoPluggy.DiaDoCalendario(Texto(e, "dueDate")!),
            Texto(e, "closeDate") is { } fechamento ? ConversaoPluggy.DiaDoCalendario(fechamento) : null,
            ConversaoPluggy.Centavos(e.GetProperty("totalAmount")))).ToList();
    }

    public async Task<IReadOnlyList<InvestimentoPluggy>> ListarInvestimentosAsync(CredenciaisPluggy c, string itemId, CancellationToken ct)
    {
        using var doc = await GetAsync(c, $"/investments?itemId={Uri.EscapeDataString(itemId)}", "/investments", ct);
        return Resultados(doc).Select(e => new InvestimentoPluggy(Texto(e, "id")!, Texto(e, "type") ?? "?", Texto(e, "subtype"),
            Texto(e, "name") ?? "", ConversaoPluggy.CentavosOuNulo(e.TryGetProperty("balance", out var b) ? b : null))).ToList();
    }

    // ---------------------------------------------------------------- HTTP

    /// <param name="rotaParaLog">A rota sem ids nem query (o item é dado cifrado no banco).</param>
    private async Task<JsonDocument> GetAsync(CredenciaisPluggy c, string caminho, string rotaParaLog, CancellationToken ct)
    {
        var renovou = false;
        var esperas = 0;
        while (true)
        {
            var chave = await ChaveAsync(c, ct);
            using var pedido = new HttpRequestMessage(HttpMethod.Get, caminho);
            pedido.Headers.Add("X-API-KEY", chave);
            using var resposta = await http.SendAsync(pedido, ct);
            if (resposta.IsSuccessStatusCode)
                return await JsonDocument.ParseAsync(await resposta.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            var status = (int)resposta.StatusCode;
            var tentarDepois = TentarDepois(resposta);
            if (resposta.StatusCode == HttpStatusCode.Unauthorized && !renovou)
            {
                cache.Esquecer(c.Slot);
                renovou = true;
                continue;
            }

            if (resposta.StatusCode == HttpStatusCode.TooManyRequests && esperas < EsperasEm429)
            {
                esperas++;
                var tempo = tentarDepois ?? TimeSpan.FromSeconds(1);
                await espera.EsperarAsync(tempo > TetoDaEspera ? TetoDaEspera : tempo, ct);
                continue;
            }

            throw await FalhaAsync("GET", rotaParaLog, resposta, status, tentarDepois, ct);
        }
    }

    private async Task<string> ChaveAsync(CredenciaisPluggy c, CancellationToken ct)
    {
        if (cache.Obter(c.Slot, relogio.GetUtcNow()) is { } guardada) return guardada;

        using var pedido = new HttpRequestMessage(HttpMethod.Post, "/auth")
        {
            Content = JsonContent.Create(new { clientId = c.ClientId, clientSecret = c.ClientSecret }),
        };
        using var resposta = await http.SendAsync(pedido, ct);
        if (!resposta.IsSuccessStatusCode)
            throw await FalhaAsync("POST", "/auth", resposta, (int)resposta.StatusCode, TentarDepois(resposta), ct);

        using var doc = await JsonDocument.ParseAsync(await resposta.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var chave = Texto(doc.RootElement, "apiKey") ?? throw new ErroDaPluggy((int)resposta.StatusCode, "SEM_APIKEY", null);
        cache.Guardar(c.Slot, chave, relogio.GetUtcNow());
        return chave;
    }

    private async Task<ErroDaPluggy> FalhaAsync(string metodo, string rota, HttpResponseMessage resposta, int status,
        TimeSpan? tentarDepois, CancellationToken ct)
    {
        var codigo = await CodigoDoErroAsync(resposta, ct);
        RespostaDeErro(log, metodo, rota, status, codigo ?? "-");
        return new ErroDaPluggy(status, codigo, tentarDepois);
    }

    /// <summary>Só o campo <c>code</c> do corpo de erro; o resto do corpo é descartado.</summary>
    private static async Task<string?> CodigoDoErroAsync(HttpResponseMessage resposta, CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(await resposta.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("code", out var codigo)) return null;
            return codigo.ValueKind switch
            {
                JsonValueKind.String => codigo.GetString(),
                JsonValueKind.Number => codigo.GetRawText(),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private TimeSpan? TentarDepois(HttpResponseMessage resposta) =>
        resposta.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } data } => data - relogio.GetUtcNow(),
            _ => null,
        };

    // ---------------------------------------------------------------- JSON

    private static TransacaoPluggy Transacao(JsonElement e)
    {
        var cartao = Objeto(e, "creditCardMetadata");
        return new TransacaoPluggy(Texto(e, "id")!, ConversaoPluggy.DiaDeSaoPaulo(Texto(e, "date")!),
            ConversaoPluggy.Centavos(e.GetProperty("amount")), Texto(e, "type") ?? "?", Texto(e, "status") ?? "?",
            Texto(e, "description"), Texto(e, "categoryId"), Texto(e, "category"),
            cartao is { } p ? Inteiro(p, "installmentNumber") : null,
            cartao is { } t ? Inteiro(t, "totalInstallments") : null,
            cartao is { } b ? Texto(b, "billId") : null);
    }

    private static List<JsonElement> Resultados(JsonDocument doc) =>
        doc.RootElement.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array ? [.. r.EnumerateArray()] : [];

    private static JsonElement? Objeto(JsonElement e, string nome) =>
        e.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Texto(JsonElement e, string nome) =>
        e.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Inteiro(JsonElement e, string nome) =>
        e.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static DateTimeOffset? Instante(JsonElement e, string nome) =>
        Texto(e, nome) is { } t && DateTimeOffset.TryParse(t, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static string? FinalDoNumero(string? numero)
    {
        var digitos = new string((numero ?? "").Where(char.IsAsciiDigit).ToArray());
        return digitos.Length >= 4 ? digitos[^4..] : null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pluggy {Metodo} {Rota} respondeu {Status} {Codigo}")]
    private static partial void RespostaDeErro(ILogger logger, string metodo, string rota, int status, string codigo);

    [GeneratedRegex(@"[?&]after=([^&]+)")]
    private static partial Regex CursorAfter();
}
