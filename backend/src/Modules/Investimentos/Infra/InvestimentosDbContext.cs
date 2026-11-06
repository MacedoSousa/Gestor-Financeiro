using GestorFinanceiro.Modules.Investimentos.Dominio;
using GestorFinanceiro.SharedKernel.Aplicacao;
using GestorFinanceiro.SharedKernel.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GestorFinanceiro.Modules.Investimentos.Infra;

internal sealed class InvestimentosDbContext(DbContextOptions<InvestimentosDbContext> options, IUsuarioAtual usuario)
    : ModuloDbContext(options, usuario)
{
    protected override string Schema => "investimentos";

    public DbSet<Meta> Metas => Set<Meta>();
    public DbSet<MetaConta> MetasContas => Set<MetaConta>();
}

internal sealed class MetaConfig : IEntityTypeConfiguration<Meta>
{
    public void Configure(EntityTypeBuilder<Meta> b)
    {
        b.ToTable("metas", "investimentos");
        b.HasKey(x => x.Id);
        b.Property(x => x.Nome).HasMaxLength(LimitesDaMeta.NomeMaximo);
        b.Property(x => x.TaxaMensal).HasPrecision(8, 6);
    }
}

internal sealed class MetaContaConfig : IEntityTypeConfiguration<MetaConta>
{
    public void Configure(EntityTypeBuilder<MetaConta> b)
    {
        b.ToTable("metas_contas", "investimentos");
        b.HasKey(x => x.Id);
    }
}
