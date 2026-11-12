namespace GestorFinanceiro.Modules.Integracoes.Pluggy;

/// <summary>
/// Cliente da Pluggy, só de leitura (ADR-015): <c>POST /auth</c> e <c>GET</c> de items, contas, transações (v2, por
/// cursor), faturas e investimentos. Nunca <c>GET /v2/items</c> (403 no Meu Pluggy) nem <c>GET /transactions</c> (410).
/// Erros saem como <see cref="ErroDaPluggy"/>.
/// </summary>
internal interface IClientePluggy
{
    Task<ItemPluggy> ObterItemAsync(CredenciaisPluggy c, string itemId, CancellationToken ct);
    Task<IReadOnlyList<ContaPluggy>> ListarContasAsync(CredenciaisPluggy c, string itemId, CancellationToken ct);
    IAsyncEnumerable<TransacaoPluggy> ListarTransacoesAsync(CredenciaisPluggy c, string contaId, DateOnly desde, CancellationToken ct);
    Task<IReadOnlyList<FaturaPluggy>> ListarFaturasAsync(CredenciaisPluggy c, string contaId, CancellationToken ct);
    Task<IReadOnlyList<InvestimentoPluggy>> ListarInvestimentosAsync(CredenciaisPluggy c, string itemId, CancellationToken ct);
}

/// <summary>Espera do <c>Retry-After</c>; os testes trocam por uma que não dorme.</summary>
internal interface IEsperaDaPluggy
{
    Task EsperarAsync(TimeSpan tempo, CancellationToken ct);
}

internal sealed class EsperaReal : IEsperaDaPluggy
{
    public Task EsperarAsync(TimeSpan tempo, CancellationToken ct) => Task.Delay(tempo, ct);
}
