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
internal sealed class ContasLigadas(ContasDbContext db, IUsuarioAtual atual, ServicoDeContas servico, TimeProvider relogio,
    IEnumerable<IVerificadorDeUsoDaConta> verificadores)
    : IContasLigadas
{
    private const string IndiceDeApelido = "ux_contas_apelido";
    private const string ChavePrimaria = "contas_pkey";
    private const int ApelidoMaximo = 60;

    public async Task<ResultadoDaContaLigada> CriarAsync(NovaContaLigada pedido, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pedido);
        var casalId = atual.CasalId ?? throw new InvalidOperationException("Conta ligada sem casal.");

        if (await JaExisteAsync(pedido.ContaId, ct) is { } existente) return existente;

        var dto = new NovaContaDto(pedido.Tipo, pedido.Apelido, pedido.Instituicao, pedido.TitularUsuarioId, null, null,
            pedido.SaldoInicialCentavos, pedido.SaldoInicialEm, pedido.DiaFechamento, pedido.DiaVencimento, pedido.LimiteCentavos);
        var erros = await servico.ValidarAsync(dto, casalId, tipoAtual: null, ct);
        if (erros.Count > 0) return new ResultadoDaContaLigada.Invalida(erros);
        if (!ServicoDeContas.TentarTipo(pedido.Tipo, out var tipo) || tipo == TipoDeConta.Dinheiro)
            return new ResultadoDaContaLigada.Invalida(new Dictionary<string, string[]> { ["tipo"] = ["Conta do banco não é do tipo DINHEIRO."] });

        var apelido = pedido.Apelido.Trim();
        if (await ApelidoUsadoAsync(apelido, ct)) return await ApelidoRepetidoAsync(pedido.ContaId, apelido, ct);

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
        catch (DbUpdateException e) when (e.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: IndiceDeApelido or ChavePrimaria,
        } pg)
        {
            db.ChangeTracker.Clear();
            // Apelido tomado no meio: devolve a sugestão. Id tomado: é desta conta ligada (outra chamada gravou antes) ou de
            // uma conta que o filtro não mostra (apagada, de outro casal); nunca "Pronta" sem conferir.
            return pg.ConstraintName == IndiceDeApelido
                ? await ApelidoRepetidoAsync(conta.Id, apelido, ct)
                : await JaExisteAsync(conta.Id, ct) ?? new ResultadoDaContaLigada.IdEmUso();
        }
    }

    /// <summary>
    /// O apelido já está em uso. Se for por esta mesma conta, criada agora por outra chamada com o mesmo id (duas
    /// confirmações juntas), ela está pronta; senão, devolve a sugestão.
    /// </summary>
    private async Task<ResultadoDaContaLigada> ApelidoRepetidoAsync(Guid id, string apelido, CancellationToken ct) =>
        await JaExisteAsync(id, ct) ?? new ResultadoDaContaLigada.ApelidoRepetido(await SugestaoAsync(apelido, ct));

    /// <summary>A conta com este id no casal: ligada = pronta; manual = id em uso. Nula se o filtro não a mostra.</summary>
    private async Task<ResultadoDaContaLigada?> JaExisteAsync(Guid id, CancellationToken ct)
    {
        var conta = await db.Contas.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        return conta is null ? null : conta.Ligada ? new ResultadoDaContaLigada.Pronta(id) : new ResultadoDaContaLigada.IdEmUso();
    }

    public async Task<bool> MarcarLigadaAsync(Guid contaId, CancellationToken ct)
    {
        // Na trava da conta, a mesma do lançar: um lançamento manual em andamento termina antes de a conta virar do banco.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({ChaveDeTrava.Conta(contaId)}, 0))", ct);
        var conta = await db.Contas.FirstOrDefaultAsync(c => c.Id == contaId, ct);
        if (conta is null) return false;
        conta.MarcarLigada(relogio.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task DefinirSaldoInicialAsync(Guid contaId, long centavos, DateOnly em, CancellationToken ct)
    {
        var conta = await db.Contas.FirstOrDefaultAsync(c => c.Id == contaId, ct)
            ?? throw new InvalidOperationException("Conta não encontrada no casal.");
        conta.DefinirSaldoInicialDoBanco(new Dinheiro(centavos), em);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Na trava da conta (a mesma da importação e do apagar conta): um lançamento nesta conta ou já está gravado quando os
    /// verificadores olham, ou espera e encontra a conta desligada (e a importação, que exige conta ligada, recusa).
    /// </summary>
    public async Task<ResultadoDoDesligar> DesligarAsync(Guid contaId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({ChaveDeTrava.Conta(contaId)}, 0))", ct);
        var conta = await db.Contas.FirstOrDefaultAsync(c => c.Id == contaId, ct);
        if (conta is not { Ligada: true }) return ResultadoDoDesligar.NaoEraLigada;

        var emUso = false;
        foreach (var verificador in verificadores)
            emUso |= await verificador.EstaEmUsoAsync(contaId, ct);
        conta.Desligar();
        if (!emUso) conta.Arquivar();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return emUso ? ResultadoDoDesligar.Manual : ResultadoDoDesligar.Arquivada;
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
