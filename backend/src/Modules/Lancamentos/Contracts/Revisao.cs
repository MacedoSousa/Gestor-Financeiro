namespace GestorFinanceiro.Modules.Lancamentos.Contracts;

/// <summary>
/// Um grupo de lançamentos "a revisar" (Fase 2, Tarefa 5.2): mesma descrição do banco normalizada e mesma natureza. Os
/// lançamentos ainda não entram no orçamento (D14).
/// </summary>
/// <param name="Descricao">A descrição do banco do lançamento mais recente do grupo.</param>
/// <param name="Natureza">DESPESA ou RECEITA.</param>
/// <param name="TotalCentavos">Soma com sinal (despesa negativa).</param>
/// <param name="CategoriaSugeridaId">A categoria que a regra ou o mapa sugeriu, quando é a mesma em todo o grupo.</param>
/// <param name="CategoriaDoBanco">Código da categoria da Pluggy (o mais comum no grupo), para "sempre que o banco disser".</param>
/// <param name="NomeDaCategoriaDoBanco">Nome genérico dessa categoria na Pluggy.</param>
public sealed record GrupoARevisarDto(string Descricao, string Natureza, int Quantidade, long TotalCentavos, Guid? CategoriaSugeridaId,
    string? CategoriaDoBanco, string? NomeDaCategoriaDoBanco, IReadOnlyList<Guid> Ids);

/// <summary>Corpo de POST /api/v1/lancamentos/revisao: revisa o lote inteiro ou nada (atômico).</summary>
/// <param name="Ids">De 1 a 500 lançamentos "a revisar" do casal, todos da mesma natureza.</param>
/// <param name="Aplicar">
/// SO_ESTA: só estes. SEMPRE: também cria ou atualiza a regra pela descrição do banco do primeiro. SEMPRE_DO_BANCO: também
/// grava o mapa pela categoria da Pluggy (o único caminho que grava o mapa, decisão 2).
/// </param>
/// <param name="CategoriaDoBanco">
/// Com SEMPRE_DO_BANCO: o código que a tela mostrou no botão (tem de estar no lote). Sem ele, vale o mais comum do lote,
/// como na lista.
/// </param>
public sealed record PedidoDeRevisaoDto(IReadOnlyList<Guid>? Ids, Guid? CategoriaId, string? Aplicar, string? CategoriaDoBanco = null);

/// <summary>Uma entrada do mapa "sempre que o banco disser &lt;categoria&gt;" (decisão 2), editável na tela de revisão.</summary>
public sealed record CategoriaExternaDto(Guid Id, string CategoriaExterna, string? Nome, Guid CategoriaId);

/// <summary>Corpo de PUT /api/v1/lancamentos/categorias-externas/{id}.</summary>
public sealed record MudancaDeCategoriaExternaDto(Guid? CategoriaId);
