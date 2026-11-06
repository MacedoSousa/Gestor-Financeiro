namespace GestorFinanceiro.Modules.Identidade.Dominio;

/// <summary>Todas as durações e limites do login num só lugar.</summary>
internal static class TemposDoLogin
{
    public const int FalhasParaBloquear = 5;
    public static readonly TimeSpan BloqueioBase = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan BloqueioTeto = TimeSpan.FromHours(24);
    public static readonly TimeSpan SessaoParada = TimeSpan.FromDays(7);
    public static readonly TimeSpan SessaoAbsoluta = TimeSpan.FromDays(30);
    public static readonly TimeSpan ConfirmacaoRecente = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Pedido = TimeSpan.FromMinutes(10);
    /// <summary>Pedidos de celular novo PENDENTES e no prazo que um cônjuge pode ter ao mesmo tempo.</summary>
    public const int PedidosPendentesPorConjuge = 3;
    public static readonly TimeSpan AutorizacaoDeRegistro = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan DesafioWebAuthn = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RegravacaoDoUltimoUso = TimeSpan.FromHours(1);
}
