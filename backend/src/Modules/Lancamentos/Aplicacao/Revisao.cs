using GestorFinanceiro.Modules.Lancamentos.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Dominio;
using GestorFinanceiro.Modules.Lancamentos.Infra;
using GestorFinanceiro.SharedKernel.Aplicacao;
using GestorFinanceiro.SharedKernel.Seguranca;
using Microsoft.EntityFrameworkCore;

namespace GestorFinanceiro.Modules.Lancamentos.Aplicacao;

/// <summary>
/// Revisão em lote dos importados (Fase 2, Tarefa 5.2). Atômica: o lote inteiro vira REVISADA com a categoria escolhida, e
/// a regra ou o mapa (se pedidos) gravam na mesma transação, ou nada muda. Só linha "a revisar": o que o casal já revisou
/// não é tocado (o lote é recusado). Sob a trava do casal (a mesma da importação) e a da categoria (a mesma do apagar
/// categoria). Mensagens sem dado do lançamento (regra 5).
/// </summary>
internal sealed class Revisao(LancamentosDbContext db, IUsuarioAtual atual, ICofre cofre)
{
    internal const int LoteMaximo = 500;
    internal const string TituloJaRevisado = "Algum lançamento do lote já foi revisado. Atualize a página.";
    internal const string TituloCategoriaArquivada = "Categoria arquivada: escolha outra.";

    /// <summary>D23: o que a etapa 6 reconhece não é revisado nem mapeado à mão até lá (a tela mostra a mesma frase).</summary>
    internal const string TituloReconhecidoNaProximaEtapa =
        "Pagamento de fatura e transferência entre as contas de vocês são reconhecidos na próxima etapa; não entram no orçamento.";
    private static readonly string[] Aplicacoes = ["SO_ESTA", "SEMPRE", "SEMPRE_DO_BANCO"];

    public async Task<IReadOnlyList<GrupoARevisarDto>?> ListarAsync(CancellationToken ct)
    {
        if (atual.CasalId is null) return null;
        var linhas = await db.Transacoes.AsNoTracking()
            .Where(t => t.Revisao == RevisaoDaTransacao.ARevisar && t.Natureza != NaturezaDaTransacao.Transferencia)
            .OrderByDescending(t => t.Data).ThenBy(t => t.Id)
            .ToListAsync(ct);

        return linhas
            // D23: pagamento de fatura, mesma titularidade e investimento esperam a etapa 6.
            .Where(t => !CategoriasDoBanco.ReconhecidoNaProximaEtapa(t.CategoriaExterna))
            .Select(t => (T: t, Descricao: t.LerDescricaoDoBanco(cofre)))
            .GroupBy(x => (Chave: SugestaoDeCategoria.Normalizada(x.T, cofre) ?? $"#{x.T.Id}", x.T.Natureza))
            // Grupo maior que o lote máximo vem em partes, cada uma revisável de uma vez.
            .SelectMany(g => g.Chunk(LoteMaximo).Select(parte => (g.Key.Natureza, Itens: parte)))
            .Select(g =>
            {
                var itens = g.Itens;
                var categorias = itens.Select(x => x.T.CategoriaId).Distinct().ToList();
                var (codigo, nome) = CodigoMaisComum(itens.Select(x => x.T));
                return new GrupoARevisarDto(itens[0].Descricao, Natureza(g.Natureza), itens.Length, itens.Sum(x => x.T.ValorCentavos),
                    categorias is [{ } unica] ? unica : null, codigo, nome, itens.Select(x => x.T.Id).ToList());
            })
            .OrderByDescending(g => g.Quantidade).ThenBy(g => g.TotalCentavos)
            .ToList();
    }

    public async Task<ResultadoDeLancamento> RevisarAsync(PedidoDeRevisaoDto pedido, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pedido);
        if (atual.CasalId is not { } casalId) return new ResultadoDeLancamento.NaoEncontrado();
        var ids = pedido.Ids?.Distinct().ToList() ?? [];
        var erros = new Dictionary<string, string[]>();
        if (ids.Count is 0 or > LoteMaximo || ids.Count != pedido.Ids!.Count)
            erros["ids"] = [$"Escolha de 1 a {LoteMaximo} lançamentos, sem repetir."];
        if (pedido.CategoriaId is null) erros["categoriaId"] = ["Escolha a categoria."];
        if (pedido.Aplicar is null || !Aplicacoes.Contains(pedido.Aplicar, StringComparer.Ordinal))
            erros["aplicar"] = ["Use SO_ESTA, SEMPRE ou SEMPRE_DO_BANCO."];
        if (erros.Count > 0) return new ResultadoDeLancamento.Invalido(erros);
        var categoriaId = pedido.CategoriaId!.Value;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended('lancamentos.casal:' || {casalId}::text, 0))", ct);

            // Trava das contas do lote (a mesma da edição e da importação), depois da do casal e antes da categoria: uma edição à
            // mão em curso termina antes, e a linha que ela revisou é lida já REVISADA (o lote então é recusado).
            var contasDoLote = await db.Transacoes.AsNoTracking().Where(t => ids.Contains(t.Id)).Select(t => t.ContaId).Distinct().ToListAsync(ct);
            await TravaDeConta.TomarAsync(db, contasDoLote, ct);
            var lote = await db.Transacoes.Where(t => ids.Contains(t.Id)).ToListAsync(ct);
            if (lote.Count != ids.Count) return new ResultadoDeLancamento.NaoEncontrado();
            if (lote.Any(t => t.Revisao != RevisaoDaTransacao.ARevisar)) return new ResultadoDeLancamento.Recusado(TituloJaRevisado, 409);
            if (lote.Any(t => CategoriasDoBanco.ReconhecidoNaProximaEtapa(t.CategoriaExterna)))
                return new ResultadoDeLancamento.Recusado(TituloReconhecidoNaProximaEtapa, 400);
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended('lancamentos.categoria:' || {categoriaId}::text, 0))", ct);
            var naturezas = lote.Select(t => t.Natureza).Distinct().ToList();
            if (naturezas is not [var natureza] || natureza == NaturezaDaTransacao.Transferencia)
                return Invalido("ids", "Revise despesas e receitas em lotes separados.");

            var categoria = await db.Categorias.AsNoTracking().FirstOrDefaultAsync(c => c.Id == categoriaId, ct);
            if (categoria is null) return Invalido("categoriaId", "Categoria não encontrada.");
            if (!SugestaoDeCategoria.Serve(categoria, natureza)) return Invalido("categoriaId", "A categoria não é do tipo do lançamento.");
            if (categoria.Arquivada) return new ResultadoDeLancamento.Recusado(TituloCategoriaArquivada, 400);

            // O primeiro do pedido decide a regra e o mapa (o grupo da tela tem a mesma descrição).
            var primeiro = lote.First(t => t.Id == ids[0]);
            var recusa = pedido.Aplicar switch
            {
                "SEMPRE" => await GravarRegraAsync(casalId, primeiro, categoriaId, ct),
                "SEMPRE_DO_BANCO" => await GravarMapaAsync(casalId, lote, pedido.CategoriaDoBanco, categoriaId, ct),
                _ => null,
            };
            if (recusa is not null) return recusa;

            foreach (var t in lote) t.Revisar(categoriaId);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new ResultadoDeLancamento.Feito();
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private async Task<ResultadoDeLancamento?> GravarRegraAsync(Guid casalId, Transacao primeiro, Guid categoriaId, CancellationToken ct)
    {
        if (SugestaoDeCategoria.Normalizada(primeiro, cofre) is not { } normalizada)
            return Invalido("aplicar", "Lançamento sem descrição do banco não vira regra.");
        var hmac = RegraDeCategoria.Indice(casalId, normalizada, cofre);
        var regra = await db.Regras.FirstOrDefaultAsync(r => r.DescricaoHmac == hmac, ct);
        if (regra is null) db.Regras.Add(RegraDeCategoria.Criar(casalId, normalizada, categoriaId, cofre));
        else regra.Apontar(categoriaId);
        return null;
    }

    /// <summary>
    /// O código que a tela mostrou no botão (tem de estar no lote) ou, sem ele, o mais comum do lote, com a mesma regra da
    /// lista. Só código de 8 dígitos que veio do banco numa linha do lote.
    /// </summary>
    private async Task<ResultadoDeLancamento?> GravarMapaAsync(Guid casalId, IReadOnlyList<Transacao> lote, string? pedido, Guid categoriaId,
        CancellationToken ct)
    {
        var (codigo, nome) = pedido is null
            ? CodigoMaisComum(lote)
            : (pedido, lote.Where(t => t.CategoriaExterna == pedido).Select(t => t.CategoriaExternaNome).FirstOrDefault(n => n is not null));
        if (CategoriasDoBanco.ReconhecidoNaProximaEtapa(codigo)) return new ResultadoDeLancamento.Recusado(TituloReconhecidoNaProximaEtapa, 400);
        if (codigo is null || !MapaDeCategoriaExterna.CodigoValido(codigo) || !lote.Any(t => t.CategoriaExterna == codigo))
            return Invalido("categoriaDoBanco", "O banco não mandou essa categoria nestes lançamentos.");
        var entrada = await db.Mapa.FirstOrDefaultAsync(m => m.CategoriaExterna == codigo, ct);
        if (entrada is null) db.Mapa.Add(MapaDeCategoriaExterna.Criar(casalId, codigo, nome, categoriaId));
        else entrada.Apontar(categoriaId, nome);
        return null;
    }

    /// <summary>O código da Pluggy mais comum entre as linhas (empate: o menor), com o nome dele; nulo se nenhuma tem.</summary>
    private static (string? Codigo, string? Nome) CodigoMaisComum(IEnumerable<Transacao> linhas) =>
        linhas.Where(t => t.CategoriaExterna is not null)
            .GroupBy(t => t.CategoriaExterna!, StringComparer.Ordinal)
            .OrderByDescending(c => c.Count()).ThenBy(c => c.Key, StringComparer.Ordinal)
            .Select(c => ((string?)c.Key, c.Select(t => t.CategoriaExternaNome).FirstOrDefault(n => n is not null)))
            .FirstOrDefault();

    public async Task<IReadOnlyList<CategoriaExternaDto>?> MapaAsync(CancellationToken ct) =>
        atual.CasalId is null
            ? null
            : (await db.Mapa.AsNoTracking().OrderBy(m => m.CategoriaExterna).ToListAsync(ct))
                .Select(m => new CategoriaExternaDto(m.Id, m.CategoriaExterna, m.NomeExterno, m.CategoriaId)).ToList();

    /// <summary>Edição do mapa na tela (decisão 2): só troca a categoria do casal; o código do banco não muda.</summary>
    public async Task<ResultadoDeLancamento> ApontarMapaAsync(Guid id, MudancaDeCategoriaExternaDto pedido, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pedido);
        if (atual.CasalId is null) return new ResultadoDeLancamento.NaoEncontrado();
        if (pedido.CategoriaId is not { } categoriaId) return Invalido("categoriaId", "Escolha a categoria.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended('lancamentos.categoria:' || {categoriaId}::text, 0))", ct);
        var entrada = await db.Mapa.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (entrada is null) return new ResultadoDeLancamento.NaoEncontrado();
        if (CategoriasDoBanco.ReconhecidoNaProximaEtapa(entrada.CategoriaExterna))
            return new ResultadoDeLancamento.Recusado(TituloReconhecidoNaProximaEtapa, 400);
        var categoria = await db.Categorias.AsNoTracking().FirstOrDefaultAsync(c => c.Id == categoriaId, ct);
        if (categoria is null) return Invalido("categoriaId", "Categoria não encontrada.");
        if (categoria.Arquivada) return new ResultadoDeLancamento.Recusado(TituloCategoriaArquivada, 400);
        entrada.Apontar(categoriaId, null);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new ResultadoDeLancamento.Feito();
    }

    private static string Natureza(NaturezaDaTransacao n) => n == NaturezaDaTransacao.Receita ? "RECEITA" : "DESPESA";

    private static ResultadoDeLancamento.Invalido Invalido(string campo, string mensagem) =>
        new(new Dictionary<string, string[]> { [campo] = [mensagem] });
}
