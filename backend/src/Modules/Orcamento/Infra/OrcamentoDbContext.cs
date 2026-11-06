using GestorFinanceiro.Modules.Orcamento.Dominio;
using GestorFinanceiro.SharedKernel.Aplicacao;
using GestorFinanceiro.SharedKernel.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GestorFinanceiro.Modules.Orcamento.Infra;

internal sealed class OrcamentoDbContext(DbContextOptions<OrcamentoDbContext> options, IUsuarioAtual usuario)
    : ModuloDbContext(options, usuario)
{
    protected override string Schema => "orcamento";

    public DbSet<Teto> Tetos => Set<Teto>();
    public DbSet<ReceitaPrevista> ReceitasPrevistas => Set<ReceitaPrevista>();
}

internal sealed class TetoConfig : IEntityTypeConfiguration<Teto>
{
    public void Configure(EntityTypeBuilder<Teto> b)
    {
        b.ToTable("tetos", "orcamento");
        b.HasKey(x => x.Id);
    }
}

internal sealed class ReceitaPrevistaConfig : IEntityTypeConfiguration<ReceitaPrevista>
{
    public void Configure(EntityTypeBuilder<ReceitaPrevista> b)
    {
        b.ToTable("receitas_previstas", "orcamento");
        b.HasKey(x => x.Id);
    }
}
