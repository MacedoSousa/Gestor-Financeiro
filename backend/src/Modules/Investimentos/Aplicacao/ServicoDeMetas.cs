using System.Globalization;
using GestorFinanceiro.Modules.Contas.Contracts;
using GestorFinanceiro.Modules.Investimentos.Contracts;
using GestorFinanceiro.Modules.Investimentos.Dominio;
using GestorFinanceiro.Modules.Investimentos.Infra;
using GestorFinanceiro.Modules.Lancamentos.Contracts;
using GestorFinanceiro.SharedKernel.Aplicacao;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GestorFinanceiro.Modules.Investimentos.Aplicacao;

internal abstract record ResultadoDeMetas
{
    public sealed record Painel(PainelDeMetasDto Dto) : ResultadoDeMetas;
    public sealed record Criada(MetaDto Meta) : ResultadoDeMetas;
    public sealed record Simulacao(ProjecaoDto Projecao) : ResultadoDeMetas;
    public sealed record Feito(Guid Id) : ResultadoDeMetas;
    public sealed record Invalido(IReadOnlyDictionary<string, string[]> Erros) : ResultadoDeMetas;
    public sealed record Recusado(string Titulo, int Status) : ResultadoDeMetas;
    public sealed record NaoEncontrado : ResultadoDeMetas;
}

/// <summary>
/// Metas do casal e suas contas. O casal vem da sessão; contas, saldos e aportes só pelos contratos de Contas e
/// Lancamentos. Toda escrita roda em transação com o lock do casal; os índices parciais são a última defesa.
/// </summary>
internal sealed class ServicoDeMetas(
    InvestimentosDbContext db,
    IUsuarioAtual atual,
    IConsultaDeContas contas,
    IConsultaDeSaldos saldos,
    IConsultaDeUso uso,
    TimeProvider relogio)
{
    internal const string TituloContaEmOutraMeta = "Esta conta já pertence a outra meta.";
    internal const string TituloNomeRepetido = "Já existe uma meta com esse nome.";
    internal const string TituloOutroTipo = "Só contas de poupança ou investimento entram numa meta.";
    internal const string TituloArquivada = "Conta arquivada: escolha outra.";
    internal const string MensagemSemContas = "Escolha ao menos uma conta.";
    private const string IndiceDoNome = "ux_metas_nome";
    private const string IndiceDaConta = "ux_metas_contas_conta";
    private static readonly string[] TiposDeInvestimento = ["POUPANCA", "INVESTIMENTO"];

    private sealed record MetaPedida(string Nome, long Alvo, DateOnly? Data, long Aporte, decimal Taxa, Guid[] Contas);

    /// <summary>O que o painel e a simulação leem dos outros módulos, uma vez por requisição.</summary>
    private sealed record Leituras(
        DateOnly Mes,
        IReadOnlyDictionary<Guid, ContaResumo> Contas,
        IReadOnlyDictionary<Guid, long> Saldos,
        IReadOnlyDictionary<Guid, long> Aportes);

    public async Task<ResultadoDeMetas> PainelAsync(CancellationToken ct)
    {
        if (atual.CasalId is null) return new ResultadoDeMetas.NaoEncontrado();

        var leituras = await LerAsync(ct);
        var metas = await db.Metas.AsNoTracking().OrderBy(m => m.Id).ToListAsync(ct);
        var vinculos = await db.MetasContas.AsNoTracking().ToListAsync(ct);

        var dtos = metas.Select(m => Montar(m, vinculos, leituras, m.AportePlanejadoCentavos)).ToList();
        var metaDaConta = vinculos
            .Join(metas, v => v.MetaId, m => m.Id, (v, m) => (v.ContaId, Meta: m))
            .ToDictionary(x => x.ContaId, x => x.Meta);
        var contasDto = leituras.Contas.Values
            .Where(c => TiposDeInvestimento.Contains(c.Tipo))
            .Select(c =>
            {
                var meta = metaDaConta.GetValueOrDefault(c.Id);
                return new ContaDeInvestimentoDto(
                    c.Id, c.Apelido, c.Instituicao, c.Tipo, c.Arquivada, leituras.Saldos.GetValueOrDefault(c.Id), meta?.Id, meta?.Nome);
            })
            .ToList();

        return new ResultadoDeMetas.Painel(new PainelDeMetasDto(Texto(leituras.Mes), dtos, contasDto));
    }

    public async Task<ResultadoDeMetas> CriarAsync(NovaMetaDto pedido, CancellationToken ct)
    {
        if (atual.CasalId is not { } casalId) return new ResultadoDeMetas.NaoEncontrado();
        var mes = MesCorrente();
        if (Validar(pedido, out var erros) is not { } p) return new ResultadoDeMetas.Invalido(erros);
        if (p.Data is { } data && data < mes) return Invalido("dataDesejada", MensagemDataPassada);

        var doCasal = await ContasDoCasalAsync(ct);
        if (ConferirContas(p.Contas, doCasal, []) is { } recusa) return recusa;

        var meta = Meta.Criar(casalId, p.Nome, p.Alvo, p.Data, p.Aporte, p.Taxa);
        var resultado = await NaTravaDoCasalAsync(casalId, async () =>
        {
            if (await ConflitoAsync(null, p, ct) is { } conflito) return conflito;
            db.Metas.Add(meta);
            await db.SaveChangesAsync(ct); // a meta antes dos vínculos (FK).
            db.MetasContas.AddRange(p.Contas.Select(c => MetaConta.Criar(casalId, meta.Id, c)));
            return new ResultadoDeMetas.Feito(meta.Id);
        }, ct);
        if (resultado is not ResultadoDeMetas.Feito) return resultado;

        var vinculos = await db.MetasContas.AsNoTracking().Where(v => v.MetaId == meta.Id).ToListAsync(ct);
        return new ResultadoDeMetas.Criada(Montar(meta, vinculos, await LerAsync(ct), meta.AportePlanejadoCentavos));
    }

    public async Task<ResultadoDeMetas> EditarAsync(Guid id, NovaMetaDto pedido, CancellationToken ct)
    {
        if (atual.CasalId is not { } casalId) return new ResultadoDeMetas.NaoEncontrado();
        var mes = MesCorrente();
        if (Validar(pedido, out var erros) is not { } p) return new ResultadoDeMetas.Invalido(erros);
        var doCasal = await ContasDoCasalAsync(ct);

        return await NaTravaDoCasalAsync(casalId, async () =>
        {
            var meta = await db.Metas.FirstOrDefaultAsync(m => m.Id == id, ct);
            if (meta is null) return new ResultadoDeMetas.NaoEncontrado();
            // Uma data passada que já estava gravada pode ficar; criar ou mudar para uma data passada, não.
            if (p.Data is { } data && data < mes && data != meta.DataDesejada) return Invalido("dataDesejada", MensagemDataPassada);

            var vinculos = await db.MetasContas.Where(v => v.MetaId == id).ToListAsync(ct);
            var atuais = vinculos.Select(v => v.ContaId).ToHashSet();
            if (ConferirContas(p.Contas, doCasal, atuais) is { } recusa) return recusa;
            if (await ConflitoAsync(id, p, ct) is { } conflito) return conflito;

            meta.Definir(p.Nome, p.Alvo, p.Data, p.Aporte, p.Taxa);
            db.MetasContas.RemoveRange(vinculos.Where(v => !p.Contas.Contains(v.ContaId))); // soft delete pelo interceptor.
            db.MetasContas.AddRange(p.Contas.Where(c => !atuais.Contains(c)).Select(c => MetaConta.Criar(casalId, id, c)));
            return new ResultadoDeMetas.Feito(id);
        }, ct);
    }

    public async Task<ResultadoDeMetas> ApagarAsync(Guid id, CancellationToken ct)
    {
        if (atual.CasalId is not { } casalId) return new ResultadoDeMetas.NaoEncontrado();

        return await NaTravaDoCasalAsync(casalId, async () =>
        {
            var meta = await db.Metas.FirstOrDefaultAsync(m => m.Id == id, ct);
            if (meta is null) return new ResultadoDeMetas.NaoEncontrado();
            db.MetasContas.RemoveRange(await db.MetasContas.Where(v => v.MetaId == id).ToListAsync(ct));
            db.Metas.Remove(meta); // SoftDeleteInterceptor converte em is_deleted = true.
            return new ResultadoDeMetas.Feito(id);
        }, ct);
    }

    /// <summary>Mesmas regras do painel com <paramref name="aporte"/> no lugar do planejado. Só leitura.</summary>
    public async Task<ResultadoDeMetas> SimularAsync(Guid id, long? aporte, CancellationToken ct)
    {
        if (atual.CasalId is null) return new ResultadoDeMetas.NaoEncontrado();
        if (aporte is not { } valor) return Invalido("aporte", "Informe o aporte.");
        if (valor is < 0 or > LimitesDaMeta.ValorMaximo) return Invalido("aporte", MensagemDoAporte);

        var meta = await db.Metas.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
        if (meta is null) return new ResultadoDeMetas.NaoEncontrado();
        var vinculos = await db.MetasContas.AsNoTracking().Where(v => v.MetaId == id).ToListAsync(ct);
        return new ResultadoDeMetas.Simulacao(Montar(meta, vinculos, await LerAsync(ct), valor).Projecao);
    }

    private const string MensagemDataPassada = "A data desejada deve ser a partir do mês atual.";
    private const string MensagemDoAporte = "O aporte deve ficar entre 0 e 100.000.000.000 centavos.";

    private static MetaPedida? Validar(NovaMetaDto p, out Dictionary<string, string[]> erros)
    {
        erros = [];
        var nome = p.Nome?.Trim() ?? "";
        if (nome.Length is < 1 or > LimitesDaMeta.NomeMaximo) erros["nome"] = ["O nome deve ter de 1 a 60 caracteres."];

        if (p.ValorAlvoCentavos is not { } alvo) erros["valorAlvoCentavos"] = ["Informe o valor alvo."];
        else if (alvo is < 1 or > LimitesDaMeta.ValorMaximo)
            erros["valorAlvoCentavos"] = ["O valor alvo deve ficar entre 1 e 100.000.000.000 centavos."];

        if (p.AportePlanejadoCentavos is not { } aporte) erros["aportePlanejadoCentavos"] = ["Informe o aporte planejado."];
        else if (aporte is < 0 or > LimitesDaMeta.ValorMaximo) erros["aportePlanejadoCentavos"] = [MensagemDoAporte];

        if (p.RendimentoMensalPercentual is not { } rendimento)
            erros["rendimentoMensalPercentual"] = ["Informe o rendimento estimado."];
        else if (rendimento < 0 || rendimento > LimitesDaMeta.TaxaMaxima * 100 || decimal.Round(rendimento, 4) != rendimento)
            erros["rendimentoMensalPercentual"] = ["O rendimento deve ficar entre 0 e 5% ao mês, com até 4 casas."];

        DateOnly? data = null;
        if (!string.IsNullOrWhiteSpace(p.DataDesejada))
        {
            if (DateOnly.TryParseExact(p.DataDesejada, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var lida))
                data = lida;
            else
                erros["dataDesejada"] = ["Use o mês no formato AAAA-MM."];
        }

        var ids = p.ContaIds?.Distinct().ToArray() ?? [];
        if (ids.Length == 0) erros["contaIds"] = [MensagemSemContas];

        return erros.Count > 0
            ? null
            : new MetaPedida(nome, p.ValorAlvoCentavos!.Value, data, p.AportePlanejadoCentavos!.Value,
                CalculoDaMeta.FracaoDePercentual(p.RendimentoMensalPercentual!.Value), ids);
    }

    /// <summary>Conta inexistente ou de outro casal: 404; de outro tipo: 400; arquivada entrando: 400 (ficar pode).</summary>
    private static ResultadoDeMetas? ConferirContas(
        IReadOnlyList<Guid> ids, IReadOnlyDictionary<Guid, ContaResumo> doCasal, HashSet<Guid> atuais)
    {
        if (ids.Any(id => !doCasal.ContainsKey(id))) return new ResultadoDeMetas.NaoEncontrado();
        if (ids.Any(id => !TiposDeInvestimento.Contains(doCasal[id].Tipo))) return new ResultadoDeMetas.Recusado(TituloOutroTipo, 400);
        if (ids.Any(id => doCasal[id].Arquivada && !atuais.Contains(id))) return new ResultadoDeMetas.Recusado(TituloArquivada, 400);
        return null;
    }

    /// <summary>Nome repetido (sem caixa) ou conta já em outra meta, entre as linhas ativas do casal.</summary>
    private async Task<ResultadoDeMetas?> ConflitoAsync(Guid? id, MetaPedida p, CancellationToken ct)
    {
        // Traduzido para lower() do Postgres dos dois lados, a mesma regra do índice ux_metas_nome.
#pragma warning disable CA1304, CA1311, CA1862
        var nomeRepetido = await db.Metas.AnyAsync(m => m.Id != id && m.Nome.ToLower() == p.Nome.ToLower(), ct);
#pragma warning restore CA1304, CA1311, CA1862
        if (nomeRepetido) return new ResultadoDeMetas.Recusado(TituloNomeRepetido, 409);
        if (await db.MetasContas.AnyAsync(v => v.MetaId != id && p.Contas.Contains(v.ContaId), ct))
            return new ResultadoDeMetas.Recusado(TituloContaEmOutraMeta, 409);
        return null;
    }

    /// <summary>
    /// Transação com <c>pg_advisory_xact_lock</c> por casal: escritas de metas do casal ficam em fila. Se ainda assim um
    /// índice parcial acusar 23505 (alguém gravou sem o lock), vira o 409 correspondente. Só grava quando a ação dá certo.
    /// </summary>
    private async Task<ResultadoDeMetas> NaTravaDoCasalAsync(Guid casalId, Func<Task<ResultadoDeMetas>> acao, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended('investimentos.casal:' || {casalId}::text, 0))", ct);
        try
        {
            var resultado = await acao();
            if (resultado is not ResultadoDeMetas.Feito)
            {
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                return resultado;
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return resultado;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: IndiceDoNome or IndiceDaConta,
        } pg)
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return new ResultadoDeMetas.Recusado(pg.ConstraintName == IndiceDoNome ? TituloNomeRepetido : TituloContaEmOutraMeta, 409);
        }
    }

    private async Task<IReadOnlyDictionary<Guid, ContaResumo>> ContasDoCasalAsync(CancellationToken ct) =>
        (await contas.ListarAsync(ct)).ToDictionary(c => c.Id);

    private async Task<Leituras> LerAsync(CancellationToken ct)
    {
        var hoje = Brasilia.Hoje(relogio);
        var mes = new DateOnly(hoje.Year, hoje.Month, 1);
        var doCasal = await ContasDoCasalAsync(ct);
        var saldosDeHoje = (await saldos.SaldosAsync(hoje, ct)).ToDictionary(s => s.ContaId, s => s.SaldoCentavos);
        var aportes = (await uso.AportesPorContaAsync(mes, ct)).ToDictionary(a => a.ContaId, a => a.AporteCentavos);
        return new Leituras(mes, doCasal, saldosDeHoje, aportes);
    }

    /// <summary>
    /// Uma conta vinculada que não existe mais no Contas (apagada) some da meta sem erro; as demais seguem contando.
    /// </summary>
    private static MetaDto Montar(Meta meta, IReadOnlyList<MetaConta> vinculos, Leituras l, long aporteMensal)
    {
        var contasDaMeta = vinculos
            .Where(v => v.MetaId == meta.Id && l.Contas.ContainsKey(v.ContaId))
            .Select(v => l.Contas[v.ContaId])
            .OrderBy(c => c.Apelido, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new ContaDaMetaDto(c.Id, c.Apelido, c.Tipo, l.Saldos.GetValueOrDefault(c.Id), c.Arquivada))
            .ToList();
        var guardado = contasDaMeta.Sum(c => c.SaldoCentavos);
        var aporteDoMes = contasDaMeta.Sum(c => l.Aportes.GetValueOrDefault(c.ContaId));
        var projecao = CalculoDaMeta.Projetar(
            l.Mes, guardado, meta.ValorAlvoCentavos, aporteMensal, aporteDoMes, meta.TaxaMensal, meta.DataDesejada);

        return new MetaDto(
            meta.Id, meta.Nome, meta.ValorAlvoCentavos, meta.DataDesejada is { } d ? Texto(d) : null,
            meta.AportePlanejadoCentavos, CalculoDaMeta.PercentualDeFracao(meta.TaxaMensal), contasDaMeta,
            guardado, CalculoDaMeta.Progresso(guardado, meta.ValorAlvoCentavos), aporteDoMes, projecao);
    }

    private DateOnly MesCorrente()
    {
        var hoje = Brasilia.Hoje(relogio);
        return new DateOnly(hoje.Year, hoje.Month, 1);
    }

    private static string Texto(DateOnly mes) => mes.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static ResultadoDeMetas.Invalido Invalido(string campo, string mensagem) =>
        new(new Dictionary<string, string[]> { [campo] = [mensagem] });
}
