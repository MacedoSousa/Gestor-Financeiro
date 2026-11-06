using GestorFinanceiro.Modules.Identidade.Dominio;

namespace GestorFinanceiro.UnitTests;

public class PoliticaDeBloqueioTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, 15)] [InlineData(1, 30)] [InlineData(2, 60)] [InlineData(6, 960)] [InlineData(7, 1440)] [InlineData(30, 1440)]
    public void Duracao_dobra_ate_24h(int anteriores, int minutos) =>
        Assert.Equal(TimeSpan.FromMinutes(minutos), PoliticaDeBloqueio.DuracaoDoBloqueio(anteriores));

    [Fact]
    public void Quinta_falha_bloqueia_por_15_min_e_sucesso_zera()
    {
        var u = UsuarioDeTeste.Criar();
        for (var i = 0; i < 4; i++) u.RegistrarFalha(T0);
        Assert.False(u.EstaBloqueado(T0));
        u.RegistrarFalha(T0);
        Assert.True(u.EstaBloqueado(T0.AddMinutes(14)));
        Assert.False(u.EstaBloqueado(T0.AddMinutes(15)));
        u.RegistrarSucesso();
        for (var i = 0; i < 5; i++) u.RegistrarFalha(T0.AddHours(1));
        Assert.True(u.EstaBloqueado(T0.AddHours(1).AddMinutes(14)));
        Assert.False(u.EstaBloqueado(T0.AddHours(1).AddMinutes(15)));
    }

    [Fact]
    public void Segundo_bloqueio_dura_30_min_e_destravar_mantem_a_contagem()
    {
        var u = UsuarioDeTeste.Criar();
        for (var i = 0; i < 5; i++) u.RegistrarFalha(T0);
        var t1 = T0.AddMinutes(16);
        for (var i = 0; i < 5; i++) u.RegistrarFalha(t1);
        Assert.True(u.EstaBloqueado(t1.AddMinutes(29)));
        u.Destravar();
        Assert.False(u.EstaBloqueado(t1));
        Assert.Equal(2, u.Bloqueios);
    }

    [Fact]
    public void Encerrar_primeiro_acesso_e_idempotente()
    {
        var u = UsuarioDeTeste.Criar();
        Assert.False(u.PrimeiroAcessoEncerrado);
        u.EncerrarPrimeiroAcesso(T0);
        u.EncerrarPrimeiroAcesso(T0.AddDays(1));
        Assert.True(u.PrimeiroAcessoEncerrado);
        Assert.Equal(T0, u.PrimeiroAcessoEncerradoEm);
    }
}
