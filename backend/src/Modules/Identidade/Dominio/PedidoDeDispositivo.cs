using GestorFinanceiro.SharedKernel.Dominio;

namespace GestorFinanceiro.Modules.Identidade.Dominio;

/// <summary>Pedido de um celular novo, aprovado pelo outro cônjuge conferindo o código de 6 dígitos.</summary>
internal sealed class PedidoDeDispositivo : Entidade, ICasalScoped
{
    public static readonly TimeSpan Validade = TimeSpan.FromMinutes(10);

    public Guid CasalId { get; private set; }
    public Guid UsuarioId { get; private set; }
    public byte[] TokenHmac { get; private set; } = [];
    public string Codigo { get; private set; } = string.Empty;
    public EstadoDoPedido Estado { get; private set; }
    public DateTimeOffset ExpiraEm { get; private set; }
    public Guid? AprovadoPor { get; private set; }

    private PedidoDeDispositivo() { }

    public static PedidoDeDispositivo Criar(Guid casalId, Guid usuarioId, byte[] tokenHmac, string codigo, DateTimeOffset agora)
    {
        if (codigo is not { Length: 6 } || !codigo.All(char.IsAsciiDigit))
            throw new ArgumentException("O código deve ter 6 dígitos.", nameof(codigo));
        return new PedidoDeDispositivo
        {
            CasalId = casalId, UsuarioId = usuarioId, TokenHmac = tokenHmac, Codigo = codigo,
            Estado = EstadoDoPedido.Pendente, ExpiraEm = agora + Validade,
        };
    }

    public bool Expirou(DateTimeOffset agora) => agora >= ExpiraEm;

    /// <summary>O pedido não pode ser aprovado pelo próprio dono.</summary>
    public void Aprovar(Guid aprovadorId)
    {
        if (aprovadorId == UsuarioId) throw new InvalidOperationException("O pedido não pode ser aprovado pelo próprio dono.");
        if (Estado != EstadoDoPedido.Pendente) throw new InvalidOperationException("Só pedido pendente pode ser aprovado.");
        Estado = EstadoDoPedido.Aprovado;
        AprovadoPor = aprovadorId;
    }

    public void MarcarUsado()
    {
        if (Estado != EstadoDoPedido.Aprovado) throw new InvalidOperationException("Só pedido aprovado pode ser usado.");
        Estado = EstadoDoPedido.Usado;
    }
}

internal enum EstadoDoPedido
{
    Pendente,
    Aprovado,
    Usado,
}
