using GestorFinanceiro.Modules.Contas.Dominio;
using GestorFinanceiro.SharedKernel.Aplicacao;
using GestorFinanceiro.SharedKernel.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GestorFinanceiro.Modules.Contas.Infra;

internal sealed class ContasDbContext(DbContextOptions<ContasDbContext> options, IUsuarioAtual usuario)
    : ModuloDbContext(options, usuario)
{
    protected override string Schema => "contas";

    public DbSet<Conta> Contas => Set<Conta>();
}

internal sealed class ContaConfig : IEntityTypeConfiguration<Conta>
{
    public void Configure(EntityTypeBuilder<Conta> b)
    {
        b.ToTable("contas");
        b.HasKey(x => x.Id);
        b.Property(x => x.Tipo).HasConversion(
            v => v.ToString().ToUpperInvariant(),
            v => Enum.Parse<TipoDeConta>(v, true));
        b.Property(x => x.Apelido).HasMaxLength(60);
        b.Property(x => x.Instituicao).HasMaxLength(60);
        b.Property(x => x.DiaFechamento).HasColumnType("smallint");
        b.Property(x => x.DiaVencimento).HasColumnType("smallint");
        b.HasIndex(x => x.CasalId).HasFilter("NOT is_deleted").HasDatabaseName("ix_contas_casal");
    }
}
