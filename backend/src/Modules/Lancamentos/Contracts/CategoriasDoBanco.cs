using System.Collections.Frozen;

namespace GestorFinanceiro.Modules.Lancamentos.Contracts;

/// <summary>O que o reconhecimento automático (etapa 6) faz com uma transação, pela categoria que o banco mandou.</summary>
public enum PapelDaCategoriaDoBanco
{
    Nenhum,
    PagamentoDeFatura,
    Aplicacao,
    ResgateOuRendimento,
    MesmaTitularidade,
    Boleto,
    Iof,
    ImpostoDeRenda,
}

/// <summary>
/// A tabela dos códigos de categoria da Pluggy que o sistema reconhece, num lugar só (CEO 11/11 21h, decisão 13; o
/// teste de arquitetura recusa esses códigos em qualquer outro arquivo). Códigos medidos no Itaú em 11/11. Só o código
/// exato conta: subcategoria não listada ou transação sem categoria não tem papel, e cai em "A conferir".
/// </summary>
public static class CategoriasDoBanco
{
    public static readonly FrozenDictionary<string, PapelDaCategoriaDoBanco> Codigos = new Dictionary<string, PapelDaCategoriaDoBanco>
    {
        ["05100000"] = PapelDaCategoriaDoBanco.PagamentoDeFatura,   // Credit card payment
        ["03000000"] = PapelDaCategoriaDoBanco.Aplicacao,           // Investments
        ["03020000"] = PapelDaCategoriaDoBanco.Aplicacao,           // Fixed income
        ["03060000"] = PapelDaCategoriaDoBanco.ResgateOuRendimento, // Proceeds interests and dividends
        ["04000000"] = PapelDaCategoriaDoBanco.MesmaTitularidade,   // Same person transfer
        ["05010000"] = PapelDaCategoriaDoBanco.Boleto,              // Transfer - Bank Slip
        ["15030000"] = PapelDaCategoriaDoBanco.Iof,                 // Tax on financial operations
        ["15010000"] = PapelDaCategoriaDoBanco.ImpostoDeRenda,      // Income taxes
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Pagamento de fatura, transferência para a mesma pessoa e investimento (aplicação, resgate ou rendimento): a etapa 6
    /// reconhece esses lançamentos. Até lá ficam "a revisar", fora da revisão em lote, do mapa e do orçamento (CEO, D23).
    /// </summary>
    public static bool ReconhecidoNaProximaEtapa(string? codigo) => Papel(codigo) is PapelDaCategoriaDoBanco.PagamentoDeFatura
        or PapelDaCategoriaDoBanco.MesmaTitularidade or PapelDaCategoriaDoBanco.Aplicacao or PapelDaCategoriaDoBanco.ResgateOuRendimento;

    public static PapelDaCategoriaDoBanco Papel(string? codigo) =>
        codigo is not null && Codigos.TryGetValue(codigo, out var papel) ? papel : PapelDaCategoriaDoBanco.Nenhum;
}
