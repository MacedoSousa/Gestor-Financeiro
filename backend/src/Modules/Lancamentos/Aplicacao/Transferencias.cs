using GestorFinanceiro.Modules.Contas.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Dominio;
using GestorFinanceiro.Modules.Lancamentos.Infra;
using GestorFinanceiro.SharedKernel.Aplicacao;
using GestorFinanceiro.SharedKernel.Dominio;
using GestorFinanceiro.SharedKernel.Seguranca;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GestorFinanceiro.Modules.Lancamentos.Aplicacao;

internal abstract record ResultadoDeTransferencia
{
    public sealed record Criada(TransferenciaDto Transferencia) : ResultadoDeTransferencia;
    public sealed record Existente(TransferenciaDto Transferencia) : ResultadoDeTransferencia;
    public sealed record Falhou(ResultadoDeLancamento Motivo) : ResultadoDeTransferencia;
}

/// <summary>
/// Transferência entre contas do casal (inclusive o pagamento da fatura, com destino ao cartão): duas pernas com o
/// mesmo grupo, gravadas juntas. As pernas não têm categoria nem fatura, então não entram em despesa, receita nem no
/// total da fatura. Editar e apagar uma perna age no grupo, pelas rotas de lançamentos.
/// </summary>
internal sealed class ServicoDeTransferencias(LancamentosDbContext db, IUsuarioAtual atual, IConsultaDeContas contas,
    ConsultaDoExtrato extrato, ICofre cofre)
{
    private const string IndiceDaChave = "ux_transacoes_origem_id_externo";

    public async Task<ResultadoDeTransferencia> CriarAsync(NovaTransferenciaDto p, CancellationToken ct)
    {
        if (atual.CasalId is not { } casalId) return Falhou(new ResultadoDeLancamento.NaoEncontrado());
        var erros = ServicoDeLancamentos.ValidarCampos("TRANSFERENCIA", p.ValorCentavos, p.Data, p.Descricao, null, aceitaTransferencia: true);
        if (p.ChaveIdempotente == Guid.Empty) erros["chaveIdempotente"] = ["Informe a chave de idempotência."];
        if (p.ContaOrigemId == p.ContaDestinoId) erros["contaDestinoId"] = ["A conta de destino deve ser diferente da de origem."];
        if (erros.Count > 0) return Falhou(new ResultadoDeLancamento.Invalido(erros));

        var chave = p.ChaveIdempotente.ToString("D");
        var chaveDaSaida = chave + ":origem";
        var chaveDaEntrada = chave + ":destino";

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Mesmo lock dos lançamentos: dois POSTs com a mesma chave criam um só par.
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended('lancamentos.chave:' || {p.ChaveIdempotente}::text, 0))", ct);

        var existentes = await db.Transacoes.AsNoTracking()
            .Where(t => t.Origem == OrigemDaTransacao.Manual && (t.IdExterno == chaveDaSaida || t.IdExterno == chaveDaEntrada))
            .ToListAsync(ct);
        if (existentes.Count == 2)
        {
            await tx.CommitAsync(ct);
            return new ResultadoDeTransferencia.Existente(await MontarAsync(existentes, casalId, ct));
        }

        await TravaDeConta.TomarAsync(db, [p.ContaOrigemId, p.ContaDestinoId], ct);
        var origem = await contas.ObterAsync(p.ContaOrigemId, ct);
        var destino = await contas.ObterAsync(p.ContaDestinoId, ct);
        if (origem is null || destino is null) return Falhou(new ResultadoDeLancamento.NaoEncontrado());
        if (origem.Arquivada || destino.Arquivada)
            return Falhou(new ResultadoDeLancamento.Recusado(ServicoDeLancamentos.TituloContaArquivada, 400));

        var grupo = Guid.CreateVersion7();
        var valor = p.ValorCentavos;
        var saida = Transacao.CriarPernaDeTransferencia(casalId, origem.Id, p.Data, new Dinheiro(-valor), p.Descricao, null,
            OrigemDaTransacao.Manual, chaveDaSaida, atual.UsuarioId, grupo, faturaVencimento: null, cofre);
        var entrada = Transacao.CriarPernaDeTransferencia(casalId, destino.Id, p.Data, new Dinheiro(valor), p.Descricao, null,
            OrigemDaTransacao.Manual, chaveDaEntrada, atual.UsuarioId, grupo, faturaVencimento: null, cofre);
        db.Transacoes.AddRange(saida, entrada);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: IndiceDaChave })
        {
            // Só acontece se a chave já foi usada por outro casal (a busca acima respeita o filtro de casal).
            return Falhou(new ResultadoDeLancamento.Recusado(ServicoDeLancamentos.TituloChaveUsada, 409));
        }

        await tx.CommitAsync(ct);
        return new ResultadoDeTransferencia.Criada(await MontarAsync([saida, entrada], casalId, ct));
    }

    private async Task<TransferenciaDto> MontarAsync(IReadOnlyList<Transacao> pernas, Guid casalId, CancellationToken ct)
    {
        var dtos = await extrato.MontarAsync(pernas, casalId, null, ct);
        var saida = dtos.Single(d => d.ValorCentavos < 0);
        var entrada = dtos.Single(d => d.ValorCentavos > 0);
        return new TransferenciaDto(saida.GrupoId!.Value, saida, entrada);
    }

    private static ResultadoDeTransferencia.Falhou Falhou(ResultadoDeLancamento motivo) => new(motivo);
}
