using GestorFinanceiro.Modules.Identidade.Aplicacao;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.Modules.Identidade.Dominio;

namespace GestorFinanceiro.UnitTests;

public class ValidadorDeCadastroTests
{
    private static CadastroDoCasalDto Valido() => new(
        "CODIGO", "Família Teste",
        new ConjugeNovoDto("Ana Teste", "529.982.247-25", "senha-da-ana-123"),
        new ConjugeNovoDto("Bruno Teste", "123.456.789-09", "senha-do-bruno-1"));

    [Fact]
    public void Pedido_valido_nao_tem_erros() => Assert.Empty(ValidadorDeCadastro.Validar(Valido()));

    [Fact]
    public void Rejeita_mesmo_cpf_com_mascaras_diferentes()
    {
        var pedido = Valido() with { Conjuge2 = Valido().Conjuge2 with { Cpf = "52998224725" } };
        Assert.Equal(["Os dois cônjuges não podem ter o mesmo CPF."], ValidadorDeCadastro.Validar(pedido)["conjuge2.cpf"]);
    }

    [Fact]
    public void Cpf_invalido_aponta_o_campo()
    {
        var pedido = Valido() with { Conjuge1 = Valido().Conjuge1 with { Cpf = "123.456.789-00" } };
        Assert.Equal(["CPF inválido."], ValidadorDeCadastro.Validar(pedido)["conjuge1.cpf"]);
    }

    [Theory]
    [InlineData("curta")]
    [InlineData("12345678901")]
    public void Senha_com_menos_de_12_caracteres_e_rejeitada(string senha)
    {
        var pedido = Valido() with { Conjuge2 = Valido().Conjuge2 with { Senha = senha } };
        Assert.True(ValidadorDeCadastro.Validar(pedido).ContainsKey("conjuge2.senha"));
    }

    [Fact]
    public void Senha_com_unicode_invalido_e_rejeitada()
    {
        var pedido = Valido() with { Conjuge1 = Valido().Conjuge1 with { Senha = "senha-valida-" + "\uD800" + "xx" } };
        var erros = ValidadorDeCadastro.Validar(pedido);
        Assert.Equal(["A senha precisa ter de 12 a 128 caracteres."], erros["conjuge1.senha"]);
    }

    [Fact]
    public void Senha_e_contada_em_nfc()
    {
        // 11 caracteres "e" + acento combinante = 12 chars brutos, mas 11 em NFC.
        var decomposta = "abcdefghij" + "é";
        var pedido = Valido() with { Conjuge1 = Valido().Conjuge1 with { Senha = decomposta } };
        Assert.True(ValidadorDeCadastro.Validar(pedido).ContainsKey("conjuge1.senha"));
    }

    [Fact]
    public void Senha_com_mais_de_128_caracteres_e_rejeitada()
    {
        var pedido = Valido() with { Conjuge1 = Valido().Conjuge1 with { Senha = new string('a', 129) } };
        Assert.True(ValidadorDeCadastro.Validar(pedido).ContainsKey("conjuge1.senha"));
    }

    [Fact]
    public void Nomes_em_branco_sao_rejeitados()
    {
        var pedido = Valido() with { NomeFamilia = "   ", Conjuge1 = Valido().Conjuge1 with { NomeCompleto = "" } };
        var erros = ValidadorDeCadastro.Validar(pedido);
        Assert.Contains("nomeFamilia", erros.Keys);
        Assert.Contains("conjuge1.nomeCompleto", erros.Keys);
    }

    [Fact]
    public void ToString_dos_dtos_nao_mostra_cpf_nem_senha()
    {
        var texto = Valido().ToString() + Valido().Conjuge1;
        Assert.DoesNotContain("529", texto);
        Assert.DoesNotContain("senha-da-ana", texto);
    }

    [Fact]
    public void CpfSintetico_gera_cpfs_validos_e_distintos()
    {
        var cpfs = Enumerable.Range(1, 50).Select(CpfSintetico.Gerar).ToList();
        Assert.All(cpfs, c => Assert.True(Cpf.TentarCriar(c, out _)));
        Assert.Equal(50, cpfs.Distinct().Count());
    }
}
