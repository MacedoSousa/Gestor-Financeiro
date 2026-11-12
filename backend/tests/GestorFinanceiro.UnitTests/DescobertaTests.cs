using GestorFinanceiro.Modules.Integracoes.Dominio;
using GestorFinanceiro.Modules.Integracoes.Pluggy;
using GestorFinanceiro.SharedKernel.Seguranca;

namespace GestorFinanceiro.UnitTests;

/// <summary>Regras puras da descoberta: apelido sugerido (D4), tipo da conta no sistema, dias do cartão e portador.</summary>
public class DescobertaTests
{
    [Theory]
    [InlineData("CORRENTE", "Ana", "Itaú Corrente · Ana")]
    [InlineData("POUPANCA", "Ana", "Itaú Poupança · Ana")]
    [InlineData("CARTAO", "Bruno", "Itaú Cartão · Bruno")]
    public void Apelido_sugerido_tem_banco_tipo_e_primeiro_nome(string tipo, string nome, string esperado) =>
        Assert.Equal(esperado, SugestaoDeApelido.Para("Itaú", tipo, nome));

    [Fact]
    public void Conta_conjunta_sugere_Conjunta() =>
        Assert.Equal("Itaú Corrente · Conjunta", SugestaoDeApelido.Para("Itaú", "CORRENTE", primeiroNome: null));

    [Fact]
    public void Apelido_sugerido_cabe_em_60_caracteres()
    {
        var apelido = SugestaoDeApelido.Para(new string('B', 60), "CORRENTE", "Ana");
        Assert.Equal(60, apelido.Length);
        Assert.EndsWith(" Corrente · Ana", apelido, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("BANK", "CHECKING_ACCOUNT", "CORRENTE")]
    [InlineData("BANK", "SAVINGS_ACCOUNT", "POUPANCA")]
    [InlineData("BANK", null, "CORRENTE")]
    [InlineData("CREDIT", "CREDIT_CARD", "CARTAO")]
    public void Tipo_da_conta_no_sistema(string tipo, string? subtipo, string esperado) =>
        Assert.Equal(esperado, Vinculo.TipoDaConta(new ContaPluggy("c", tipo, subtipo, "x", null, null, 0, null, null, null)));

    [Fact]
    public void Dia_sugerido_e_a_moda_e_no_empate_vale_a_fatura_mais_recente()
    {
        var faturas = new[]
        {
            new FaturaPluggy("a", new DateOnly(2026, 7, 10), new DateOnly(2026, 7, 2), 1),
            new FaturaPluggy("b", new DateOnly(2026, 8, 10), new DateOnly(2026, 8, 3), 1),
            new FaturaPluggy("c", new DateOnly(2026, 9, 11), new DateOnly(2026, 9, 2), 1),
            new FaturaPluggy("d", new DateOnly(2026, 10, 11), new DateOnly(2026, 10, 3), 1),
        };

        var (fechamento, vencimento) = SugestaoDeApelido.DiasDoCartao(faturas);

        Assert.Equal(3, fechamento);
        Assert.Equal(11, vencimento);
    }

    [Fact]
    public void Sem_faturas_nao_ha_dias() => Assert.Equal((null, null), SugestaoDeApelido.DiasDoCartao([]));

    [Fact]
    public void Portador_normalizado_ignora_acento_caixa_e_espacos() =>
        Assert.Equal(Vinculo.PortadorNormalizado(" Ána   Téste "), Vinculo.PortadorNormalizado("ANA TESTE"));

    private static readonly ContaPluggy Corrente =
        new("conta-sintetica-1", "BANK", "CHECKING_ACCOUNT", "Conta Corrente", null, "0001/12345-6", 0, null, null, null);

    private static readonly ContaPluggy Cartao =
        new("cartao-sintetico-1", "CREDIT", "CREDIT_CARD", "Cartão", null, null, 0, 0, "ANA TESTE", "4821");

    /// <summary>D18, item 1: a identidade é HMAC(código do banco, agência, conta); o formato da agência e conta não importa.</summary>
    [Fact]
    public void Identidade_inclui_o_codigo_do_banco()
    {
        using var cofre = new CofreAesGcm(EnvelopeDeChaves.GerarDek());

        var itau = Vinculo.IdentidadeDe(Corrente, "341", cofre);

        Assert.Equal(itau, Vinculo.IdentidadeDe(Corrente with { AgenciaEConta = "0001 12345 6" }, "341", cofre));
        Assert.NotEqual(itau, Vinculo.IdentidadeDe(Corrente, "260", cofre));
        Assert.Equal(Vinculo.IdentidadeDe(Cartao, "341", cofre), Vinculo.IdentidadeDe(Cartao with { Portador = "Ána Teste" }, "341", cofre));
        Assert.NotEqual(Vinculo.IdentidadeDe(Cartao, "341", cofre), Vinculo.IdentidadeDe(Cartao, "260", cofre));
    }
}
