using GestorFinanceiro.IntegrationTests.Infra;
using System.Net;
using System.Net.Http.Json;
using GestorFinanceiro.Modules.Lancamentos.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace GestorFinanceiro.IntegrationTests;

/// <summary>Últimos lançamentos (IConsultaDoExtrato): data desc, depois Id desc; sem data futura; só do casal. Relógio: 05/11/2026.</summary>
[Collection("postgres")]
public class UltimosLancamentosTests(PostgresFixture pg)
{
    private static async Task<IReadOnlyList<LancamentoDto>> UltimosAsync(PatrimonioTests.Contexto c, int quantos)
    {
        await using var escopo = c.Api.Services.CreateAsyncScope();
        return await escopo.ServiceProvider.GetRequiredService<IConsultaDoExtrato>().UltimosAsync(quantos);
    }

    private static async Task LancarAsync(HttpClient cliente, Guid conta, long valor, string data, string descricao)
    {
        var lista = await PatrimonioTests.JsonAsync(await cliente.GetAsync("/api/v1/categorias"));
        var categoria = lista.EnumerateArray().Single(x => x.GetProperty("nome").GetString() == "Mercado").GetProperty("id").GetGuid();
        var resposta = await cliente.PostAsJsonAsync("/api/v1/lancamentos", new
        {
            chaveIdempotente = Guid.NewGuid(), contaId = conta, natureza = "DESPESA", valorCentavos = valor, data,
            descricao, contraparte = (string?)null, categoriaId = categoria, parcelas = 1,
        });
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
    }

    [Fact]
    public async Task Ultimos_vem_por_data_e_id_e_respeitam_o_casal()
    {
        await using var c = await PatrimonioTests.IniciarAsync(pg);
        var conta = await PatrimonioTests.CriarCorrenteAsync(c.Cliente);
        foreach (var (dia, n) in new[] { ("2026-10-01", 1), ("2026-10-02", 2), ("2026-10-03", 3), ("2026-10-04", 4), ("2026-10-05", 5), ("2026-10-06", 6) })
            await LancarAsync(c.Cliente, conta, n * 1000, dia, $"Compra {n}");
        // Mesmo dia: o criado depois (Id v7 maior) vem antes.
        await LancarAsync(c.Cliente, conta, 7000, "2026-10-06", "Compra 7");
        var outro = Guid.NewGuid();
        var contaAlheia = Guid.NewGuid();
        await PatrimonioTests.ExecutarAsync(c.Banco, $"INSERT INTO identidade.casais (id, nome_familia) VALUES ('{outro}', 'Outra')");
        await PatrimonioTests.ExecutarAsync(c.Banco,
            "INSERT INTO contas.contas (id, casal_id, tipo, apelido, saldo_inicial_centavos, saldo_inicial_em) " +
            $"VALUES ('{contaAlheia}', '{outro}', 'CORRENTE', 'Alheia', 0, '2026-09-01')");
        await PatrimonioTests.ExecutarAsync(c.Banco,
            "INSERT INTO lancamentos.transacoes (id, casal_id, conta_id, categoria_id, data, valor_centavos, natureza, descricao_cifrada, origem, id_externo) " +
            $"SELECT '{Guid.NewGuid()}', '{outro}', '{contaAlheia}', id, '2026-11-01', -99000, 'DESPESA', '\\x01', 'MANUAL', '{Guid.NewGuid()}' FROM lancamentos.categorias LIMIT 1");

        var ultimos = await UltimosAsync(c, 5);

        Assert.Equal(["Compra 7", "Compra 6", "Compra 5", "Compra 4", "Compra 3"], ultimos.Select(l => l.Descricao).ToArray());
        Assert.All(ultimos, l => Assert.Equal(conta, l.Conta.Id));
    }

    [Fact]
    public async Task Ultimos_ignora_lancamento_de_data_futura()
    {
        await using var c = await PatrimonioTests.IniciarAsync(pg);
        var conta = await PatrimonioTests.CriarCorrenteAsync(c.Cliente);
        await LancarAsync(c.Cliente, conta, 1000, "2026-11-03", "Hoje ou antes");
        await LancarAsync(c.Cliente, conta, 2000, "2026-11-15", "Futuro");

        var ultimos = await UltimosAsync(c, 5);

        Assert.Equal(["Hoje ou antes"], ultimos.Select(l => l.Descricao).ToArray());
    }
}
