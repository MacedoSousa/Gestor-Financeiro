using GestorFinanceiro.Modules.Contas.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Dominio;
using GestorFinanceiro.Modules.Lancamentos.Infra;
using GestorFinanceiro.SharedKernel.Seguranca;
using Microsoft.EntityFrameworkCore;

namespace GestorFinanceiro.Modules.Lancamentos.Aplicacao;

/// <summary>
/// "Parear como transferência" (D9, D10, Tarefa 6.1), sobre linhas já rastreadas, dentro da transação e das travas de
/// quem chama. Mantém os ids, tira a categoria, põe as duas pernas no mesmo grupo; a auditoria do banco grava a mudança.
/// </summary>
internal sealed class Pareamento(LancamentosDbContext db, IConsultaDeContas contas, ICofre cofre)
{
    /// <summary>Prefixo da chave do lado criado pelo sistema (D9, D10, D12): par:&lt;id da transação importada&gt;.</summary>
    internal static string ChaveDoPar(Guid transacaoId) => $"par:{transacaoId:D}";

    /// <summary>As duas pernas do banco viram o par: a saída (negativa) e a entrada (positiva), em contas diferentes.</summary>
    public static void Parear(Transacao saida, Transacao entrada)
    {
        if (saida.ValorCentavos >= 0 || entrada.ValorCentavos != -saida.ValorCentavos || saida.ContaId == entrada.ContaId)
            throw new InvalidOperationException("Par de transferência incoerente.");
        var grupo = Guid.CreateVersion7();
        saida.VirarPernaDeTransferencia(grupo);
        entrada.VirarPernaDeTransferencia(grupo);
    }

    /// <summary>
    /// O outro lado não vem do banco (conta manual, D9; cartão não ligado, D10; o grupo de investimento, D12): o sistema o
    /// cria, origem SISTEMA, chave par:&lt;id&gt;, com o valor oposto e a mesma data. Repetir não cria outro.
    /// </summary>
    public async Task<Transacao> CriarParManualAsync(Transacao importada, ContaResumo destino, CancellationToken ct)
    {
        var chave = ChaveDoPar(importada.Id);
        var existente = await db.Transacoes.FirstOrDefaultAsync(t => t.Origem == OrigemDaTransacao.Sistema && t.IdExterno == chave, ct);
        if (existente is not null) return existente;
        if (destino.Id == importada.ContaId) throw new InvalidOperationException("O par é de outra conta.");

        var grupo = Guid.CreateVersion7();
        var lado = Transacao.CriarDoSistema(importada.CasalId, destino.Id, null, importada.Data, -importada.ValorCentavos,
            NaturezaDaTransacao.Transferencia, importada.LerDescricao(cofre), chave, grupo, contas.VencimentoDaFatura(destino, importada.Data), cofre);
        importada.VirarPernaDeTransferencia(grupo);
        db.Transacoes.Add(lado);
        return lado;
    }
}

/// <summary>Dias úteis (segunda a sexta; feriado fica para depois) entre duas datas.</summary>
internal static class DiasUteis
{
    /// <summary>Quantos dias úteis há depois de <paramref name="de"/> até <paramref name="ate"/> (inclusive); 0 se ate &lt;= de.</summary>
    public static int Entre(DateOnly de, DateOnly ate)
    {
        var n = 0;
        for (var d = de.AddDays(1); d <= ate; d = d.AddDays(1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) n++;
        return n;
    }

    /// <summary>Distância em dias úteis, nos dois sentidos.</summary>
    public static int Distancia(DateOnly a, DateOnly b) => a <= b ? Entre(a, b) : Entre(b, a);
}
