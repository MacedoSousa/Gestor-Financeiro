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
    public DbSet<RegraDeCategoria> Regras => Set<RegraDeCategoria>();
    public DbSet<MapaDeCategoriaExterna> Mapa => Set<MapaDeCategoriaExterna>();
}

internal sealed class RegraDeCategoriaConfig : IEntityTypeConfiguration<RegraDeCategoria>
{
    public void Configure(EntityTypeBuilder<RegraDeCategoria> b)
    {
        b.ToTable("regras_de_categoria");
        b.HasKey(x => x.Id);
    }
}

internal sealed class MapaDeCategoriaExternaConfig : IEntityTypeConfiguration<MapaDeCategoriaExterna>
{
    public void Configure(EntityTypeBuilder<MapaDeCategoriaExterna> b)
    {
        b.ToTable("mapa_categoria_externa");
        b.HasKey(x => x.Id);
        b.Property(x => x.CategoriaExterna).HasMaxLength(8);
        b.Property(x => x.NomeExterno).HasMaxLength(60);
    }
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
        b.Property(x => x.Revisao).HasConversion(
            v => v == RevisaoDaTransacao.ARevisar ? "A_REVISAR" : "REVISADA",
            v => v == "A_REVISAR" ? RevisaoDaTransacao.ARevisar : RevisaoDaTransacao.Revisada);
        b.Property(x => x.ApagadaPor).HasConversion(
            v => v == null ? null : v == MotivoDoApagamento.Casal ? "CASAL"
                : v == MotivoDoApagamento.AusenciaNoBanco ? "AUSENCIA_NO_BANCO" : "PENDENTE_PROMOVIDA",
            v => v == null ? null : v == "CASAL" ? MotivoDoApagamento.Casal
                : v == "AUSENCIA_NO_BANCO" ? MotivoDoApagamento.AusenciaNoBanco : MotivoDoApagamento.PendentePromovida);
        b.Property(x => x.Confirmacao).HasConversion(
            v => v == ConfirmacaoDaTransacao.AConfirmar ? "A_CONFIRMAR" : "CONFIRMADA",
            v => v == "A_CONFIRMAR" ? ConfirmacaoDaTransacao.AConfirmar : ConfirmacaoDaTransacao.Confirmada);
        b.Property(x => x.Parcela).HasColumnType("smallint");
        b.Property(x => x.Parcelas).HasColumnType("smallint");
    }
}
