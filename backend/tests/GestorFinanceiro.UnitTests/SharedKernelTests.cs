using System.Security.Cryptography;
using GestorFinanceiro.SharedKernel.Aplicacao;
using GestorFinanceiro.SharedKernel.Dominio;
using GestorFinanceiro.SharedKernel.Persistencia;
using GestorFinanceiro.SharedKernel.Seguranca;
using Microsoft.EntityFrameworkCore;

namespace GestorFinanceiro.UnitTests;

public class DinheiroTests
{
    [Theory]
    [InlineData("142.50", 14_250)]
    [InlineData("0.005", 1)]
    [InlineData("-18.90", -1_890)]
    [InlineData("300000", 30_000_000)]
    public void DeReais_converte_para_centavos(string reais, long centavos) =>
        Assert.Equal(centavos, Dinheiro.DeReais(decimal.Parse(reais, System.Globalization.CultureInfo.InvariantCulture)).Centavos);

    [Fact]
    public void Soma_e_subtracao_trabalham_em_centavos()
    {
        var total = Dinheiro.DeReais(0.10m) + Dinheiro.DeReais(0.20m);
        Assert.Equal(30, total.Centavos);
        Assert.Equal(-30, (Dinheiro.Zero - total).Centavos);
    }
}

public class CofreTests
{
    private static CofreAesGcm NovoCofre() => new(EnvelopeDeChaves.GerarDek());

    [Fact]
    public void Cifra_e_decifra_com_o_mesmo_contexto()
    {
        using var cofre = NovoCofre();
        var aad = Cofre.Aad("identidade.usuarios", "cpf_cifrado", Guid.CreateVersion7());

        var cifrado = cofre.Cifrar("52998224725", aad);

        Assert.Equal("52998224725", cofre.Decifrar(cifrado, aad));
    }

    [Fact]
    public void Valor_copiado_para_outra_linha_nao_decifra()
    {
        using var cofre = NovoCofre();
        var cifrado = cofre.Cifrar("52998224725", Cofre.Aad("identidade.usuarios", "cpf_cifrado", Guid.CreateVersion7()));

        Assert.ThrowsAny<CryptographicException>(() =>
            cofre.Decifrar(cifrado, Cofre.Aad("identidade.usuarios", "cpf_cifrado", Guid.CreateVersion7())));
    }

    [Fact]
    public void Mesmo_texto_gera_cifras_diferentes()
    {
        using var cofre = NovoCofre();
        Assert.NotEqual(cofre.Cifrar("x", "a"), cofre.Cifrar("x", "a"));
    }

    [Fact]
    public void Indice_cego_e_deterministico_e_separado_por_proposito()
    {
        using var cofre = NovoCofre();
        Assert.Equal(cofre.IndiceCego("52998224725", "cpf"), cofre.IndiceCego("52998224725", "cpf"));
        Assert.NotEqual(cofre.IndiceCego("52998224725", "cpf"), cofre.IndiceCego("52998224725", "telefone"));
    }
}

public class EnvelopeDeChavesTests
{
    private static readonly ParametrosArgon2 Rapido = new(MemoriaKiB: 1024, Iteracoes: 1, Paralelismo: 1);

    [Fact]
    public void Senha_certa_recupera_a_mesma_dek()
    {
        var dek = EnvelopeDeChaves.GerarDek();
        var salt = EnvelopeDeChaves.GerarSalt();
        var envelope = EnvelopeDeChaves.Embrulhar(dek, EnvelopeDeChaves.DerivarKek("senha do casal", salt, Rapido));

        var recuperada = EnvelopeDeChaves.Desembrulhar(envelope, EnvelopeDeChaves.DerivarKek("senha do casal", salt, Rapido));

        Assert.Equal(dek, recuperada);
    }

    [Fact]
    public void Senha_errada_falha()
    {
        var salt = EnvelopeDeChaves.GerarSalt();
        var envelope = EnvelopeDeChaves.Embrulhar(EnvelopeDeChaves.GerarDek(), EnvelopeDeChaves.DerivarKek("certa", salt, Rapido));

        Assert.ThrowsAny<CryptographicException>(() =>
            EnvelopeDeChaves.Desembrulhar(envelope, EnvelopeDeChaves.DerivarKek("errada", salt, Rapido)));
    }

    [Fact]
    public void Codigo_de_recuperacao_tem_8_grupos_de_4()
    {
        var codigo = EnvelopeDeChaves.GerarCodigoRecuperacao();
        Assert.Matches("^([A-Z2-9]{4}-){7}[A-Z2-9]{4}$", codigo);
    }
}

public class SnakeCaseTests
{
    [Theory]
    [InlineData("CpfHmac", "cpf_hmac")]
    [InlineData("IsDeleted", "is_deleted")]
    [InlineData("NomeFamilia", "nome_familia")]
    [InlineData("Id", "id")]
    [InlineData("DekCifrada", "dek_cifrada")]
    public void Converte_nomes_de_propriedade(string entrada, string esperado) =>
        Assert.Equal(esperado, ModuloDbContext.ParaSnakeCase(entrada));
}

public class FiltrosNomeadosTests
{
    [Fact]
    public void Entidade_do_casal_recebe_os_filtros_SoftDelete_e_Casal()
    {
        using var contexto = new ContextoDeTeste();

        var filtros = contexto.Model.FindEntityType(typeof(ItemDoCasal))!
            .GetDeclaredQueryFilters()
            .Select(f => f.Key)
            .Order();

        Assert.Equal([Filtros.Casal, Filtros.SoftDelete], filtros);
    }

    private sealed class ItemDoCasal : Entidade, ICasalScoped
    {
        public Guid CasalId { get; init; }
    }

    private sealed class ContextoDeTeste() : ModuloDbContext(
        new DbContextOptionsBuilder().UseNpgsql("Host=localhost").Options,
        new UsuarioSistema())
    {
        protected override string Schema => "teste";

        public DbSet<ItemDoCasal> Itens => Set<ItemDoCasal>();
    }
}
