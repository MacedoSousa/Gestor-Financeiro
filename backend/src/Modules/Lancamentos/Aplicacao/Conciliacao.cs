using GestorFinanceiro.Modules.Contas.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Dominio;
using GestorFinanceiro.Modules.Lancamentos.Infra;
using GestorFinanceiro.SharedKernel.Aplicacao;
using GestorFinanceiro.SharedKernel.Dominio;
using GestorFinanceiro.SharedKernel.Seguranca;
using Microsoft.EntityFrameworkCore;

namespace GestorFinanceiro.Modules.Lancamentos.Aplicacao;

internal abstract record ResultadoDaConciliacao
{
    public sealed record Feita(ConciliacaoDto Conciliacao) : ResultadoDaConciliacao;
    public sealed record Invalida(IReadOnlyDictionary<string, string[]> Erros) : ResultadoDaConciliacao;
    public sealed record Recusada(string Titulo, int Status) : ResultadoDaConciliacao;
    public sealed record NaoEncontrada : ResultadoDaConciliacao;
}

/// <summary>
/// Conciliação de saldo de poupança ou investimento. Sob o lock do casal, recalcula o saldo de hoje (o mesmo cálculo do
/// GET /saldos), recusa tela desatualizada e, se o banco tem mais, lança a diferença como receita em Rendimentos.
/// Uma segunda conciliação igual em paralelo espera o lock, encontra o saldo já atualizado e recebe 409.
/// Saldos e descrição nunca vão para log.
/// </summary>
internal sealed class ServicoDeConciliacao(LancamentosDbContext db, IUsuarioAtual atual, IConsultaDeContas contas,
    ConsultaDeSaldosPorConta saldos, ServicoDeCategorias categorias, ConsultaDoExtrato extrato, ICofre cofre, TimeProvider relogio)
{
    internal const string Lancado = "LANCADO";
    internal const string Confere = "CONFERE";
    internal const string Menor = "MENOR";
    internal const string TituloTipoDeConta = "Só contas de poupança ou investimento são conciliadas aqui.";
    internal const string TituloContaLigada = "Esta conta vem do banco. O saldo é conferido sozinho.";
    internal const string TituloTelaDesatualizada = "O saldo mudou desde que a tela foi aberta. Confira de novo.";
    internal const string Descricao = "Rendimentos (conciliação)";
    internal const long LimiteDoSaldo = 100_000_000_000;
    private const string NomeDaCategoria = "Rendimentos";
    private const string NomeAlternativo = "Rendimentos (conciliação)";

    public async Task<ResultadoDaConciliacao> ConciliarAsync(Guid contaId, ConciliacaoPedidoDto p, CancellationToken ct)
    {
        if (atual.CasalId is not { } casalId) return new ResultadoDaConciliacao.NaoEncontrada();
        var erros = new Dictionary<string, string[]>();
        ValidarSaldo(erros, "saldoInformadoCentavos", p.SaldoInformadoCentavos);
        ValidarSaldo(erros, "saldoExibidoCentavos", p.SaldoExibidoCentavos);
        if (erros.Count > 0) return new ResultadoDaConciliacao.Invalida(erros);
        var informado = p.SaldoInformadoCentavos!.Value;
        var exibido = p.SaldoExibidoCentavos!.Value;

        if ((await ContaConciliavelAsync(contaId, ct)).Recusa is { } recusa) return recusa;
        // Fora da transação (ela abre a própria): sem isto, criar só "Rendimentos" num casal sem categorias impediria as padrão.
        await categorias.GarantirPadraoAsync(casalId, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended('lancamentos.casal:' || {casalId}::text, 0))", ct);

        // Depois do lock: a conta pode ter sido arquivada (ou apagada) enquanto esperávamos.
        await TravaDeConta.TomarAsync(db, [contaId], ct);
        var (conta, recusaNoLock) = await ContaConciliavelAsync(contaId, ct);
        if (recusaNoLock is not null) return recusaNoLock;
        var hoje = Brasilia.Hoje(relogio);
        var saldoDeHoje = (await saldos.SaldosAsync([conta], hoje, ct))[conta.Id];
        if (saldoDeHoje != exibido) return new ResultadoDaConciliacao.Recusada(TituloTelaDesatualizada, 409);

        var diferenca = informado - saldoDeHoje;
        if (diferenca <= 0)
        {
            await tx.CommitAsync(ct);
            return new ResultadoDaConciliacao.Feita(new ConciliacaoDto(diferenca == 0 ? Confere : Menor, diferenca, null));
        }

        var categoriaId = await CategoriaDeRendimentosAsync(casalId, ct);
        var receita = Transacao.CriarReceita(casalId, conta.Id, categoriaId, hoje, new Dinheiro(diferenca), Descricao, null,
            OrigemDaTransacao.Manual, "conciliacao:" + Guid.CreateVersion7().ToString("D"), atual.UsuarioId, null, cofre);
        db.Transacoes.Add(receita);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var dto = (await extrato.MontarAsync([receita], casalId, null, ct)).Single();
        return new ResultadoDaConciliacao.Feita(new ConciliacaoDto(Lancado, diferenca, dto));
    }

    /// <summary>Conta do casal (senão 404), poupança ou investimento e não arquivada (senão 400).</summary>
    private async Task<(ContaResumo Conta, ResultadoDaConciliacao? Recusa)> ContaConciliavelAsync(Guid contaId, CancellationToken ct)
    {
        var conta = await contas.ObterAsync(contaId, ct);
        if (conta is null) return (null!, new ResultadoDaConciliacao.NaoEncontrada());
        if (conta.Ligada) return (conta, new ResultadoDaConciliacao.Recusada(TituloContaLigada, 409)); // D11: é automática
        if (conta.Tipo is not ("POUPANCA" or "INVESTIMENTO")) return (conta, new ResultadoDaConciliacao.Recusada(TituloTipoDeConta, 400));
        if (conta.Arquivada) return (conta, new ResultadoDaConciliacao.Recusada(ServicoDeLancamentos.TituloContaArquivada, 400));
        return (conta, null);
    }

    /// <summary>
    /// A categoria com papel RENDIMENTOS, mesmo arquivada (arquivar só a esconde dos formulários). Se o casal não tem
    /// (a V0009 pula quem já tinha uma despesa "Rendimentos"), cria "Rendimentos", ou "Rendimentos (conciliação)" se o
    /// nome estiver tomado. Roda dentro da transação com o lock do casal.
    /// </summary>
    private async Task<Guid> CategoriaDeRendimentosAsync(Guid casalId, CancellationToken ct)
    {
        var existente = await db.Categorias.AsNoTracking()
            .Where(c => c.Papel == PapelDeCategoria.Rendimentos).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
        if (existente is { } id) return id;

        // Mesma regra do índice ux_categorias_nome: sem caixa, entre as não apagadas do casal (os filtros estão ligados).
        var tomados = (await db.Categorias.AsNoTracking().Select(c => c.Nome).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nome = NomeDaCategoria;
        for (var n = 1; tomados.Contains(nome); n++)
            nome = n == 1 ? NomeAlternativo : $"{NomeAlternativo} {n}";

        var categoria = Categoria.Criar(casalId, nome, TipoDeCategoria.Receita, PapelDeCategoria.Rendimentos);
        db.Categorias.Add(categoria);
        await db.SaveChangesAsync(ct); // Antes da receita: a chave estrangeira pede a categoria gravada.
        return categoria.Id;
    }

    private static void ValidarSaldo(Dictionary<string, string[]> erros, string campo, long? valor)
    {
        if (valor is null) erros[campo] = ["Informe o saldo."];
        else if (valor is < -LimiteDoSaldo or > LimiteDoSaldo) erros[campo] = ["O saldo deve estar entre −1 bilhão e 1 bilhão de reais."];
    }
}
