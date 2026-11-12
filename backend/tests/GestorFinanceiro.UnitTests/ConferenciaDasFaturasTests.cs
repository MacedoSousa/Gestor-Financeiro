using GestorFinanceiro.Modules.Integracoes.Pluggy;

namespace GestorFinanceiro.UnitTests;

/// <summary>
/// Conferência do cartão por fatura (CEO 11/11 21h, decisão 12): para cada fatura do banco, o total informado contra a
/// soma das transações com aquele billId. Pendentes (sem billId) não entram. Valores no sinal da Pluggy no cartão:
/// compra positiva, pagamento ou estorno negativo.
/// </summary>
public class ConferenciaDasFaturasTests
{
    private static readonly DateOnly Desde = new(2026, 1, 1);

    private static FaturaPluggy Fatura(string id, string vencimento, long total) =>
        new(id, DateOnly.Parse(vencimento, System.Globalization.CultureInfo.InvariantCulture), null, total);

    private static TransacaoPluggy Tx(string id, long valor, string? fatura, string status = "POSTED") =>
        new(id, new DateOnly(2026, 4, 1), valor, valor >= 0 ? "DEBIT" : "CREDIT", status, "Compra sintética",
            null, null, null, null, fatura);

    [Fact]
    public void Fatura_que_fecha_nao_aparece()
    {
        var faturas = new[] { Fatura("b4", "2026-04-10", 70000) };
        var tx = new[] { Tx("t1", 50000, "b4"), Tx("t2", 30000, "b4"), Tx("t3", -10000, "b4") };

        Assert.Empty(ConferenciaDasFaturas.Diferencas(faturas, tx, Desde));
    }

    [Fact]
    public void Fatura_diferente_aparece_com_os_dois_totais()
    {
        var faturas = new[] { Fatura("b4", "2026-04-10", 70000), Fatura("b5", "2026-05-10", 1000) };
        var tx = new[] { Tx("t1", 50000, "b4"), Tx("t2", 1000, "b5") };

        var d = Assert.Single(ConferenciaDasFaturas.Diferencas(faturas, tx, Desde));

        Assert.Equal(new DiferencaDeFatura("b4", new DateOnly(2026, 4, 10), 70000, 50000), d);
        Assert.Equal(20000, d.DiferencaCentavos);
    }

    [Fact]
    public void Pendente_sem_billId_nao_entra()
    {
        var faturas = new[] { Fatura("b4", "2026-04-10", 50000) };
        var tx = new[] { Tx("t1", 50000, "b4"), Tx("p1", 20000, null, status: "PENDING") };

        Assert.Empty(ConferenciaDasFaturas.Diferencas(faturas, tx, Desde));
    }

    [Fact]
    public void Transacao_de_fatura_que_o_banco_nao_listou_nao_entra()
    {
        var faturas = new[] { Fatura("b4", "2026-04-10", 50000) };
        var tx = new[] { Tx("t1", 50000, "b4"), Tx("t9", 99000, "b-aberta") };

        Assert.Empty(ConferenciaDasFaturas.Diferencas(faturas, tx, Desde));
    }

    [Fact]
    public void Fatura_sem_transacao_e_total_zero_nao_aparece()
    {
        // Cartão sem uso no ano (segundo banco da medição): faturas zeradas não são divergência.
        var faturas = Enumerable.Range(2, 11).Select(m => Fatura($"z{m}", $"2026-{m:00}-10", 0)).ToArray();

        Assert.Empty(ConferenciaDasFaturas.Diferencas(faturas, [], Desde));
    }

    [Fact]
    public void Fatura_que_pode_ter_compra_antes_do_historico_nao_e_conferida()
    {
        // Vence antes de dois meses depois do início do histórico: pode ter compras de antes de 2026-01-01, que não foram
        // importadas. Não é conferida (nem aparece como diferença).
        var faturas = new[] { Fatura("b1", "2026-01-10", 90000), Fatura("b2", "2026-02-28", 40000), Fatura("b3", "2026-03-01", 40000) };
        var tx = new[] { Tx("t1", 10000, "b1"), Tx("t2", 10000, "b2"), Tx("t3", 10000, "b3") };

        var d = Assert.Single(ConferenciaDasFaturas.Diferencas(faturas, tx, Desde));
        Assert.Equal("b3", d.FaturaId);
    }
}
