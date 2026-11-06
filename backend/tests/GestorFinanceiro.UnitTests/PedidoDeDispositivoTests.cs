using GestorFinanceiro.Modules.Identidade.Dominio;

namespace GestorFinanceiro.UnitTests;

public class PedidoDeDispositivoTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Dono = Guid.CreateVersion7();
    private static readonly Guid Conjuge = Guid.CreateVersion7();

    private static PedidoDeDispositivo Novo() => PedidoDeDispositivo.Criar(Guid.CreateVersion7(), Dono, [1, 2, 3], "123456", T0);

    [Fact]
    public void Conjuge_aprova_pedido_pendente_dentro_do_prazo()
    {
        var pedido = Novo();
        pedido.Aprovar(Conjuge, T0.AddMinutes(9));
        Assert.Equal(EstadoDoPedido.Aprovado, pedido.Estado);
        Assert.Equal(Conjuge, pedido.AprovadoPor);
    }

    [Fact]
    public void Dono_nao_aprova_o_proprio_pedido()
    {
        var pedido = Novo();
        Assert.Throws<InvalidOperationException>(() => pedido.Aprovar(Dono, T0));
        Assert.Equal(EstadoDoPedido.Pendente, pedido.Estado);
    }

    [Fact]
    public void Pedido_expirado_nao_e_aprovado()
    {
        var pedido = Novo();
        Assert.Throws<InvalidOperationException>(() => pedido.Aprovar(Conjuge, T0 + TemposDoLogin.Pedido));
        Assert.Equal(EstadoDoPedido.Pendente, pedido.Estado);
        Assert.Null(pedido.AprovadoPor);
    }

    [Fact]
    public void Pedido_que_nao_esta_pendente_nao_e_aprovado_de_novo()
    {
        var pedido = Novo();
        pedido.Aprovar(Conjuge, T0);
        Assert.Throws<InvalidOperationException>(() => pedido.Aprovar(Conjuge, T0));
        pedido.MarcarUsado();
        Assert.Throws<InvalidOperationException>(() => pedido.Aprovar(Conjuge, T0));
    }

    [Fact]
    public void Marcar_usado_so_vale_para_pedido_aprovado_e_uma_vez()
    {
        var pedido = Novo();
        Assert.Throws<InvalidOperationException>(pedido.MarcarUsado);
        pedido.Aprovar(Conjuge, T0);
        pedido.MarcarUsado();
        Assert.Equal(EstadoDoPedido.Usado, pedido.Estado);
        Assert.Throws<InvalidOperationException>(pedido.MarcarUsado);
    }

    [Fact]
    public void Expira_exatamente_em_10_min()
    {
        var pedido = Novo();
        Assert.False(pedido.Expirou(T0 + TemposDoLogin.Pedido - TimeSpan.FromTicks(1)));
        Assert.True(pedido.Expirou(T0 + TemposDoLogin.Pedido));
    }
}
