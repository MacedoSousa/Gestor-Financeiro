using GestorFinanceiro.Modules.Contas.Contracts;
using GestorFinanceiro.Modules.Contas.Dominio;
using GestorFinanceiro.Modules.Contas.Infra;
using GestorFinanceiro.SharedKernel.Aplicacao;
using GestorFinanceiro.SharedKernel.Dominio;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GestorFinanceiro.Modules.Contas.Aplicacao;

/// <summary>
/// Contas ligadas ao banco (D5, D17), para o Integracoes. Valida como o cadastro manual, no casal da sessão (ou do job).
/// O id vem de quem chama: repetir o pedido devolve a conta que já existe, sem criar outra.
/// </summary>
internal sealed class ContasLigadas(ContasDbContext db, IUsuarioAtual atual, ServicoDeContas servico, TimeProvider relogio)
    : IContasLigadas
{
    private const string IndiceDeApelido = "ux_contas_apelido";
    private const int ApelidoMaximo = 60;

    public async Task<ResultadoDaContaLigada> CriarAsync(NovaContaLigada pedido, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pedido);
        var casalId = atual.CasalId ?? throw new InvalidOperationException("Conta ligada sem casal.");

        if (await db.Contas.AnyAsync(c => c.Id == pedido.ContaId, ct)) return new ResultadoDaContaLigada.Pronta(pedido.ContaId);

        var dto = new NovaContaDto(pedido.Tipo, pedido.Apelido, pedido.Instituicao, pedido.TitularUsuarioId, null, null,
            pedido.SaldoInicialCentavos, pedido.SaldoInicialEm, pedido.DiaFechamento, pedido.DiaVencimento, pedido.LimiteCentavos);
        var erros = await servico.ValidarAsync(dto, casalId, tipoAtual: null, ct);
        if (erros.Count > 0) return new ResultadoDaContaLigada.Invalida(erros);
        if (!ServicoDeContas.TentarTipo(pedido.Tipo, out var tipo) || tipo == TipoDeConta.Dinheiro)
            return new ResultadoDaContaLigada.Invalida(new Dictionary<string, string[]> { ["tipo"] = ["Conta do banco não é do tipo DINHEIRO."] });

        var apelido = pedido.Apelido.Trim();
        if (await ApelidoUsadoAsync(apelido, ct)) return new ResultadoDaContaLigada.ApelidoRepetido(await SugestaoAsync(apelido, ct));

        var conta = Conta.CriarLigada(pedido.ContaId, casalId, tipo, apelido, relogio.GetUtcNow());
        conta.Definir(apelido, string.IsNullOrWhiteSpace(pedido.Instituicao) ? null : pedido.Instituicao.Trim(), pedido.TitularUsuarioId,
            new Dinheiro(pedido.SaldoInicialCentavos), pedido.SaldoInicialEm, pedido.DiaFechamento, pedido.DiaVencimento,
            pedido.LimiteCentavos is { } limite ? new Dinheiro(limite) : null, arquivada: false);
        db.Contas.Add(conta);
        try
        {
            await db.SaveChangesAsync(ct);
            return new ResultadoDaContaLigada.Pronta(conta.Id);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            db.ChangeTracker.Clear();
            // Outra chamada com o mesmo id gravou antes: a conta está pronta. Apelido tomado no meio: devolve a sugestão.
            return pg.ConstraintName == IndiceDeApelido
                ? new ResultadoDaContaLigada.ApelidoRepetido(await SugestaoAsync(apelido, ct))
                : new ResultadoDaContaLigada.Pronta(conta.Id);
        }
    }

    public async Task<bool> MarcarLigadaAsync(Guid contaId, CancellationToken ct)
    {
        var conta = await db.Contas.FirstOrDefaultAsync(c => c.Id == contaId, ct);
        if (conta is null) return false;
        conta.MarcarLigada(relogio.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task DefinirSaldoInicialAsync(Guid contaId, long centavos, DateOnly em, CancellationToken ct)
    {
        var conta = await db.Contas.FirstOrDefaultAsync(c => c.Id == contaId, ct)
            ?? throw new InvalidOperationException("Conta não encontrada no casal.");
        conta.DefinirSaldoInicialDoBanco(new Dinheiro(centavos), em);
        await db.SaveChangesAsync(ct);
    }

    // Traduzido para lower() do Postgres dos dois lados, a mesma regra do índice ux_contas_apelido.
#pragma warning disable CA1304, CA1311, CA1862
    private Task<bool> ApelidoUsadoAsync(string apelido, CancellationToken ct) =>
        db.Contas.AnyAsync(c => c.Apelido.ToLower() == apelido.ToLower(), ct);
#pragma warning restore CA1304, CA1311, CA1862

    /// <summary>"&lt;apelido&gt; (N)" com o primeiro N livre a partir de 2, cortando o apelido para caber em 60 caracteres.</summary>
    private async Task<string> SugestaoAsync(string apelido, CancellationToken ct)
    {
        for (var n = 2; ; n++)
        {
            var sufixo = $" ({n})";
            var candidato = apelido[..Math.Min(apelido.Length, ApelidoMaximo - sufixo.Length)].TrimEnd() + sufixo;
            if (!await ApelidoUsadoAsync(candidato, ct)) return candidato;
        }
    }
}
