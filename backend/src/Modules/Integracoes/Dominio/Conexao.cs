using GestorFinanceiro.SharedKernel.Dominio;

namespace GestorFinanceiro.Modules.Integracoes.Dominio;

/// <summary>
/// Conexão do Meu Pluggy de um cônjuge (ADR-015, D20): guarda só quem é o titular e o slot das chaves no servidor
/// (<c>pluggy_&lt;slot&gt;_client_*</c>, D23). Estado e consentimento ficam em cada <see cref="Item"/> (D18).
/// Uma por cônjuge e uma por slot no casal, só entre as não apagadas.
/// </summary>
internal sealed class Conexao : Entidade, ICasalScoped
{
    public const int SlotMinimo = 1;
    public const int SlotMaximo = 2;

    public Guid CasalId { get; private set; }
    public Guid UsuarioId { get; private set; }
    public short Slot { get; private set; }

    private Conexao() { }

    public static Conexao Criar(Guid casalId, Guid usuarioId, int slot)
    {
        if (casalId == Guid.Empty) throw new ArgumentException("Casal vazio.", nameof(casalId));
        if (usuarioId == Guid.Empty) throw new ArgumentException("Usuário vazio.", nameof(usuarioId));
        ArgumentOutOfRangeException.ThrowIfLessThan(slot, SlotMinimo);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slot, SlotMaximo);
        return new Conexao { CasalId = casalId, UsuarioId = usuarioId, Slot = (short)slot };
    }

    public static bool SlotValido(int slot) => slot is >= SlotMinimo and <= SlotMaximo;
}
