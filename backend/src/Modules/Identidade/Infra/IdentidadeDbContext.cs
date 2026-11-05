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
    public DbSet<Credencial> Credenciais => Set<Credencial>();
    public DbSet<Sessao> Sessoes => Set<Sessao>();
    public DbSet<PedidoDeDispositivo> PedidosDeDispositivo => Set<PedidoDeDispositivo>();
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

internal sealed class CredencialConfig : IEntityTypeConfiguration<Credencial>
{
    public void Configure(EntityTypeBuilder<Credencial> b)
    {
        b.ToTable("credenciais");
        b.HasKey(x => x.Id);
        b.Property(x => x.Tipo).HasConversion(
            v => v.ToString().ToUpperInvariant(),
            v => Enum.Parse<TipoCredencial>(v, true));
        b.Property(x => x.Apelido).HasMaxLength(60);
        b.HasIndex(x => x.CredentialId).IsUnique().HasFilter("NOT is_deleted AND credential_id IS NOT NULL")
            .HasDatabaseName("ux_credenciais_credential_id");
    }
}

internal sealed class SessaoConfig : IEntityTypeConfiguration<Sessao>
{
    public void Configure(EntityTypeBuilder<Sessao> b)
    {
        b.ToTable("sessoes");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.TokenHmac).IsUnique().HasFilter("NOT is_deleted").HasDatabaseName("ux_sessoes_token");
    }
}

internal sealed class PedidoDeDispositivoConfig : IEntityTypeConfiguration<PedidoDeDispositivo>
{
    public void Configure(EntityTypeBuilder<PedidoDeDispositivo> b)
    {
        b.ToTable("pedidos_dispositivo");
        b.HasKey(x => x.Id);
        b.Property(x => x.Codigo).HasColumnType("char(6)");
        b.Property(x => x.Estado).HasConversion(
            v => v.ToString().ToUpperInvariant(),
            v => Enum.Parse<EstadoDoPedido>(v, true));
        b.HasIndex(x => x.TokenHmac).IsUnique().HasFilter("NOT is_deleted").HasDatabaseName("ux_pedidos_token");
    }
}
