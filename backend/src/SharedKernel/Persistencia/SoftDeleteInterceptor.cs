using GestorFinanceiro.SharedKernel.Aplicacao;
using GestorFinanceiro.SharedKernel.Dominio;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GestorFinanceiro.SharedKernel.Persistencia;

/// <summary>
/// Converte Remove() em exclusão lógica e mantém CriadoEm/AtualizadoEm.
/// A auditoria não é feita aqui: é responsabilidade dos triggers do banco.
/// </summary>
public sealed class SoftDeleteInterceptor(IUsuarioAtual usuario, TimeProvider relogio) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Aplicar(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Aplicar(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Aplicar(DbContext? contexto)
    {
        if (contexto is null) return;
        var agora = relogio.GetUtcNow();

        foreach (var entrada in contexto.ChangeTracker.Entries<Entidade>())
        {
            switch (entrada.State)
            {
                case EntityState.Added:
                    entrada.Entity.CriadoEm = agora;
                    entrada.Entity.AtualizadoEm = agora;
                    break;

                case EntityState.Modified:
                    entrada.Entity.AtualizadoEm = agora;
                    break;

                case EntityState.Deleted:
                    entrada.State = EntityState.Modified;
                    entrada.Entity.IsDeleted = true;
                    entrada.Entity.DeletedAt = agora;
                    entrada.Entity.DeletedBy = usuario.UsuarioId;
                    entrada.Entity.AtualizadoEm = agora;
                    break;
            }
        }

        if (contexto.ChangeTracker.Entries().Any(e => e.State == EntityState.Deleted))
            throw new InvalidOperationException("Exclusão física bloqueada: a entidade não herda Entidade/ISoftDeletable.");
    }
}
