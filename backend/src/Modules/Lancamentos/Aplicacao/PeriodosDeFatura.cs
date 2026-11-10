using GestorFinanceiro.Modules.Contas.Contracts;

namespace GestorFinanceiro.Modules.Lancamentos.Aplicacao;

/// <summary>Fatura aberta = a que contém a data; fechada = a imediatamente anterior (a do mês anterior ao fechamento da aberta).</summary>
internal static class PeriodosDeFatura
{
    internal readonly record struct Periodo(DateOnly Vencimento, DateOnly Fechamento);

    /// <summary>Nulo se a conta não for cartão.</summary>
    public static (Periodo Aberta, Periodo Fechada)? De(IConsultaDeContas contas, ContaResumo conta, DateOnly data)
    {
        if (contas.VencimentoDaFatura(conta, data) is not { } vencAberta) return null;
        var fechAberta = contas.FechamentoDaFatura(conta, vencAberta)!.Value;
        var vencFechada = contas.VencimentoDaFatura(conta, fechAberta.AddMonths(-1))!.Value;
        var fechFechada = contas.FechamentoDaFatura(conta, vencFechada)!.Value;
        return (new Periodo(vencAberta, fechAberta), new Periodo(vencFechada, fechFechada));
    }
}
