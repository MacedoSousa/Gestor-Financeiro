using System.Security.Cryptography;
using System.Text;
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

    [Theory]
    [InlineData("abcd-efgh-2345", "ABCDEFGH2345")]
    [InlineData("  AbCd EFgh\t2345\n", "ABCDEFGH2345")]
    [InlineData("ab_cd.ef/gh", "ABCDEFGH")]
    [InlineData("", "")]
    public void Canonizar_codigo_remove_separadores_e_poe_em_maiusculas(string digitado, string canonico) =>
        Assert.Equal(canonico, EnvelopeDeChaves.CanonizarCodigo(digitado));

    [Fact]
    public void Envelope_do_codigo_canonico_abre_com_o_codigo_digitado_de_qualquer_jeito()
    {
        var codigo = EnvelopeDeChaves.GerarCodigoRecuperacao();
        var dek = EnvelopeDeChaves.GerarDek();
        var salt = EnvelopeDeChaves.GerarSalt();
        var envelope = EnvelopeDeChaves.Embrulhar(
            dek, EnvelopeDeChaves.DerivarKek(EnvelopeDeChaves.CanonizarCodigo(codigo), salt, Rapido));

        var digitado = " " + string.Join(' ', codigo.ToLowerInvariant().Split('-')) + " ";
        var recuperada = EnvelopeDeChaves.Desembrulhar(
            envelope, EnvelopeDeChaves.DerivarKek(EnvelopeDeChaves.CanonizarCodigo(digitado), salt, Rapido));

        Assert.DoesNotContain('-', digitado);
        Assert.Equal(dek, recuperada);
    }
}

public class EnvelopesDaDekTests
{
    private static readonly ParametrosArgon2 Barato = new(MemoriaKiB: 1024, Iteracoes: 1, Paralelismo: 1);

    [Fact]
    public void Envelope_desembrulha_a_mesma_dek_com_o_mesmo_segredo()
    {
        var dek = EnvelopeDeChaves.GerarDek();
        var envelope = new EnvelopesDaDek(() => dek.ToArray(), Barato).Embrulhar("senha longa de teste");

        var kek = EnvelopeDeChaves.DerivarKek("senha longa de teste", envelope.Salt, Barato);
        Assert.Equal(dek, EnvelopeDeChaves.Desembrulhar(envelope.DekCifrada, kek));
    }

    [Fact]
    public void Segredo_errado_nao_desembrulha()
    {
        var envelope = new EnvelopesDaDek(EnvelopeDeChaves.GerarDek, Barato).Embrulhar("senha longa de teste");
        var kek = EnvelopeDeChaves.DerivarKek("outra senha qualquer", envelope.Salt, Barato);
        Assert.ThrowsAny<CryptographicException>(() => EnvelopeDeChaves.Desembrulhar(envelope.DekCifrada, kek));
    }

    [Fact]
    public void Cada_envelope_tem_salt_proprio()
    {
        var envelopes = new EnvelopesDaDek(EnvelopeDeChaves.GerarDek, Barato);
        Assert.NotEqual(envelopes.Embrulhar("a mesma senha!!").Salt, envelopes.Embrulhar("a mesma senha!!").Salt);
    }

    [Fact]
    public void Senha_em_nfd_desembrulha_envelope_feito_em_nfc()
    {
        var dek = EnvelopeDeChaves.GerarDek();
        var nfc = "coração de açúcar".Normalize(NormalizationForm.FormC);
        var envelope = new EnvelopesDaDek(() => dek.ToArray(), Barato).Embrulhar(nfc);

        var kek = EnvelopeDeChaves.DerivarKek(nfc.Normalize(NormalizationForm.FormD), envelope.Salt, Barato);
        Assert.Equal(dek, EnvelopeDeChaves.Desembrulhar(envelope.DekCifrada, kek));
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

public class CodigoDeInstalacaoTests
{
    private static string ArquivoTemporario() => Path.Combine(Path.GetTempPath(), $"gf-instalacao-{Guid.NewGuid():N}");
    private readonly RelogioFixo relogio = new() { Agora = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero) };

    [Fact]
    public void Codigo_vale_ate_24h_e_depois_expira()
    {
        var caminho = ArquivoTemporario();
        var codigo = CodigoDeInstalacaoEmArquivo.Gerar(caminho, relogio);
        var verificador = new CodigoDeInstalacaoEmArquivo(caminho, relogio);

        relogio.Agora += TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1);
        Assert.True(verificador.Confere(codigo));
        relogio.Agora += TimeSpan.FromSeconds(1);
        Assert.False(verificador.Confere(codigo));
    }

    [Fact]
    public void Codigo_consumido_nao_confere_mais()
    {
        var caminho = ArquivoTemporario();
        var codigo = CodigoDeInstalacaoEmArquivo.Gerar(caminho, relogio);
        var verificador = new CodigoDeInstalacaoEmArquivo(caminho, relogio);

        verificador.Consumir();

        Assert.False(verificador.Confere(codigo));
        Assert.True(File.Exists(caminho));
    }

    [Fact]
    public void Codigo_gerado_confere_com_e_sem_hifens_e_em_minusculas()
    {
        var caminho = ArquivoTemporario();
        var codigo = CodigoDeInstalacaoEmArquivo.Gerar(caminho, relogio);
        var verificador = new CodigoDeInstalacaoEmArquivo(caminho, relogio);

        Assert.True(verificador.Confere(codigo));
        Assert.True(verificador.Confere(codigo.Replace("-", "").ToLowerInvariant()));
        Assert.True(verificador.Confere($"  {codigo}  "));
    }

    [Fact]
    public void Codigo_diferente_ou_vazio_nao_confere()
    {
        var caminho = ArquivoTemporario();
        CodigoDeInstalacaoEmArquivo.Gerar(caminho, relogio);
        var verificador = new CodigoDeInstalacaoEmArquivo(caminho, relogio);

        Assert.False(verificador.Confere(EnvelopeDeChaves.GerarCodigoRecuperacao()));
        Assert.False(verificador.Confere(""));
        Assert.False(verificador.Confere(null));
    }

    [Fact]
    public void Sem_arquivo_nada_confere() =>
        Assert.False(new CodigoDeInstalacaoEmArquivo(ArquivoTemporario(), new RelogioFixo()).Confere("AAAA-BBBB"));

    [Fact]
    public void Gerar_de_novo_invalida_o_codigo_anterior()
    {
        var caminho = ArquivoTemporario();
        var antigo = CodigoDeInstalacaoEmArquivo.Gerar(caminho, relogio);
        var novo = CodigoDeInstalacaoEmArquivo.Gerar(caminho, relogio);
        var verificador = new CodigoDeInstalacaoEmArquivo(caminho, relogio);

        Assert.False(verificador.Confere(antigo));
        Assert.True(verificador.Confere(novo));
    }

    [Fact]
    public void Arquivo_ausente_ou_com_tamanho_errado_gera_aviso_sem_valores()
    {
        var caminho = ArquivoTemporario();
        var log = new LogEmMemoria();
        var verificador = new CodigoDeInstalacaoEmArquivo(caminho, relogio, log);
        var codigo = EnvelopeDeChaves.GerarCodigoRecuperacao();

        Assert.False(verificador.Confere(codigo));
        File.WriteAllBytes(caminho, [1, 2, 3]);
        Assert.False(verificador.Confere(codigo));

        Assert.Equal(2, log.Mensagens.Count);
        Assert.All(log.Mensagens, m => Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, m.Nivel));
        Assert.Contains("não existe", log.Mensagens[0].Texto);
        Assert.Contains("3 bytes", log.Mensagens[1].Texto);
        Assert.All(log.Mensagens, m =>
        {
            Assert.DoesNotContain(caminho, m.Texto);
            Assert.DoesNotContain(EnvelopeDeChaves.CanonizarCodigo(codigo), EnvelopeDeChaves.CanonizarCodigo(m.Texto));
        });
    }

    [Fact]
    public void Codigo_errado_com_arquivo_valido_nao_gera_aviso()
    {
        var caminho = ArquivoTemporario();
        CodigoDeInstalacaoEmArquivo.Gerar(caminho, relogio);
        var log = new LogEmMemoria();

        Assert.False(new CodigoDeInstalacaoEmArquivo(caminho, relogio, log).Confere(EnvelopeDeChaves.GerarCodigoRecuperacao()));
        Assert.Empty(log.Mensagens);
    }

    private sealed class LogEmMemoria : Microsoft.Extensions.Logging.ILogger<CodigoDeInstalacaoEmArquivo>
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Nivel, string Texto)> Mensagens { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Mensagens.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public void Arquivo_fica_somente_leitura_para_o_dono()
    {
        if (OperatingSystem.IsWindows()) return;
        var caminho = ArquivoTemporario();
        CodigoDeInstalacaoEmArquivo.Gerar(caminho, relogio);
        Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(caminho));
    }
}
