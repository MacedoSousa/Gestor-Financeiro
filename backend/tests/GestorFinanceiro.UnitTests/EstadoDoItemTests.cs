using GestorFinanceiro.Modules.Integracoes.Dominio;
using GestorFinanceiro.Modules.Integracoes.Pluggy;

namespace GestorFinanceiro.UnitTests;

/// <summary>Estado de um item a partir do que a Pluggy disse dele, e o dia de São Paulo de um instante.</summary>
public class EstadoDoItemTests
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 12, 15, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("UPDATED")]
    [InlineData("UPDATING")]
    public void Item_atualizado_e_consentimento_longe_e_ativo(string status) =>
        Assert.Equal((EstadoDoItem.Ativo, (string?)null), EstadoDoItem.Do(status, Agora.AddDays(365), Agora));

    [Fact]
    public void Sem_data_de_consentimento_e_ativo() =>
        Assert.Equal((EstadoDoItem.Ativo, (string?)null), EstadoDoItem.Do("UPDATED", null, Agora));

    [Theory]
    [InlineData(15)]
    [InlineData(1)]
    public void Consentimento_a_15_dias_ou_menos_esta_vencendo(int dias) =>
        Assert.Equal((EstadoDoItem.ConsentimentoVencendo, (string?)null), EstadoDoItem.Do("UPDATED", Agora.AddDays(dias), Agora));

    [Fact]
    public void Consentimento_a_mais_de_15_dias_nao_avisa() =>
        Assert.Equal(EstadoDoItem.Ativo, EstadoDoItem.Do("UPDATED", Agora.AddDays(15).AddMinutes(1), Agora).Estado);

    [Fact]
    public void Consentimento_vencido_ganha_de_tudo() =>
        Assert.Equal((EstadoDoItem.ConsentimentoVencido, (string?)null), EstadoDoItem.Do("LOGIN_ERROR", Agora, Agora));

    [Theory]
    [InlineData("LOGIN_ERROR")]
    [InlineData("OUTDATED")]
    [InlineData("WAITING_USER_INPUT")]
    public void Status_ruim_e_erro_com_o_codigo(string status) =>
        Assert.Equal((EstadoDoItem.Erro, (string?)status), EstadoDoItem.Do(status, Agora.AddDays(365), Agora));

    [Theory]
    [InlineData("login error: senha de fulano")]
    [InlineData("")]
    [InlineData("?")]
    public void Motivo_que_nao_e_codigo_vira_desconhecido(string status) =>
        Assert.Equal((EstadoDoItem.Erro, (string?)"DESCONHECIDO"), EstadoDoItem.Do(status, null, Agora));

    [Fact]
    public void Motivo_longo_demais_vira_desconhecido() =>
        Assert.Equal("DESCONHECIDO", EstadoDoItem.Do(new string('A', 61), null, Agora).Motivo);

    [Fact]
    public void Instante_vira_o_dia_de_Sao_Paulo()
    {
        Assert.Equal(new DateOnly(2027, 11, 10), ConversaoPluggy.DiaDeSaoPaulo(new DateTimeOffset(2027, 11, 11, 2, 30, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2027, 11, 11), ConversaoPluggy.DiaDeSaoPaulo(new DateTimeOffset(2027, 11, 11, 3, 0, 0, TimeSpan.Zero)));
    }
}
