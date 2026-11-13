using System.Collections.Concurrent;
using GestorFinanceiro.Modules.Integracoes.Pluggy;

namespace GestorFinanceiro.IntegrationTests.Infra;

/// <summary>
/// Pluggy falsa em memória: conhece só os items cadastrados nela, cada um aceito só pelo client id de um slot. Item
/// desconhecido ou chave de outro slot dá <see cref="ErroDaPluggy"/> 404, como a Pluggy. Registra cada consulta de item
/// (slot e client id) para os testes conferirem com quais chaves o sistema autenticou. Contas, faturas e investimentos
/// cadastrados por item (só as chaves do item os enxergam). Dados sintéticos.
/// </summary>
internal sealed class ClientePluggyFalso : IClientePluggy
{
    private readonly ConcurrentDictionary<string, (string ClientId, ItemPluggy Item)> _items = new();
    private readonly ConcurrentDictionary<string, List<ContaPluggy>> _contas = new();
    private readonly ConcurrentDictionary<string, List<FaturaPluggy>> _faturas = new();
    private readonly ConcurrentDictionary<string, List<InvestimentoPluggy>> _investimentos = new();
    private readonly ConcurrentDictionary<string, List<TransacaoPluggy>> _transacoes = new();

    /// <summary>Cada leitura de transações: a conta e o "desde" pedido.</summary>
    public ConcurrentQueue<(string ContaId, DateOnly Desde)> LeiturasDeTransacoes { get; } = new();

    /// <summary>Contas cuja leitura de transações falha (como um 500 da Pluggy só naquela conta).</summary>
    public ConcurrentDictionary<string, bool> ContasComFalha { get; } = new();

    public ConcurrentQueue<(int Slot, string ClientId, string ItemId)> Consultas { get; } = new();

    /// <summary>Exceção a lançar em vez de responder (ex.: a Pluggy fora do ar).</summary>
    public Exception? Falha { get; set; }

    public void Cadastrar(string clientId, string itemId, string status = "UPDATED", DateTimeOffset? consentimentoVenceEm = null) =>
        _items[itemId] = (clientId, new ItemPluggy(itemId, status, "SUCCESS", consentimentoVenceEm, null));

    /// <summary>Conta (ou cartão) do item; o item precisa estar cadastrado antes.</summary>
    public void CadastrarConta(string itemId, ContaPluggy conta) => _contas.GetOrAdd(itemId, _ => []).Add(conta);

    /// <summary>Troca o saldo (ou outro dado) de uma conta já cadastrada, pelo id.</summary>
    public void AtualizarConta(string itemId, ContaPluggy conta)
    {
        var lista = _contas[itemId];
        lock (lista) lista[lista.FindIndex(c => c.Id == conta.Id)] = conta;
    }

    /// <summary>Transação da conta, como a Pluggy manda (valor no sinal da Pluggy; data já no dia de São Paulo).</summary>
    public void CadastrarTransacao(string contaId, TransacaoPluggy transacao) => _transacoes.GetOrAdd(contaId, _ => []).Add(transacao);

    /// <summary>O banco apagou a transação.</summary>
    public void RemoverTransacao(string contaId, string id) => _transacoes[contaId].RemoveAll(t => t.Id == id);

    public void CadastrarFatura(string contaId, FaturaPluggy fatura) => _faturas.GetOrAdd(contaId, _ => []).Add(fatura);

    public void CadastrarInvestimento(string itemId, InvestimentoPluggy investimento) =>
        _investimentos.GetOrAdd(itemId, _ => []).Add(investimento);

    public Task<ItemPluggy> ObterItemAsync(CredenciaisPluggy c, string itemId, CancellationToken ct)
    {
        Consultas.Enqueue((c.Slot, c.ClientId, itemId));
        return Task.FromResult(Item(c, itemId));
    }

    public Task<IReadOnlyList<ContaPluggy>> ListarContasAsync(CredenciaisPluggy c, string itemId, CancellationToken ct)
    {
        Item(c, itemId);
        return Task.FromResult<IReadOnlyList<ContaPluggy>>(_contas.TryGetValue(itemId, out var l) ? [.. l] : []);
    }

    public async IAsyncEnumerable<TransacaoPluggy> ListarTransacoesAsync(CredenciaisPluggy c, string contaId, DateOnly desde,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        if (Falha is not null) throw Falha;
        if (!_items.Any(i => i.Value.ClientId == c.ClientId && _contas.TryGetValue(i.Key, out var l) && l.Any(x => x.Id == contaId)))
            throw new ErroDaPluggy(404, "ACCOUNT_NOT_FOUND", null);
        LeiturasDeTransacoes.Enqueue((contaId, desde));
        if (ContasComFalha.ContainsKey(contaId)) throw new ErroDaPluggy(500, "INTERNAL_ERROR", null);
        foreach (var t in _transacoes.TryGetValue(contaId, out var l2) ? l2.ToList() : [])
            if (t.Data >= desde) yield return t;
    }

    public Task<IReadOnlyList<FaturaPluggy>> ListarFaturasAsync(CredenciaisPluggy c, string contaId, CancellationToken ct)
    {
        if (Falha is not null) throw Falha;
        if (!_items.Any(i => i.Value.ClientId == c.ClientId && _contas.TryGetValue(i.Key, out var l) && l.Any(x => x.Id == contaId)))
            throw new ErroDaPluggy(404, "ACCOUNT_NOT_FOUND", null);
        return Task.FromResult<IReadOnlyList<FaturaPluggy>>(_faturas.TryGetValue(contaId, out var f) ? [.. f] : []);
    }

    public Task<IReadOnlyList<InvestimentoPluggy>> ListarInvestimentosAsync(CredenciaisPluggy c, string itemId, CancellationToken ct)
    {
        Item(c, itemId);
        return Task.FromResult<IReadOnlyList<InvestimentoPluggy>>(_investimentos.TryGetValue(itemId, out var l) ? [.. l] : []);
    }

    /// <summary>O item, se estas chaves o enxergam; senão 404, como a Pluggy.</summary>
    private ItemPluggy Item(CredenciaisPluggy c, string itemId)
    {
        if (Falha is not null) throw Falha;
        return _items.TryGetValue(itemId, out var i) && i.ClientId == c.ClientId ? i.Item : throw new ErroDaPluggy(404, "ITEM_NOT_FOUND", null);
    }
}
