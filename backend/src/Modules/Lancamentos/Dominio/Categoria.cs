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

    private Categoria() { }

    public static Categoria Criar(Guid casalId, string nome, TipoDeCategoria tipo) =>
        new() { CasalId = casalId, Nome = nome, Tipo = tipo };

    public void Definir(string nome, TipoDeCategoria tipo, bool arquivada)
    {
        Nome = nome;
        Tipo = tipo;
        Arquivada = arquivada;
    }
}
