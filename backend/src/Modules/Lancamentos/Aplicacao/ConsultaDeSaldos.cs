using GestorFinanceiro.Modules.Contas.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Dominio;
using GestorFinanceiro.Modules.Lancamentos.Infra;
using Microsoft.EntityFrameworkCore;

namespace GestorFinanceiro.Modules.Lancamentos.Aplicacao;

/// <summary>Saldos e totais de fatura. Tudo é SUM no banco; nada é decifrado.</summary>
internal sealed class ConsultaDeSaldos(LancamentosDbContext db, IConsultaDeContas contas, ConsultaDeSaldosPorConta saldosPorConta, TimeProvider relogio)
{
    public async Task<IReadOnlyList<SaldoDaContaDto>> ObterAsync(DateOnly? em, CancellationToken ct)
    {
        var data = em ?? Brasilia.Hoje(relogio);
        var lista = await contas.ListarAsync(ct);

        var periodos = new Dictionary<Guid, (FaturaDto Aberta, FaturaDto Fechada)>();
        foreach (var conta in lista)
        {
            if (PeriodosDeFatura.De(contas, conta, data) is not var (aberta, fechada)) continue;
            periodos[conta.Id] = (new FaturaDto(aberta.Vencimento, aberta.Fechamento, 0), new FaturaDto(fechada.Vencimento, fechada.Fechamento, 0));
        }

        var vencimentos = periodos.Values.SelectMany(p => new[] { p.Aberta.Vencimento, p.Fechada.Vencimento }).Distinct().ToArray();
        var totais = vencimentos.Length == 0
            ? []
            : (await db.Transacoes.AsNoTracking()
                .Where(t => t.FaturaVencimento != null && vencimentos.Contains(t.FaturaVencimento.Value)
                            && t.Natureza != NaturezaDaTransacao.Transferencia)
                .GroupBy(t => new { t.ContaId, Vencimento = t.FaturaVencimento!.Value })
                .Select(g => new { g.Key.ContaId, g.Key.Vencimento, Soma = g.Sum(t => t.ValorCentavos) })
                .ToListAsync(ct))
              .ToDictionary(x => (x.ContaId, x.Vencimento), x => -x.Soma);

        var saldos = await saldosPorConta.SaldosAsync(lista, data, ct);
        var resultado = new List<SaldoDaContaDto>(lista.Count);
        foreach (var conta in lista)
        {
            FaturaDto? aberta = null, fechada = null;
            if (periodos.TryGetValue(conta.Id, out var p))
            {
                aberta = p.Aberta with { TotalCentavos = totais.GetValueOrDefault((conta.Id, p.Aberta.Vencimento)) };
                fechada = p.Fechada with { TotalCentavos = totais.GetValueOrDefault((conta.Id, p.Fechada.Vencimento)) };
            }

            resultado.Add(new SaldoDaContaDto(conta.Id, saldos[conta.Id], aberta, fechada));
        }

        return resultado;
    }
}
