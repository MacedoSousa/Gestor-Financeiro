using GestorFinanceiro.Modules.Integracoes.Dominio;
using GestorFinanceiro.SharedKernel.Aplicacao;
using GestorFinanceiro.SharedKernel.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GestorFinanceiro.Modules.Integracoes.Infra;

internal sealed class IntegracoesDbContext(DbContextOptions<IntegracoesDbContext> options, IUsuarioAtual usuario)
    : ModuloDbContext(options, usuario)
{
    protected override string Schema => "integracoes";

    public DbSet<Conexao> Conexoes => Set<Conexao>();
    public DbSet<Item> Items => Set<Item>();
    public DbSet<Vinculo> Vinculos => Set<Vinculo>();
}

internal sealed class ConexaoConfig : IEntityTypeConfiguration<Conexao>
{
    public void Configure(EntityTypeBuilder<Conexao> b)
    {
        b.ToTable("conexoes", "integracoes");
        b.HasKey(x => x.Id);
    }
}

internal sealed class ItemConfig : IEntityTypeConfiguration<Item>
{
    public void Configure(EntityTypeBuilder<Item> b)
    {
        b.ToTable("items", "integracoes");
        b.HasKey(x => x.Id);
        b.Property(x => x.Banco).HasMaxLength(Item.BancoMaximo);
        b.Property(x => x.CodigoBanco).HasColumnType("char(3)");
        b.Property(x => x.MotivoErro).HasMaxLength(Item.MotivoMaximo);
    }
}

internal sealed class VinculoConfig : IEntityTypeConfiguration<Vinculo>
{
    public void Configure(EntityTypeBuilder<Vinculo> b)
    {
        b.ToTable("vinculos", "integracoes");
        b.HasKey(x => x.Id);
        b.Property(x => x.IdExterno).HasMaxLength(Vinculo.IdExternoMaximo);
        b.Property(x => x.FaturasDivergentes).HasColumnType("jsonb");
        b.Ignore(x => x.FaturasVisiveis);
        b.Ignore(x => x.DiferencaVisivel);
    }
}
