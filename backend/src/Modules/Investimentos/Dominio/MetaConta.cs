using GestorFinanceiro.SharedKernel.Dominio;

namespace GestorFinanceiro.Modules.Investimentos.Dominio;

/// <summary>Vínculo entre uma meta e uma conta do módulo Contas. Uma conta tem no máximo um vínculo ativo.</summary>
internal sealed class MetaConta : Entidade, ICasalScoped
{
    public Guid CasalId { get; private set; }
    public Guid MetaId { get; private set; }
    public Guid ContaId { get; private set; }

    private MetaConta() { }

    public static MetaConta Criar(Guid casalId, Guid metaId, Guid contaId) =>
        new() { CasalId = casalId, MetaId = metaId, ContaId = contaId };
}
