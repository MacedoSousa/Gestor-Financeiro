namespace GestorFinanceiro.Modules.Lancamentos.Aplicacao;

/// <summary>"Hoje" do casal: a data no fuso de Brasília, não a do servidor nem a UTC.</summary>
internal static class Brasilia
{
    private static readonly TimeZoneInfo Fuso = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    public static DateOnly Hoje(TimeProvider relogio) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(relogio.GetUtcNow(), Fuso).DateTime);
}
