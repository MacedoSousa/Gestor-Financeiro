namespace GestorFinanceiro.Modules.Investimentos.Contracts;

/// <summary>Escrita para o Integracoes (D17): liga a conta de um grupo de investimento à meta escolhida no nascimento.</summary>
public interface IMetasDeContas
{
    /// <summary>
    /// Idempotente: verdadeiro se a conta fica (ou já estava) na meta. Falso se a meta ou a conta não existem no casal, se a
    /// conta não é de poupança ou investimento, ou se ela já está em outra meta.
    /// </summary>
    Task<bool> LigarContaAMetaAsync(Guid contaId, Guid metaId, CancellationToken ct);
}
