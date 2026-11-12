using System.Collections.Concurrent;
using GestorFinanceiro.Modules.Integracoes.Pluggy;

namespace GestorFinanceiro.IntegrationTests.Infra;

/// <summary>
/// Pluggy falsa em memória: conhece só os items cadastrados nela, cada um aceito só pelo client id de um slot. Item
/// desconhecido ou chave de outro slot dá <see cref="ErroDaPluggy"/> 404, como a Pluggy. Registra cada consulta (slot e
/// client id) para os testes conferirem com quais chaves o sistema autenticou. Dados sintéticos.
/// </summary>
internal sealed class ClientePluggyFalso : IClientePluggy
{
    private readonly ConcurrentDictionary<string, (string ClientId, ItemPluggy Item)> _items = new();

    public ConcurrentQueue<(int Slot, string ClientId, string ItemId)> Consultas { get; } = new();

    /// <summary>Exceção a lançar em vez de responder (ex.: a Pluggy fora do ar).</summary>
    public Exception? Falha { get; set; }

    public void Cadastrar(string clientId, string itemId, string status = "UPDATED", DateTimeOffset? consentimentoVenceEm = null) =>
        _items[itemId] = (clientId, new ItemPluggy(itemId, status, "SUCCESS", consentimentoVenceEm, null));

    public Task<ItemPluggy> ObterItemAsync(CredenciaisPluggy c, string itemId, CancellationToken ct)
    {
        Consultas.Enqueue((c.Slot, c.ClientId, itemId));
        if (Falha is not null) throw Falha;
        return _items.TryGetValue(itemId, out var i) && i.ClientId == c.ClientId
            ? Task.FromResult(i.Item)
            : throw new ErroDaPluggy(404, "ITEM_NOT_FOUND", null);
    }

    public Task<IReadOnlyList<ContaPluggy>> ListarContasAsync(CredenciaisPluggy c, string itemId, CancellationToken ct) =>
        throw new NotSupportedException();

    public IAsyncEnumerable<TransacaoPluggy> ListarTransacoesAsync(CredenciaisPluggy c, string contaId, DateOnly desde, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<FaturaPluggy>> ListarFaturasAsync(CredenciaisPluggy c, string contaId, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<InvestimentoPluggy>> ListarInvestimentosAsync(CredenciaisPluggy c, string itemId, CancellationToken ct) =>
        throw new NotSupportedException();
}
