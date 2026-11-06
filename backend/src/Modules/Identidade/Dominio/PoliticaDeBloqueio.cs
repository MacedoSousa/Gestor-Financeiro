namespace GestorFinanceiro.Modules.Identidade.Dominio;

/// <summary>Bloqueio progressivo por falhas seguidas de login: 15 min, dobrando a cada bloqueio, até 24 h.</summary>
internal static class PoliticaDeBloqueio
{
    public static TimeSpan DuracaoDoBloqueio(int bloqueiosAnteriores)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bloqueiosAnteriores);
        // 15 min x 2^7 já passa de 24 h; o limite evita estouro do deslocamento.
        var fator = 1L << Math.Min(bloqueiosAnteriores, 16);
        var duracao = TimeSpan.FromTicks(TemposDoLogin.BloqueioBase.Ticks * fator);
        return duracao > TemposDoLogin.BloqueioTeto ? TemposDoLogin.BloqueioTeto : duracao;
    }
}
