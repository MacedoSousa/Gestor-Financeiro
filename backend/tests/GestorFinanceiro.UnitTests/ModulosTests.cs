using GestorFinanceiro.Modules.Identidade.Dominio;
using GestorFinanceiro.Modules.Lancamentos.Dominio;
using GestorFinanceiro.SharedKernel.Dominio;

namespace GestorFinanceiro.UnitTests;

public class CpfTests
{
    // CPFs sintéticos (dígitos verificadores válidos), usados só em teste.
    [Theory]
    [InlineData("529.982.247-25")]
    [InlineData("52998224725")]
    [InlineData("123.456.789-09")]
    public void Aceita_cpf_valido(string entrada)
    {
        Assert.True(Cpf.TentarCriar(entrada, out var cpf));
        Assert.Equal(11, cpf.Digitos.Length);
    }

    [Theory]
    [InlineData("111.111.111-11")]
    [InlineData("123.456.789-00")]
    [InlineData("5299822472")]
    [InlineData("529.982.247-2a")]
    [InlineData("")]
    public void Rejeita_cpf_invalido(string entrada) => Assert.False(Cpf.TentarCriar(entrada, out _));

    [Fact]
    public void ToString_nunca_mostra_o_cpf_inteiro()
    {
        Cpf.TentarCriar("529.982.247-25", out var cpf);
        Assert.Equal("***.982.247-**", cpf.ToString());
    }
}

public class ParserMensagemTests
{
    [Theory]
    [InlineData("mercado 142,50", 14_250, "mercado")]
    [InlineData("142,50 mercado", 14_250, "mercado")]
    [InlineData("uber 23.90", 2_390, "uber")]
    [InlineData("R$ 1.234,56 condomínio", 123_456, "condomínio")]
    [InlineData("farmácia 64", 6_400, "farmácia")]
    [InlineData("2 pães 7,50", 750, "2 pães")]
    [InlineData("geladeira 3.499", 349_900, "geladeira")]
    public void Extrai_valor_e_descricao(string mensagem, long centavos, string descricao)
    {
        var gasto = ParserMensagem.Interpretar(mensagem);

        Assert.NotNull(gasto);
        Assert.Equal(centavos, gasto.Valor.Centavos);
        Assert.Equal(descricao, gasto.Descricao);
    }

    [Theory]
    [InlineData("oi, tudo bem?")]
    [InlineData("mercado 0,00")]
    [InlineData("")]
    public void Ignora_mensagem_sem_valor(string mensagem) => Assert.Null(ParserMensagem.Interpretar(mensagem));
}
