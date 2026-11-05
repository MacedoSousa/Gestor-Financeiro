namespace GestorFinanceiro.SharedKernel.Dominio;

/// <summary>Base de toda entidade persistida. Id UUID v7 (ordenável) gerado na aplicação.</summary>
public abstract class Entidade : ISoftDeletable
{
    public Guid Id { get; protected init; } = Guid.CreateVersion7();
    public DateTimeOffset CriadoEm { get; set; }
    public DateTimeOffset AtualizadoEm { get; set; }

    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }
}

/// <summary>Toda entidade implementa: exclusão é sempre lógica.</summary>
public interface ISoftDeletable
{
    bool IsDeleted { get; set; }
    DateTimeOffset? DeletedAt { get; set; }
    Guid? DeletedBy { get; set; }
}

/// <summary>Entidade que pertence a um casal; recebe o filtro nomeado "Casal".</summary>
public interface ICasalScoped
{
    Guid CasalId { get; }
}
