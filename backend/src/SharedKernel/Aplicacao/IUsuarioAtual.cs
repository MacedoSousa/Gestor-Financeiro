namespace GestorFinanceiro.SharedKernel.Aplicacao;

/// <summary>Quem está fazendo a operação. Alimenta os filtros, o soft delete e a auditoria.</summary>
public interface IUsuarioAtual
{
    Guid? UsuarioId { get; }
    Guid? CasalId { get; }
    string? Ip { get; }
}

/// <summary>Usado por jobs e pelo migrador: sem usuário humano.</summary>
public sealed class UsuarioSistema(Guid? casalId = null) : IUsuarioAtual
{
    public Guid? UsuarioId => null;
    public Guid? CasalId { get; } = casalId;
    public string? Ip => "sistema";
}
