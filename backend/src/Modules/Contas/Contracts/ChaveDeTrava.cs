namespace GestorFinanceiro.Modules.Contas.Contracts;

/// <summary>
/// Chaves de <c>pg_advisory_xact_lock(hashtextextended(chave, 0))</c> que outros módulos tomam para não correr com o
/// Contas. Quem apaga a conta e quem grava algo que aponta para ela tomam a mesma trava, na própria transação.
/// </summary>
public static class ChaveDeTrava
{
    /// <summary>Trava da conta: apagar conta e criar ou editar lançamento nela.</summary>
    public static string Conta(Guid id) => $"contas.conta:{id:D}";
}
