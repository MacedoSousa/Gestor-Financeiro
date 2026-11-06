using GestorFinanceiro.SharedKernel.Dominio;

namespace GestorFinanceiro.Modules.Lancamentos.Dominio;

internal enum TipoDeCategoria
{
    Despesa,
    Receita,
}

/// <summary>Categoria do casal. Arquivar não apaga: os lançamentos antigos continuam apontando para ela.</summary>
internal sealed class Categoria : Entidade, ICasalScoped
{
    public Guid CasalId { get; private set; }
    public string Nome { get; private set; } = string.Empty;
    public TipoDeCategoria Tipo { get; private set; }
    public bool Arquivada { get; private set; }

    /// <summary>INVESTIMENTO e DIVIDAS (despesa) ou RENDIMENTOS (receita) (<see cref="Contracts.PapelDeCategoria"/>); nulo na maioria. Não muda depois de criada.</summary>
    public string? Papel { get; private set; }

    private Categoria() { }

    public static Categoria Criar(Guid casalId, string nome, TipoDeCategoria tipo, string? papel = null) =>
        new() { CasalId = casalId, Nome = nome, Tipo = tipo, Papel = papel };

    private static TipoDeCategoria TipoDoPapel(string papel) =>
        papel == Contracts.PapelDeCategoria.Rendimentos ? TipoDeCategoria.Receita : TipoDeCategoria.Despesa;

    public void Definir(string nome, TipoDeCategoria tipo, bool arquivada)
    {
        if (Papel is not null && tipo != TipoDoPapel(Papel))
            throw new InvalidOperationException("Categoria com papel não muda de tipo.");
        Nome = nome;
        Tipo = tipo;
        Arquivada = arquivada;
    }
}
