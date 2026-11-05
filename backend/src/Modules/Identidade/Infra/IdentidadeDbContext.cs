using GestorFinanceiro.Modules.Identidade.Dominio;
using GestorFinanceiro.SharedKernel.Aplicacao;
using GestorFinanceiro.SharedKernel.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GestorFinanceiro.Modules.Identidade.Infra;

internal sealed class IdentidadeDbContext(DbContextOptions<IdentidadeDbContext> options, IUsuarioAtual usuario)
    : ModuloDbContext(options, usuario)
{
    protected override string Schema => "identidade";

    public DbSet<Casal> Casais => Set<Casal>();
    public DbSet<Usuario> Usuarios => Set<Usuario>();
    public DbSet<ChaveDoCasal> Chaves => Set<ChaveDoCasal>();
}

internal sealed class CasalConfig : IEntityTypeConfiguration<Casal>
{
    public void Configure(EntityTypeBuilder<Casal> b)
    {
        b.ToTable("casais");
        b.HasKey(x => x.Id);
        b.Property(x => x.NomeFamilia).HasMaxLength(100);
    }
}

internal sealed class UsuarioConfig : IEntityTypeConfiguration<Usuario>
{
    public void Configure(EntityTypeBuilder<Usuario> b)
    {
        b.ToTable("usuarios");
        b.HasKey(x => x.Id);
        b.Property(x => x.NomeCompleto).HasMaxLength(150);
        b.HasIndex(x => x.CpfHmac).IsUnique().HasFilter("NOT is_deleted");
        b.HasIndex(x => x.TelefoneHmac).IsUnique().HasFilter("NOT is_deleted AND telefone_hmac IS NOT NULL");
    }
}

internal sealed class ChaveDoCasalConfig : IEntityTypeConfiguration<ChaveDoCasal>
{
    public void Configure(EntityTypeBuilder<ChaveDoCasal> b)
    {
        b.ToTable("chaves");
        b.HasKey(x => x.Id);
        b.Property(x => x.Tipo).HasConversion(
            v => v.ToString().ToUpperInvariant(),
            v => Enum.Parse<TipoChave>(v, true));
    }
}
