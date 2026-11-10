using System.Globalization;
using GestorFinanceiro.Modules.Contas.Contracts;
using GestorFinanceiro.Modules.Investimentos.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Contracts;
using GestorFinanceiro.Modules.Orcamento.Contracts;
using GestorFinanceiro.Modules.Painel.Contracts;

namespace GestorFinanceiro.Modules.Painel.Dominio;

/// <summary>Regras da Visão geral, puras: recebem os tipos dos contratos e devolvem os DTOs do painel.</summary>
internal static class RegrasDoPainel
{
    private static readonly string[] Meses = ["jan", "fev", "mar", "abr", "mai", "jun", "jul", "ago", "set", "out", "nov", "dez"];

    public static AindaDaParaGastarDto AindaDaParaGastar(FatiaDto tetoDeGastos, DateOnly hoje, long? faturaVencidaCentavos = null) =>
        AindaDaParaGastarSemFatura(tetoDeGastos, hoje) with { FaturaVencidaCentavos = faturaVencidaCentavos };

    /// <summary>Soma do que está vencido e não pago em todos os cartões; nulo quando não há nada.</summary>
    public static long? TotalVencido(IReadOnlyList<FaturasDoCartao> faturas)
    {
        var total = faturas.Sum(f => f.Vencida?.RestanteCentavos ?? 0);
        return total > 0 ? total : null;
    }

    private static AindaDaParaGastarDto AindaDaParaGastarSemFatura(FatiaDto tetoDeGastos, DateOnly hoje)
    {
        var fimDoMes = new DateOnly(hoje.Year, hoje.Month, DateTime.DaysInMonth(hoje.Year, hoje.Month));
        var usado = tetoDeGastos.UsadoCentavos;

        if (tetoDeGastos.TetoCentavos is not { } teto)
            return new("SEM_TETO", null, usado, null, null, null, fimDoMes);

        var restante = teto - usado;
        int? percentual = teto > 0 ? (int)(usado * 100 / teto) : null;

        if (restante < 0) return new("PASSOU", teto, usado, restante, null, percentual, fimDoMes);
        if (restante == 0) return new("ESGOTADO", teto, usado, restante, null, percentual, fimDoMes);

        var dias = fimDoMes.Day - hoje.Day + 1;
        var porDia = restante / dias / 100 * 100;
        return new("COM_TETO", teto, usado, restante, porDia, percentual, fimDoMes);
    }

    public static SaldoDasContasDto SaldoDasContas(IReadOnlyList<ContaResumo> contas, IReadOnlyDictionary<Guid, long> saldos)
    {
        var linhas = contas
            .Where(c => !c.Arquivada && c.Tipo is "CORRENTE" or "DINHEIRO")
            .Select(c => new ContaNoSaldoDto(c.Id, c.Apelido, saldos.GetValueOrDefault(c.Id)))
            .OrderByDescending(c => c.SaldoCentavos)
            .ThenBy(c => c.Apelido, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var total = linhas.Sum(c => c.SaldoCentavos);
        return linhas.Count <= 3
            ? new(total, linhas, 0)
            : new(total, linhas.Take(2).ToList(), linhas.Count - 2);
    }

    public static AportePlanejadoDto AportePlanejado(IReadOnlyList<MetaDto> metas) =>
        new(metas.Sum(m => m.AportePlanejadoCentavos),
            metas.Sum(m => m.AporteDoMesCentavos),
            metas.Select(m => new AporteDaMetaDto(m.Id, m.Nome, m.AportePlanejadoCentavos)).ToList());

    /// <summary>Patrimônio de hoje = último ponto; variação = ele menos o ponto do mês anterior.</summary>
    public static PatrimonioDto Patrimonio(IReadOnlyList<PatrimonioDoMes> pontos)
    {
        var hoje = pontos.Count > 0 ? pontos[^1].TotalCentavos : null;
        var anterior = pontos.Count > 1 ? pontos[^2].TotalCentavos : null;

        if (hoje is not { } total) return new(0, null, null);
        if (anterior is not { } antes) return new(total, null, null);

        var variacao = total - antes;
        decimal? percentual = antes > 0
            ? Math.Round(variacao * 100m / antes, 1, MidpointRounding.AwayFromZero)
            : null;
        return new(total, variacao, percentual);
    }

    public static PatrimonioEm12MesesDto PatrimonioEm12Meses(IReadOnlyList<PatrimonioDoMes> pontos)
    {
        if (pontos.Count == 0) return new("", []);

        var dtos = pontos.Select(p => new PontoDoPatrimonioDto(p.Mes, Rotulo(p.Mes), p.TotalCentavos)).ToList();
        return new($"{Rotulo(pontos[0].Mes)}/{Ano(pontos[0].Mes)} → {Rotulo(pontos[^1].Mes)}/{Ano(pontos[^1].Mes)}", dtos);
    }

    public static IReadOnlyList<AvisoDeFaturaDto> AvisosDeFatura(
        IReadOnlyList<FaturasDoCartao> faturas, IReadOnlyDictionary<Guid, ContaResumo> contas, DateOnly hoje)
    {
        var avisos = new List<AvisoDeFaturaDto>();
        foreach (var f in faturas)
        {
            var dias = hoje.DayNumber - f.Fechada.Fechamento.DayNumber;
            if (dias < 0 || dias > 5 || !contas.TryGetValue(f.ContaId, out var conta) || conta.Arquivada) continue;

            avisos.Add(new(f.ContaId,
                $"Compras no cartão {conta.Apelido} a partir de hoje só vencem em {DiaMes(f.Aberta.Vencimento)} — a fatura fechou em {DiaMes(f.Fechada.Fechamento)}"));
        }
        return avisos;
    }

    public static MetasDoPainelDto Metas(IReadOnlyList<MetaDto> metas) =>
        new(metas.Take(3)
                .Select(m => new MetaDoPainelDto(m.Id, m.Nome, m.ProgressoPercentual, m.GuardadoCentavos,
                    m.ValorAlvoCentavos, m.Projecao.Situacao, m.Projecao.ChegaEm))
                .ToList(),
            metas.Count);

    public static OrcamentoDoPainelDto Orcamento(IReadOnlyList<TetoDaCategoriaDto> categorias)
    {
        var comTeto = categorias
            .Where(c => c.Papel is null && c.TetoCentavos is not null)
            .Select(c =>
            {
                var teto = c.TetoCentavos!.Value;
                var passou = c.UsadoCentavos > teto;
                var razao = teto > 0 ? c.UsadoCentavos / (decimal)teto : c.UsadoCentavos > 0 ? decimal.MaxValue : 0m;
                var percentual = teto > 0 ? (int)(c.UsadoCentavos * 100 / teto) : c.UsadoCentavos > 0 ? 100 : 0;
                return (Razao: razao, Dto: new CategoriaDoPainelDto(c.CategoriaId, c.Nome, teto, c.UsadoCentavos,
                    percentual, passou ? "PASSOU" : "NORMAL"));
            })
            .OrderByDescending(x => x.Razao)
            .ThenBy(x => x.Dto.Nome, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Dto)
            .ToList();

        return new(comTeto.Take(6).ToList(), Math.Max(0, comTeto.Count - 6));
    }

    public static IReadOnlyList<VencimentoDto> Vencimentos(
        IReadOnlyList<FaturasDoCartao> faturas, IReadOnlyDictionary<Guid, ContaResumo> contas, DateOnly hoje)
    {
        var vencidas = new List<VencimentoDto>();
        var linhas = new List<VencimentoDto>();
        foreach (var f in faturas)
        {
            if (!contas.TryGetValue(f.ContaId, out var conta)) continue;

            if (f.Vencida is { RestanteCentavos: > 0 } v)
                vencidas.Add(new(f.ContaId, conta.Apelido, v.Vencimento, v.Fechamento, "VENCIDA", v.RestanteCentavos, 0));

            if (f.Fechada.Vencimento >= hoje && f.Fechada.RestanteCentavos > 0)
                linhas.Add(new(f.ContaId, conta.Apelido, f.Fechada.Vencimento, f.Fechada.Fechamento,
                    f.Fechada.AtrasoCentavos > 0 ? "COM_ATRASO" : "FECHADA",
                    f.Fechada.RestanteCentavos, f.Fechada.AtrasoCentavos > 0 ? f.Fechada.AtrasoCentavos : 0));

            if (f.Aberta.TotalAteHojeCentavos > 0)
                linhas.Add(new(f.ContaId, conta.Apelido, f.Aberta.Vencimento, f.Aberta.Fechamento, "ABERTA",
                    f.Aberta.TotalAteHojeCentavos, 0));
        }

        // As vencidas vêm no topo e todas aparecem; o corte de 5 vale só para as que ainda vão vencer.
        return Ordenar(vencidas).Concat(Ordenar(linhas).Take(5)).ToList();
    }

    private static IEnumerable<VencimentoDto> Ordenar(IEnumerable<VencimentoDto> linhas) =>
        linhas
            .OrderBy(v => v.Vencimento)
            .ThenBy(v => v.Fechamento)
            .ThenBy(v => v.Apelido, StringComparer.OrdinalIgnoreCase);

    private static string DiaMes(DateOnly d) => d.ToString("dd/MM", CultureInfo.InvariantCulture);

    private static string Rotulo(string mes) => Meses[int.Parse(mes.AsSpan(5, 2), CultureInfo.InvariantCulture) - 1];

    private static string Ano(string mes) => mes[..4];
}
