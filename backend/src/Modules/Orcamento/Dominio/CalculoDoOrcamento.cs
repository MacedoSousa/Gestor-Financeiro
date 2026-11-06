using GestorFinanceiro.Modules.Lancamentos.Contracts;
using GestorFinanceiro.Modules.Orcamento.Contracts;

namespace GestorFinanceiro.Modules.Orcamento.Dominio;

internal static class CalculoDoOrcamento
{
    public static OrcamentoDoMesDto Calcular(
        MesDoOrcamento mes, long? receitaPrevista, long receitaSugerida,
        IReadOnlyList<CategoriaDeDespesa> categorias,
        IReadOnlyDictionary<Guid, long> tetos, IReadOnlyDictionary<Guid, long> uso,
        long aportes, MesDoOrcamento? mesParaCopiar)
    {
        long? Teto(Guid id) => tetos.TryGetValue(id, out var t) ? t : null;
        long Usado(Guid id) => uso.TryGetValue(id, out var u) ? u : 0;

        decimal? Percentual(long? teto) =>
            receitaPrevista is > 0 && teto is not null
                ? Math.Round(teto.Value * 100m / receitaPrevista.Value, 1, MidpointRounding.AwayFromZero)
                : null;

        var linhas = categorias
            .Where(c => !c.Arquivada || Teto(c.Id) is not null || Usado(c.Id) != 0)
            .OrderBy(c => c.Papel == PapelDeCategoria.Investimento ? 0 : c.Papel == PapelDeCategoria.Dividas ? 1 : 2)
            .ThenBy(c => c.Nome, StringComparer.CurrentCultureIgnoreCase)
            .Select(c =>
            {
                var teto = Teto(c.Id);
                var usado = Usado(c.Id);
                return new TetoDaCategoriaDto(c.Id, c.Nome, c.Papel, c.Arquivada, teto, usado, teto - usado);
            })
            .ToList();

        var investimento = linhas.FirstOrDefault(l => l.Papel == PapelDeCategoria.Investimento);
        var dividas = linhas.FirstOrDefault(l => l.Papel == PapelDeCategoria.Dividas);
        var comuns = linhas.Where(l => l.Papel is null).ToList();

        var tetoInvestir = investimento?.TetoCentavos;
        var tetoDividas = dividas?.TetoCentavos;
        var tetoGastos = comuns.Sum(l => l.TetoCentavos ?? 0);
        var usadoGastos = comuns.Sum(l => l.UsadoCentavos);

        var fatias = new FatiasDto(
            new FatiaDeInvestirDto(tetoInvestir, (investimento?.UsadoCentavos ?? 0) + aportes, aportes, Percentual(tetoInvestir)),
            new FatiaDto(tetoDividas, dividas?.UsadoCentavos ?? 0, Percentual(tetoDividas)),
            new FatiaDto(tetoGastos, usadoGastos, Percentual(tetoGastos)));

        long? folga = receitaPrevista - ((tetoInvestir ?? 0) + tetoGastos + (tetoDividas ?? 0));

        return new OrcamentoDoMesDto(mes.Texto, receitaPrevista, receitaSugerida, fatias, folga, linhas, mesParaCopiar?.Texto);
    }
}
