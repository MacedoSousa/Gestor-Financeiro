using GestorFinanceiro.Modules.Contas.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Dominio;
using GestorFinanceiro.Modules.Lancamentos.Infra;
using Microsoft.EntityFrameworkCore;

namespace GestorFinanceiro.Modules.Lancamentos.Aplicacao;

/// <summary>Saldos e totais de fatura. Tudo é SUM no banco; nada é decifrado.</summary>
internal sealed class ConsultaDeSaldos(LancamentosDbContext db, IConsultaDeContas contas, TimeProvider relogio)
{
    public async Task<IReadOnlyList<SaldoDaContaDto>> ObterAsync(DateOnly? em, CancellationToken ct)
    {
        var data = em ?? Brasilia.Hoje(relogio);
        var lista = await contas.ListarAsync(ct);

        // Fatura aberta = a que contém a data; fechada = a imediatamente anterior (a do mês anterior ao fechamento da aberta).
        var periodos = new Dictionary<Guid, (FaturaDto Aberta, FaturaDto Fechada)>();
        foreach (var conta in lista)
        {
            if (contas.VencimentoDaFatura(conta, data) is not { } vencAberta) continue;
            var fechAberta = contas.FechamentoDaFatura(conta, vencAberta)!.Value;
            var vencFechada = contas.VencimentoDaFatura(conta, fechAberta.AddMonths(-1))!.Value;
            var fechFechada = contas.FechamentoDaFatura(conta, vencFechada)!.Value;
            periodos[conta.Id] = (new FaturaDto(vencAberta, fechAberta, 0), new FaturaDto(vencFechada, fechFechada, 0));
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

        var resultado = new List<SaldoDaContaDto>(lista.Count);
        foreach (var conta in lista)
        {
            var inicio = conta.SaldoInicialEm;
            var movimento = await db.Transacoes.AsNoTracking()
                .Where(t => t.ContaId == conta.Id && t.Data >= inicio && t.Data <= data)
                .SumAsync(t => (long?)t.ValorCentavos, ct) ?? 0;

            FaturaDto? aberta = null, fechada = null;
            if (periodos.TryGetValue(conta.Id, out var p))
            {
                aberta = p.Aberta with { TotalCentavos = totais.GetValueOrDefault((conta.Id, p.Aberta.Vencimento)) };
                fechada = p.Fechada with { TotalCentavos = totais.GetValueOrDefault((conta.Id, p.Fechada.Vencimento)) };
            }

            resultado.Add(new SaldoDaContaDto(conta.Id, conta.SaldoInicialCentavos + movimento, aberta, fechada));
        }

        return resultado;
    }
}
