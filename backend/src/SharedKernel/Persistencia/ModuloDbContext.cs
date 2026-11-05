using System.Reflection;
using System.Text;
using GestorFinanceiro.SharedKernel.Aplicacao;
using GestorFinanceiro.SharedKernel.Dominio;
using Microsoft.EntityFrameworkCore;

namespace GestorFinanceiro.SharedKernel.Persistencia;

/// <summary>Nomes dos filtros globais; use-os em IgnoreQueryFilters([...]).</summary>
public static class Filtros
{
    public const string SoftDelete = "SoftDelete";
    public const string Casal = "Casal";
}

/// <summary>
/// Base dos DbContexts dos módulos: schema próprio, colunas em snake_case
/// e os filtros nomeados SoftDelete e Casal aplicados em toda entidade.
/// </summary>
public abstract class ModuloDbContext(DbContextOptions options, IUsuarioAtual usuario) : DbContext(options)
{
    protected abstract string Schema { get; }

    /// <summary>Lido a cada consulta pelo filtro "Casal" (EF parametriza membros do contexto).</summary>
    protected Guid CasalAtual => usuario.CasalId ?? Guid.Empty;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(GetType().Assembly);

        foreach (var tipo in modelBuilder.Model.GetEntityTypes().Select(e => e.ClrType).ToList())
        {
            if (typeof(ISoftDeletable).IsAssignableFrom(tipo))
                Invocar(nameof(FiltrarSoftDelete), tipo, modelBuilder);
            if (typeof(ICasalScoped).IsAssignableFrom(tipo))
                Invocar(nameof(FiltrarCasal), tipo, modelBuilder);
        }

        AplicarSnakeCase(modelBuilder);
    }

    private void Invocar(string metodo, Type tipo, ModelBuilder modelBuilder) =>
        typeof(ModuloDbContext)
            .GetMethod(metodo, BindingFlags.NonPublic | BindingFlags.Instance)!
            .MakeGenericMethod(tipo)
            .Invoke(this, [modelBuilder]);

    private void FiltrarSoftDelete<T>(ModelBuilder modelBuilder) where T : class, ISoftDeletable =>
        modelBuilder.Entity<T>().HasQueryFilter(Filtros.SoftDelete, e => !e.IsDeleted);

    private void FiltrarCasal<T>(ModelBuilder modelBuilder) where T : class, ICasalScoped =>
        modelBuilder.Entity<T>().HasQueryFilter(Filtros.Casal, e => e.CasalId == CasalAtual);

    private static void AplicarSnakeCase(ModelBuilder modelBuilder)
    {
        foreach (var entidade in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var propriedade in entidade.GetProperties())
                propriedade.SetColumnName(ParaSnakeCase(propriedade.Name));
        }
    }

    internal static string ParaSnakeCase(string nome)
    {
        var sb = new StringBuilder(nome.Length + 8);
        for (var i = 0; i < nome.Length; i++)
        {
            var c = nome[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(nome[i - 1]) || (i + 1 < nome.Length && char.IsLower(nome[i + 1]) && char.IsUpper(nome[i - 1]))))
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
