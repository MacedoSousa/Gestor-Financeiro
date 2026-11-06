using GestorFinanceiro.Modules.Lancamentos.Contracts;
using GestorFinanceiro.Modules.Orcamento.Contracts;
using GestorFinanceiro.Modules.Orcamento.Dominio;
using GestorFinanceiro.Modules.Orcamento.Infra;
using GestorFinanceiro.SharedKernel.Aplicacao;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GestorFinanceiro.Modules.Orcamento.Aplicacao;

internal abstract record ResultadoDoOrcamento
{
    public sealed record DoMes(OrcamentoDoMesDto Orcamento) : ResultadoDoOrcamento;
    public sealed record Copia(CopiaDeTetosDto Resultado) : ResultadoDoOrcamento;
    public sealed record Feito : ResultadoDoOrcamento;
    public sealed record Invalido(IReadOnlyDictionary<string, string[]> Erros) : ResultadoDoOrcamento;
    public sealed record Recusado(string Titulo, int Status) : ResultadoDoOrcamento;
    public sealed record NaoEncontrado : ResultadoDoOrcamento;
}

/// <summary>
/// Tetos por categoria, receita prevista e cópia de tetos. O casal vem da sessão. Toda escrita num mês roda em
/// transação com o lock do par (casal, mês); categorias só pelos contratos do Lancamentos.
/// </summary>
internal sealed class ServicoDeOrcamento(
    OrcamentoDbContext db, IUsuarioAtual atual, IConsultaDeCategorias categorias, IConsultaDeUso uso, TimeProvider relogio)
{
    internal const string TituloCategoriaArquivada = "Categoria arquivada: escolha outra.";
    internal const string MensagemDoValor = "O valor deve ficar entre 0 e 100.000.000.000 centavos.";
    internal const string MensagemDeReceita = "Escolha uma categoria de despesa.";
    private const string IndiceDosTetos = "ux_tetos";
    private const string IndiceDasReceitas = "ux_receitas_previstas";

    public async Task<ResultadoDoOrcamento> ObterAsync(string? texto, CancellationToken ct)
    {
        if (atual.CasalId is null) return new ResultadoDoOrcamento.NaoEncontrado();
        if (!LerMes(texto, out var mes, out var erros)) return new ResultadoDoOrcamento.Invalido(erros);

        var lista = await categorias.ListarDeDespesaAsync(ct);
        var tetos = await db.Tetos.AsNoTracking().Where(t => t.Mes == mes.Inicio)
            .ToDictionaryAsync(t => t.CategoriaId, t => t.ValorCentavos, ct);
        var receitaPrevista = await db.ReceitasPrevistas.AsNoTracking().Where(r => r.Mes == mes.Inicio)
            .Select(r => (long?)r.ValorCentavos).FirstOrDefaultAsync(ct);
        var usado = (await uso.UsoPorCategoriaAsync(mes.Inicio, ct)).ToDictionary(u => u.CategoriaId, u => u.UsadoCentavos);
        var aportes = await uso.AportesAsync(mes.Inicio, ct);
        // Mês calendário anterior, mesmo antes de 2020-01: a sugestão é só uma consulta, não um mês editável.
        var receitaSugerida = await uso.ReceitasAsync(mes.Anterior().Inicio, ct);
        var mesParaCopiar = await MesParaCopiarAsync(mes, ct);

        return new ResultadoDoOrcamento.DoMes(CalculoDoOrcamento.Calcular(
            mes, receitaPrevista, receitaSugerida, lista, tetos, usado, aportes, mesParaCopiar));
    }

    public async Task<ResultadoDoOrcamento> DefinirTetoAsync(string texto, Guid categoriaId, long valorCentavos, CancellationToken ct)
    {
        if (atual.CasalId is not { } casalId) return new ResultadoDoOrcamento.NaoEncontrado();
        LerMes(texto, out var mes, out var erros);
        ValidarValor(valorCentavos, erros);
        if (erros.Count > 0) return new ResultadoDoOrcamento.Invalido(erros);

        // Fora da transação: a lista pode criar as categorias padrão numa transação própria (do Lancamentos).
        var categoria = (await categorias.ListarDeDespesaAsync(ct)).FirstOrDefault(c => c.Id == categoriaId);
        if (categoria is null)
            return await categorias.EhDeReceitaAsync(categoriaId, ct)
                ? Invalido("categoriaId", MensagemDeReceita)
                : new ResultadoDoOrcamento.NaoEncontrado();

        return await NaTravaDoMesAsync(casalId, mes, async () =>
        {
            var teto = await db.Tetos.FirstOrDefaultAsync(t => t.Mes == mes.Inicio && t.CategoriaId == categoriaId, ct);
            if (teto is not null)
            {
                teto.Definir(valorCentavos);
                return new ResultadoDoOrcamento.Feito();
            }
            if (categoria.Arquivada) return new ResultadoDoOrcamento.Recusado(TituloCategoriaArquivada, 400);
            db.Tetos.Add(Teto.Criar(casalId, mes.Inicio, categoriaId, valorCentavos));
            return new ResultadoDoOrcamento.Feito();
        }, ct);
    }

    public async Task<ResultadoDoOrcamento> RemoverTetoAsync(string texto, Guid categoriaId, CancellationToken ct)
    {
        if (atual.CasalId is not { } casalId) return new ResultadoDoOrcamento.NaoEncontrado();
        if (!LerMes(texto, out var mes, out var erros)) return new ResultadoDoOrcamento.Invalido(erros);

        return await NaTravaDoMesAsync(casalId, mes, async () =>
        {
            var teto = await db.Tetos.FirstOrDefaultAsync(t => t.Mes == mes.Inicio && t.CategoriaId == categoriaId, ct);
            if (teto is null) return new ResultadoDoOrcamento.NaoEncontrado();
            db.Tetos.Remove(teto); // SoftDeleteInterceptor converte em is_deleted = true.
            return new ResultadoDoOrcamento.Feito();
        }, ct);
    }

    public async Task<ResultadoDoOrcamento> DefinirReceitaAsync(string texto, long valorCentavos, CancellationToken ct)
    {
        if (atual.CasalId is not { } casalId) return new ResultadoDoOrcamento.NaoEncontrado();
        LerMes(texto, out var mes, out var erros);
        ValidarValor(valorCentavos, erros);
        if (erros.Count > 0) return new ResultadoDoOrcamento.Invalido(erros);

        return await NaTravaDoMesAsync(casalId, mes, async () =>
        {
            var receita = await db.ReceitasPrevistas.FirstOrDefaultAsync(r => r.Mes == mes.Inicio, ct);
            if (receita is null) db.ReceitasPrevistas.Add(ReceitaPrevista.Criar(casalId, mes.Inicio, valorCentavos));
            else receita.Definir(valorCentavos);
            return new ResultadoDoOrcamento.Feito();
        }, ct);
    }

    public async Task<ResultadoDoOrcamento> RemoverReceitaAsync(string texto, CancellationToken ct)
    {
        if (atual.CasalId is not { } casalId) return new ResultadoDoOrcamento.NaoEncontrado();
        if (!LerMes(texto, out var mes, out var erros)) return new ResultadoDoOrcamento.Invalido(erros);

        return await NaTravaDoMesAsync(casalId, mes, async () =>
        {
            var receita = await db.ReceitasPrevistas.FirstOrDefaultAsync(r => r.Mes == mes.Inicio, ct);
            if (receita is null) return new ResultadoDoOrcamento.NaoEncontrado();
            db.ReceitasPrevistas.Remove(receita);
            return new ResultadoDoOrcamento.Feito();
        }, ct);
    }

    /// <summary>
    /// Copia para M os tetos do último mês anterior com teto, só nas categorias de despesa ativas (nem arquivadas
    /// nem apagadas) que ainda não têm linha em M. Linha existente em M, inclusive teto zero, nunca é sobrescrita.
    /// </summary>
    public async Task<ResultadoDoOrcamento> CopiarAsync(string texto, CancellationToken ct)
    {
        if (atual.CasalId is not { } casalId) return new ResultadoDoOrcamento.NaoEncontrado();
        if (!LerMes(texto, out var mes, out var erros)) return new ResultadoDoOrcamento.Invalido(erros);

        var ativas = (await categorias.ListarDeDespesaAsync(ct)).Where(c => !c.Arquivada).Select(c => c.Id).ToHashSet();

        return await NaTravaDoMesAsync(casalId, mes, async () =>
        {
            if (await MesParaCopiarAsync(mes, ct) is not { } origem)
                return new ResultadoDoOrcamento.Copia(new CopiaDeTetosDto(0, null));

            var jaTem = (await db.Tetos.AsNoTracking().Where(t => t.Mes == mes.Inicio).Select(t => t.CategoriaId).ToListAsync(ct)).ToHashSet();
            var novos = (await db.Tetos.AsNoTracking().Where(t => t.Mes == origem.Inicio).ToListAsync(ct))
                .Where(t => ativas.Contains(t.CategoriaId) && !jaTem.Contains(t.CategoriaId))
                .Select(t => Teto.Criar(casalId, mes.Inicio, t.CategoriaId, t.ValorCentavos))
                .ToList();
            db.Tetos.AddRange(novos);
            return new ResultadoDoOrcamento.Copia(new CopiaDeTetosDto(novos.Count, origem.Texto));
        }, ct);
    }

    /// <summary>O maior mês com teto ativo antes de M (pula meses vazios).</summary>
    private async Task<MesDoOrcamento?> MesParaCopiarAsync(MesDoOrcamento mes, CancellationToken ct)
    {
        var origem = await db.Tetos.AsNoTracking().Where(t => t.Mes < mes.Inicio).MaxAsync(t => (DateOnly?)t.Mes, ct);
        return origem is { } inicio ? new MesDoOrcamento(inicio) : null;
    }

    /// <summary>
    /// Transação com <c>pg_advisory_xact_lock</c> por casal e mês: escritas concorrentes no mesmo mês ficam em fila.
    /// Se ainda assim o índice parcial acusar 23505 (alguém gravou sem o lock), refaz uma vez: a nova leitura acha a
    /// linha e a escrita vira atualização.
    /// </summary>
    private async Task<ResultadoDoOrcamento> NaTravaDoMesAsync(
        Guid casalId, MesDoOrcamento mes, Func<Task<ResultadoDoOrcamento>> acao, CancellationToken ct)
    {
        for (var tentativa = 1; ; tentativa++)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended('orcamento:' || {casalId}::text || ':' || {mes.Texto}, 0))", ct);
            try
            {
                var resultado = await acao();
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return resultado;
            }
            catch (DbUpdateException e) when (tentativa == 1 && e.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: IndiceDosTetos or IndiceDasReceitas,
            })
            {
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
            }
        }
    }

    private bool LerMes(string? texto, out MesDoOrcamento mes, out Dictionary<string, string[]> erros)
    {
        erros = [];
        if (MesDoOrcamento.TryLer(texto, Brasilia.Hoje(relogio), out mes, out var erro)) return true;
        erros["mes"] = [erro!];
        return false;
    }

    private static void ValidarValor(long valorCentavos, Dictionary<string, string[]> erros)
    {
        if (valorCentavos is < 0 or > LimitesDoOrcamento.ValorMaximo) erros["valorCentavos"] = [MensagemDoValor];
    }

    private static ResultadoDoOrcamento.Invalido Invalido(string campo, string mensagem) =>
        new(new Dictionary<string, string[]> { [campo] = [mensagem] });
}
