using GestorFinanceiro.SharedKernel.Dominio;
using GestorFinanceiro.SharedKernel.Seguranca;

namespace GestorFinanceiro.Modules.Lancamentos.Dominio;

/// <summary>
/// "Sempre que vier assim" (Fase 2, Tarefa 5.1): a descrição do banco normalizada aponta uma categoria que é só SUGERIDA a
/// linhas novas "a revisar". A descrição só existe cifrada; a busca é pelo índice cego (regra 4).
/// </summary>
internal sealed class RegraDeCategoria : Entidade, ICasalScoped
{
    internal const string Tabela = "lancamentos.regras_de_categoria";
    internal const string ColunaDescricao = "descricao_normalizada_cifrada";

    /// <summary>Propósito do índice cego da descrição normalizada.</summary>
    internal const string Proposito = "lancamentos.regra";

    public Guid CasalId { get; private set; }
    public byte[] DescricaoNormalizadaCifrada { get; private set; } = [];
    public byte[] DescricaoHmac { get; private set; } = [];
    public Guid CategoriaId { get; private set; }

    private RegraDeCategoria() { }

    /// <summary>Índice cego da descrição normalizada no casal (o casal entra no texto: casais diferentes não se cruzam).</summary>
    public static byte[] Indice(Guid casalId, string normalizada, ICofre cofre) => cofre.IndiceCego($"{casalId:N}|{normalizada}", Proposito);

    public static RegraDeCategoria Criar(Guid casalId, string normalizada, Guid categoriaId, ICofre cofre)
    {
        if (string.IsNullOrEmpty(normalizada)) throw new ArgumentException("Descrição vazia não vira regra.", nameof(normalizada));
        var regra = new RegraDeCategoria { CasalId = casalId, CategoriaId = categoriaId, DescricaoHmac = Indice(casalId, normalizada, cofre) };
        regra.DescricaoNormalizadaCifrada = cofre.Cifrar(normalizada, Cofre.Aad(Tabela, ColunaDescricao, regra.Id));
        return regra;
    }

    public void Apontar(Guid categoriaId) => CategoriaId = categoriaId;
}

/// <summary>
/// "Sempre que o banco disser &lt;categoria&gt;" (Fase 2, decisão 2): o código da categoria da Pluggy aponta uma categoria do
/// casal, só SUGERIDA a linhas novas "a revisar". Só o botão grava aqui (nada de aprendizado silencioso); editável na tela.
/// </summary>
internal sealed class MapaDeCategoriaExterna : Entidade, ICasalScoped
{
    public Guid CasalId { get; private set; }
    public string CategoriaExterna { get; private set; } = string.Empty;
    public string? NomeExterno { get; private set; }
    public Guid CategoriaId { get; private set; }

    private MapaDeCategoriaExterna() { }

    /// <summary>O código da Pluggy tem 8 dígitos; qualquer outra coisa é recusada (também pelo CHECK do banco).</summary>
    public static bool CodigoValido(string? codigo) => codigo is { Length: 8 } && codigo.All(char.IsAsciiDigit);

    public static MapaDeCategoriaExterna Criar(Guid casalId, string categoriaExterna, string? nomeExterno, Guid categoriaId)
    {
        if (!CodigoValido(categoriaExterna)) throw new ArgumentException("Código de categoria do banco inválido.", nameof(categoriaExterna));
        return new MapaDeCategoriaExterna { CasalId = casalId, CategoriaExterna = categoriaExterna, NomeExterno = nomeExterno, CategoriaId = categoriaId };
    }

    public void Apontar(Guid categoriaId, string? nomeExterno)
    {
        CategoriaId = categoriaId;
        if (nomeExterno is not null) NomeExterno = nomeExterno;
    }
}
