namespace GestorFinanceiro.UnitTests;

/// <summary>Relógio controlado pelo teste.</summary>
internal sealed class RelogioFixo : TimeProvider
{
    public DateTimeOffset Agora { get; set; } = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Agora;
}
