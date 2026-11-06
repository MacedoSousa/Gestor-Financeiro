using GestorFinanceiro.Modules.Lancamentos.Dominio;
using GestorFinanceiro.SharedKernel.Aplicacao;
using GestorFinanceiro.SharedKernel.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GestorFinanceiro.Modules.Lancamentos.Infra;

internal sealed class LancamentosDbContext(DbContextOptions<LancamentosDbContext> options, IUsuarioAtual usuario)
    : ModuloDbContext(options, usuario)
{
    protected override string Schema => "lancamentos";

    public DbSet<Categoria> Categorias => Set<Categoria>();
    public DbSet<Transacao> Transacoes => Set<Transacao>();
}

internal sealed class CategoriaConfig : IEntityTypeConfiguration<Categoria>
{
    public void Configure(EntityTypeBuilder<Categoria> b)
    {
        b.ToTable("categorias");
        b.HasKey(x => x.Id);
        b.Property(x => x.Nome).HasMaxLength(40);
        b.Property(x => x.Papel).HasMaxLength(20);
        b.Property(x => x.Tipo).HasConversion(
            v => v.ToString().ToUpperInvariant(),
            v => Enum.Parse<TipoDeCategoria>(v, true));
    }
}

internal sealed class TransacaoConfig : IEntityTypeConfiguration<Transacao>
{
    public void Configure(EntityTypeBuilder<Transacao> b)
    {
        b.ToTable("transacoes");
        b.HasKey(x => x.Id);
        b.Ignore(x => x.Valor);
        b.Property(x => x.Natureza).HasConversion(
            v => v.ToString().ToUpperInvariant(),
            v => Enum.Parse<NaturezaDaTransacao>(v, true));
        b.Property(x => x.Origem).HasConversion(
            v => v == OrigemDaTransacao.OpenFinance ? "OPEN_FINANCE" : v.ToString().ToUpperInvariant(),
            v => v == "OPEN_FINANCE" ? OrigemDaTransacao.OpenFinance : Enum.Parse<OrigemDaTransacao>(v, true));
        b.Property(x => x.Parcela).HasColumnType("smallint");
        b.Property(x => x.Parcelas).HasColumnType("smallint");
    }
}
