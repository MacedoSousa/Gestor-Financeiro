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

internal abstract record ResultadoDeLancamento
{
    public sealed record Criado(LancamentoDto Lancamento) : ResultadoDeLancamento;
    public sealed record Existente(LancamentoDto Lancamento) : ResultadoDeLancamento;
    public sealed record Feito : ResultadoDeLancamento;
    public sealed record Invalido(IReadOnlyDictionary<string, string[]> Erros) : ResultadoDeLancamento;
    public sealed record Recusado(string Titulo, int Status) : ResultadoDeLancamento;
    public sealed record NaoEncontrado : ResultadoDeLancamento;
}

/// <summary>
/// Lançamentos manuais (despesa, receita e parcelas no cartão). O casal vem da sessão. Descrição e contraparte
/// nunca vão para log nem para mensagem de erro.
/// </summary>
internal sealed class ServicoDeLancamentos(LancamentosDbContext db, IUsuarioAtual atual, IConsultaDeContas contas,
    ConsultaDoExtrato extrato, ICofre cofre, TimeProvider relogio)
{
    internal const string TituloContaArquivada = "Conta arquivada: escolha outra.";
    internal const string TituloCategoriaArquivada = "Categoria arquivada: escolha outra.";
    internal const string TituloChaveUsada = "Chave de idempotência já usada.";
    internal const string TituloFaturasFechadas = "As parcelas escolhidas estão em faturas já fechadas.";
    private const string IndiceDaChave = "ux_transacoes_origem_id_externo";
    private const int TamanhoMaximoDoTexto = 140;

    public async Task<ResultadoDeLancamento> CriarAsync(NovoLancamentoDto p, CancellationToken ct)
    {
        if (atual.CasalId is not { } casalId) return new ResultadoDeLancamento.NaoEncontrado();
        var erros = ValidarCampos(p.Natureza, p.ValorCentavos, p.Data, p.Descricao, p.Contraparte);
        if (p.ChaveIdempotente == Guid.Empty) erros["chaveIdempotente"] = ["Informe a chave de idempotência."];
        if (p.Parcelas is < 1 or > Parcelamento.MaximoDeParcelas) erros["parcelas"] = ["Parcelas devem ser de 1 a 24."];
        else if (p.Parcelas > 1 && p.Natureza != "DESPESA") erros["parcelas"] = ["Só despesa pode ser parcelada."];
        else if (p.Parcelas > 1 && p.ValorCentavos > 0 && p.ValorCentavos < p.Parcelas) erros["parcelas"] = ["Cada parcela precisa de ao menos um centavo."];
        if (erros.Count > 0) return new ResultadoDeLancamento.Invalido(erros);

        var chave = p.ChaveIdempotente.ToString("D");
        var chaveDaPrimeira = chave + ":1";

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Serializa POSTs com a mesma chave: o segundo espera o primeiro terminar e então encontra o lançamento.
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended('lancamentos.chave:' || {p.ChaveIdempotente}::text, 0))", ct);

        var existente = await db.Transacoes.AsNoTracking().FirstOrDefaultAsync(
            t => t.Origem == OrigemDaTransacao.Manual && (t.IdExterno == chave || t.IdExterno == chaveDaPrimeira), ct);
        if (existente is not null)
        {
            await tx.CommitAsync(ct);
            return new ResultadoDeLancamento.Existente((await extrato.MontarAsync([existente], casalId, null, ct)).Single());
        }

        var conta = await contas.ObterAsync(p.ContaId, ct);
        if (conta is null) return new ResultadoDeLancamento.NaoEncontrado();
        if (conta.Arquivada) return new ResultadoDeLancamento.Recusado(TituloContaArquivada, 400);
        if (p.Parcelas > 1 && conta.Tipo != "CARTAO")
            return Invalido("parcelas", "Parcelas só em cartão de crédito.");

        var natureza = LerNatureza(p.Natureza);
        if (await ValidarCategoriaAsync(p.CategoriaId, natureza, categoriaAtual: null, ct) is { } recusa) return recusa;

        var valores = Parcelamento.Dividir(new Dinheiro(p.ValorCentavos), p.Parcelas);
        Guid? grupo = p.Parcelas > 1 ? Guid.CreateVersion7() : null;
        var criadas = new List<Transacao>(p.Parcelas);
        for (var i = 0; i < p.Parcelas; i++)
        {
            var idExterno = p.Parcelas == 1 ? chave : $"{chave}:{i + 1}";
            var fatura = FaturaDaParcela(conta, p.Data, i);
            var t = natureza == NaturezaDaTransacao.Despesa
                ? Transacao.CriarDespesa(casalId, conta.Id, p.CategoriaId, p.Data.AddMonths(i), valores[i], p.Descricao, p.Contraparte,
                    OrigemDaTransacao.Manual, idExterno, atual.UsuarioId, fatura, cofre, grupo,
                    grupo is null ? null : i + 1, grupo is null ? null : p.Parcelas)
                : Transacao.CriarReceita(casalId, conta.Id, p.CategoriaId, p.Data, valores[i], p.Descricao, p.Contraparte,
                    OrigemDaTransacao.Manual, idExterno, atual.UsuarioId, fatura, cofre);
            criadas.Add(t);
        }

        db.Transacoes.AddRange(criadas);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: IndiceDaChave })
        {
            // Só acontece se a chave já foi usada por outro casal (a busca acima respeita o filtro de casal).
            return new ResultadoDeLancamento.Recusado(TituloChaveUsada, 409);
        }

        await tx.CommitAsync(ct);
        return new ResultadoDeLancamento.Criado((await extrato.MontarAsync([criadas[0]], casalId, null, ct)).Single());
    }

    /// <summary>
    /// Edita um lançamento. Em parcelado com escopo "proximos", aplica às parcelas do grupo com número maior ou igual
    /// ao da escolhida cuja fatura ainda não fechou; o valor informado é o de cada parcela, e mudar a data desloca as
    /// seguintes mês a mês (com a fatura recalculada).
    /// </summary>
    public async Task<ResultadoDeLancamento> EditarAsync(Guid id, string? escopo, EditarLancamentoDto p, CancellationToken ct)
    {
        if (atual.CasalId is null) return new ResultadoDeLancamento.NaoEncontrado();
        if (LerEscopo(escopo) is not { } proximas) return Invalido("escopo", "Escopo inválido: use este ou proximos.");
        var erros = ValidarCampos(p.Natureza, p.ValorCentavos, p.Data, p.Descricao, p.Contraparte);
        if (erros.Count > 0) return new ResultadoDeLancamento.Invalido(erros);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var alvo = await db.Transacoes.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (alvo is null) return new ResultadoDeLancamento.NaoEncontrado();
        if (alvo.Natureza == NaturezaDaTransacao.Transferencia)
            return Invalido("natureza", "Transferência se edita pelos dois lados, na rota de transferências.");
        var natureza = LerNatureza(p.Natureza);
        if (alvo.Parcela is not null && natureza != NaturezaDaTransacao.Despesa)
            return Invalido("natureza", "Só despesa pode ser parcelada.");
        if (await ValidarCategoriaAsync(p.CategoriaId, natureza, alvo.CategoriaId, ct) is { } recusa) return recusa;

        var conta = await contas.ObterAsync(alvo.ContaId, ct);
        var lista = await AlvosAsync(alvo, proximas, conta, ct);
        if (lista.Count == 0) return new ResultadoDeLancamento.Recusado(TituloFaturasFechadas, 409);

        var dataMudou = p.Data != alvo.Data;
        foreach (var t in lista)
        {
            var k = (t.Parcela ?? 1) - (alvo.Parcela ?? 1);
            var data = dataMudou ? p.Data.AddMonths(k) : t.Data;
            // Data nova recalcula a fatura (nula fora de cartão); sem mudar a data, a fatura fica.
            var fatura = dataMudou && conta is not null ? FaturaDaParcela(conta, p.Data, k) : t.FaturaVencimento;
            t.Editar(natureza, new Dinheiro(p.ValorCentavos), data, p.Descricao, p.Contraparte, p.CategoriaId, fatura, cofre);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new ResultadoDeLancamento.Feito();
    }

    /// <summary>
    /// Apaga (soft delete). Escopo "proximos" em parcelado: as parcelas do grupo a partir da escolhida, menos as de
    /// faturas já fechadas. Transferência apaga sempre os dois lados.
    /// </summary>
    public async Task<ResultadoDeLancamento> ApagarAsync(Guid id, string? escopo, CancellationToken ct)
    {
        if (atual.CasalId is null) return new ResultadoDeLancamento.NaoEncontrado();
        if (LerEscopo(escopo) is not { } proximas) return Invalido("escopo", "Escopo inválido: use este ou proximos.");

        var alvo = await db.Transacoes.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (alvo is null) return new ResultadoDeLancamento.NaoEncontrado();

        List<Transacao> lista;
        if (alvo is { Natureza: NaturezaDaTransacao.Transferencia, GrupoId: { } grupo })
            lista = await db.Transacoes.Where(t => t.GrupoId == grupo).ToListAsync(ct);
        else
            lista = await AlvosAsync(alvo, proximas, await contas.ObterAsync(alvo.ContaId, ct), ct);
        if (lista.Count == 0) return new ResultadoDeLancamento.Recusado(TituloFaturasFechadas, 409);

        db.Transacoes.RemoveRange(lista); // SoftDeleteInterceptor converte em is_deleted = true.
        await db.SaveChangesAsync(ct);
        return new ResultadoDeLancamento.Feito();
    }

    private async Task<List<Transacao>> AlvosAsync(Transacao alvo, bool proximas, ContaResumo? conta, CancellationToken ct)
    {
        if (!proximas || alvo is not { GrupoId: { } grupo, Parcela: { } parcela }) return [alvo];

        var hoje = Brasilia.Hoje(relogio);
        var doGrupo = await db.Transacoes.Where(t => t.GrupoId == grupo && t.Parcela >= parcela).OrderBy(t => t.Parcela).ToListAsync(ct);
        return doGrupo.Where(t => !FaturaFechada(conta, t, hoje)).ToList();
    }

    /// <summary>
    /// A fatura está fechada a partir do dia seguinte ao fechamento (compra no dia do fechamento ainda entra nela):
    /// fechada quando <c>fechamento &lt; hoje</c>, com hoje no fuso de Brasília.
    /// </summary>
    private bool FaturaFechada(ContaResumo? conta, Transacao t, DateOnly hoje) =>
        conta is not null && t.FaturaVencimento is { } vencimento
        && contas.FechamentoDaFatura(conta, vencimento) is { } fechamento && fechamento < hoje;

    /// <summary>
    /// Vencimento da fatura da parcela <paramref name="i"/> (0 = a primeira): a fatura da compra e as dos meses
    /// seguintes. Conta do mês do fechamento, para não perder dias quando o mês é curto. Nulo fora de cartão.
    /// </summary>
    private DateOnly? FaturaDaParcela(ContaResumo conta, DateOnly dataCompra, int i)
    {
        if (contas.VencimentoDaFatura(conta, dataCompra) is not { } primeira) return null;
        if (i == 0) return primeira;
        var fechamento = contas.FechamentoDaFatura(conta, primeira)!.Value;
        // Dia 1 nunca passa do fechamento: cai na fatura que fecha naquele mês.
        return contas.VencimentoDaFatura(conta, new DateOnly(fechamento.Year, fechamento.Month, 1).AddMonths(i));
    }

    /// <summary>
    /// Categoria válida para o lançamento: do casal, não apagada, do tipo da natureza e não arquivada (a não ser que
    /// seja a que o lançamento já usa). Toma o lock da categoria até o fim da transação, o mesmo do apagar categoria.
    /// </summary>
    private async Task<ResultadoDeLancamento?> ValidarCategoriaAsync(Guid categoriaId, NaturezaDaTransacao natureza, Guid? categoriaAtual,
        CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended('lancamentos.categoria:' || {categoriaId}::text, 0))", ct);
        var categoria = await db.Categorias.AsNoTracking().FirstOrDefaultAsync(c => c.Id == categoriaId, ct);
        if (categoria is null) return Invalido("categoriaId", "Categoria não encontrada.");
        var tipoEsperado = natureza == NaturezaDaTransacao.Receita ? TipoDeCategoria.Receita : TipoDeCategoria.Despesa;
        if (categoria.Tipo != tipoEsperado) return Invalido("categoriaId", "A categoria não é do tipo do lançamento.");
        if (categoria.Arquivada && categoria.Id != categoriaAtual) return new ResultadoDeLancamento.Recusado(TituloCategoriaArquivada, 400);
        return null;
    }

    private static Dictionary<string, string[]> ValidarCampos(string natureza, long valorCentavos, DateOnly data, string descricao, string? contraparte)
    {
        var erros = new Dictionary<string, string[]>();
        if (natureza is not ("DESPESA" or "RECEITA")) erros["natureza"] = ["Natureza inválida: use DESPESA ou RECEITA."];
        if (valorCentavos <= 0) erros["valorCentavos"] = ["Informe um valor positivo; o sinal vem da natureza."];
        if (data == default) erros["data"] = ["Informe a data."];
        if ((descricao?.Trim().Length ?? 0) is < 1 or > TamanhoMaximoDoTexto)
            erros["descricao"] = [$"A descrição deve ter de 1 a {TamanhoMaximoDoTexto} caracteres."];
        if ((contraparte?.Trim().Length ?? 0) > TamanhoMaximoDoTexto)
            erros["contraparte"] = [$"A contraparte deve ter até {TamanhoMaximoDoTexto} caracteres."];
        return erros;
    }

    /// <summary>true = "proximos", false = "este" (padrão), nulo = inválido.</summary>
    private static bool? LerEscopo(string? escopo) => escopo switch
    {
        null or "este" => false,
        "proximos" => true,
        _ => null,
    };

    private static NaturezaDaTransacao LerNatureza(string natureza) =>
        natureza == "RECEITA" ? NaturezaDaTransacao.Receita : NaturezaDaTransacao.Despesa;

    private static ResultadoDeLancamento.Invalido Invalido(string campo, string mensagem) =>
        new(new Dictionary<string, string[]> { [campo] = [mensagem] });
}
