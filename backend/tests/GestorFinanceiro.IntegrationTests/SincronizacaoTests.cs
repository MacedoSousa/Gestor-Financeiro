using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GestorFinanceiro.IntegrationTests.Infra;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.Modules.Integracoes.Pluggy;
using GestorFinanceiro.UnitTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace GestorFinanceiro.IntegrationTests;

/// <summary>
/// Fase 2, Tarefa 4.5: sincronização de um item (histórico de 2026, saldo inicial, conferência de saldo e de fatura,
/// aplicação nova e reconciliação de conta ligada órfã, D18). Só a Pluggy falsa e dados sintéticos, no formato medido em
/// 11/11: na corrente o sinal da Pluggy é o do sistema; no cartão a compra vem positiva (DEBIT) e o pagamento negativo
/// (CREDIT); pendente sem billId. Relógio em 05/11/2026.
/// </summary>
[Collection("postgres")]
public class SincronizacaoTests(PostgresFixture pg)
{
    private const string IdSlot1 = "id-sintetico-do-slot-1";
    private const string IdSlot2 = "id-sintetico-do-slot-2";

    private static readonly ContaPluggy Corrente =
        new("conta-sintetica-1", "BANK", "CHECKING_ACCOUNT", "Conta Corrente", null, "0001/12345-6", 150_000, null, null, null);

    private static readonly ContaPluggy Cartao =
        new("cartao-sintetico-1", "CREDIT", "CREDIT_CARD", "Cartão Platinum", null, null, 80_000, 500_000, "ANA TESTE", "4821");

    private sealed record Ctx(ApiDeTeste Api, BancoDeTeste Banco, ClientePluggyFalso Pluggy, RelogioFixo Relogio, HttpClient Ana,
        HttpClient Bruno, Guid AnaId, string Pasta) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Api.DisposeAsync();
            Directory.Delete(Pasta, recursive: true);
        }
    }

    private async Task<Ctx> IniciarAsync(Action<IServiceCollection>? mais = null)
    {
        var pasta = Directory.CreateTempSubdirectory("gf-pluggy-").FullName;
        await File.WriteAllTextAsync(Path.Combine(pasta, "pluggy_1_client_id"), IdSlot1);
        await File.WriteAllTextAsync(Path.Combine(pasta, "pluggy_1_client_secret"), "segredo-sintetico-do-slot-1");
        await File.WriteAllTextAsync(Path.Combine(pasta, "pluggy_2_client_id"), IdSlot2);
        await File.WriteAllTextAsync(Path.Combine(pasta, "pluggy_2_client_secret"), "segredo-sintetico-do-slot-2");

        var pluggy = new ClientePluggyFalso();
        var relogio = new RelogioFixo();
        var banco = await pg.NovoBancoAsync();
        var api = new ApiDeTeste(banco, relogio: relogio, servicos: s =>
        {
            s.Replace(ServiceDescriptor.Singleton<IClientePluggy>(pluggy));
            s.Configure<OpcoesDaPluggy>(o => o.PastaDeSegredos = pasta);
            mais?.Invoke(s);
        });
        var pedido = new CadastroDoCasalDto(
            api.CodigoInstalacao, "Família Teste",
            new ConjugeNovoDto("Ana Teste", CpfSintetico.Gerar(1), "senha-da-ana-123"),
            new ConjugeNovoDto("Bruno Teste", CpfSintetico.Gerar(2), "senha-do-bruno-1"));
        Assert.Equal(HttpStatusCode.Created, (await api.CreateClient().PostAsJsonAsync("/api/v1/casal", pedido)).StatusCode);
        var ana = await EscalarAsync<Guid>(banco, "SELECT id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'");
        var bruno = await EscalarAsync<Guid>(banco, "SELECT id FROM identidade.usuarios WHERE nome_completo = 'Bruno Teste'");
        return new Ctx(api, banco, pluggy, relogio, await api.ClienteLogadoAsync(ana), await api.ClienteLogadoAsync(bruno), ana, pasta);
    }

    private static async Task<T> EscalarAsync<T>(BancoDeTeste banco, string sql)
    {
        await using var db = new NpgsqlConnection(banco.ConexaoSuperusuario);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, db);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task EsperarAsync(Task<HttpResponseMessage> chamada, HttpStatusCode esperado)
    {
        var r = await chamada;
        Assert.True(r.StatusCode == esperado, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
    }

    /// <summary>Conexão da Ana com o item, as contas no banco falso e o nascimento delas (CRIAR); devolve o id do item.</summary>
    private static async Task<(Guid Item, string ItemPluggy)> NascerAsync(Ctx c, params ContaPluggy[] contas)
    {
        var itemPluggy = $"item-sintetico-{Guid.NewGuid():N}";
        c.Pluggy.Cadastrar(IdSlot1, itemPluggy);
        foreach (var conta in contas) c.Pluggy.CadastrarConta(itemPluggy, conta);
        var r = await c.Ana.PostAsJsonAsync("/api/v1/integracoes/conexoes", new { slot = 1, itemId = itemPluggy, codigoBanco = "341" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var item = (await JsonAsync(r)).GetProperty("items")[0].GetProperty("id").GetGuid();
        var confirmadas = contas.Select(k => new
        {
            idExterno = k.Id, acao = "CRIAR", apelido = k.Tipo == "CREDIT" ? "Itaú Cartão · Ana" : "Itaú Corrente · Ana",
            titularUsuarioId = c.AnaId, diaFechamento = k.Tipo == "CREDIT" ? 3 : (int?)null, diaVencimento = k.Tipo == "CREDIT" ? 10 : (int?)null,
        }).ToArray();
        await EsperarAsync(c.Ana.PostAsJsonAsync($"/api/v1/integracoes/items/{item}/nascimento", new { contas = confirmadas, aplicacoes = Array.Empty<object>() }),
            HttpStatusCode.NoContent);
        return (item, itemPluggy);
    }

    private static Task<HttpResponseMessage> SincronizarAsync(HttpClient cliente, Guid item) =>
        cliente.PostAsync($"/api/v1/integracoes/items/{item}/sincronizacao", null);

    private static async Task<JsonElement> SincronizarOkAsync(Ctx c, Guid item, HttpClient? cliente = null)
    {
        var r = await SincronizarAsync(cliente ?? c.Ana, item);
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return await JsonAsync(r);
    }

    private static TransacaoPluggy Tx(string id, string data, long valor, string tipo = "DEBIT", string status = "POSTED",
        string descricao = "COMPRA SINTETICA", string? fatura = null, int? parcela = null, int? parcelas = null) =>
        new(id, DateOnly.Parse(data, System.Globalization.CultureInfo.InvariantCulture), valor, tipo, status, descricao, "01010000", "Compras",
            parcela, parcelas, fatura);

    private static Task<Guid> ContaAsync(Ctx c, string idExterno) =>
        EscalarAsync<Guid>(c.Banco, $"SELECT conta_id FROM integracoes.vinculos WHERE id_externo = '{idExterno}'");

    private static async Task<long> SaldoAsync(Ctx c, Guid conta, string em)
    {
        var saldos = await JsonAsync(await c.Ana.GetAsync($"/api/v1/saldos?em={em}"));
        return saldos.EnumerateArray().Single(s => s.GetProperty("contaId").GetGuid() == conta).GetProperty("saldoCentavos").GetInt64();
    }

    /// <summary>A conta como "Conexões" mostra, dentro do item.</summary>
    private static async Task<JsonElement> ContaNaTelaAsync(Ctx c, Guid item, Guid conta)
    {
        var conexoes = await JsonAsync(await c.Ana.GetAsync("/api/v1/integracoes/conexoes"));
        var i = conexoes.GetProperty("conexoes").EnumerateArray().SelectMany(x => x.GetProperty("items").EnumerateArray())
            .Single(x => x.GetProperty("id").GetGuid() == item);
        return i.GetProperty("contas").EnumerateArray().Single(x => x.GetProperty("contaId").GetGuid() == conta);
    }

    private static Task<long> LancamentosAsync(Ctx c, Guid conta) =>
        EscalarAsync<long>(c.Banco, $"SELECT count(*) FROM lancamentos.transacoes WHERE conta_id = '{conta}' AND NOT is_deleted");

    [Fact]
    public async Task Primeira_sincronizacao_importa_2026_e_calcula_saldo_inicial_que_fecha()
    {
        await using var c = await IniciarAsync();
        var (item, _) = await NascerAsync(c, Corrente);
        c.Pluggy.CadastrarTransacao(Corrente.Id, Tx("tx-salario", "2026-01-10", 500_000, "CREDIT", descricao: "SALARIO SINTETICO"));
        c.Pluggy.CadastrarTransacao(Corrente.Id, Tx("tx-aluguel", "2026-02-03", -120_000));
        c.Pluggy.CadastrarTransacao(Corrente.Id, Tx("tx-mercado", "2026-11-03", -30_000));

        var resumo = await SincronizarOkAsync(c, item);

        Assert.Equal(1, resumo.GetProperty("contasSincronizadas").GetInt32());
        Assert.Equal(0, resumo.GetProperty("contasComFalha").GetInt32());
        Assert.Equal(3, resumo.GetProperty("lancamentosNovos").GetInt32());
        Assert.Equal((Corrente.Id, new DateOnly(2026, 1, 1)), c.Pluggy.LeiturasDeTransacoes.Single());
        var conta = await ContaAsync(c, Corrente.Id);
        Assert.Equal(3L, await EscalarAsync<long>(c.Banco,
            $"SELECT count(*) FROM lancamentos.transacoes WHERE conta_id = '{conta}' AND origem = 'OPEN_FINANCE' AND revisao = 'A_REVISAR'"));
        // Saldo do banco 1.500,00 − (5.000,00 − 1.200,00 − 300,00) = −2.000,00 em 01/01; hoje fecha com o banco.
        Assert.Equal("-200000|2026-01-01", await EscalarAsync<string>(c.Banco,
            $"SELECT saldo_inicial_centavos || '|' || saldo_inicial_em FROM contas.contas WHERE id = '{conta}'"));
        Assert.Equal(150_000, await SaldoAsync(c, conta, "2026-11-05"));
        Assert.Equal("2026-01-01|2026-11-05|0", await EscalarAsync<string>(c.Banco,
            $"SELECT historico_desde || '|' || importado_ate || '|' || diferencas_seguidas FROM integracoes.vinculos WHERE id_externo = '{Corrente.Id}'"));
        Assert.True(await EscalarAsync<bool>(c.Banco,
            $"SELECT ultima_sincronizacao_em IS NOT NULL AND ultima_tentativa_em IS NOT NULL FROM integracoes.items WHERE id = '{item}'"));
        var naTela = await ContaNaTelaAsync(c, item, conta);
        Assert.Equal("2026-01-01", naTela.GetProperty("historicoDesde").GetString());
        Assert.Equal(JsonValueKind.Null, naTela.GetProperty("diferencaCentavos").ValueKind);
    }

    [Fact]
    public async Task Sincronizar_de_novo_nao_duplica()
    {
        await using var c = await IniciarAsync();
        var (item, _) = await NascerAsync(c, Corrente);
        c.Pluggy.CadastrarTransacao(Corrente.Id, Tx("tx-mercado", "2026-11-03", -30_000));
        await SincronizarOkAsync(c, item);

        var resumo = await SincronizarOkAsync(c, item);

        Assert.Equal(0, resumo.GetProperty("lancamentosNovos").GetInt32());
        Assert.Equal(1L, await LancamentosAsync(c, await ContaAsync(c, Corrente.Id)));
        Assert.Equal(150_000, await SaldoAsync(c, await ContaAsync(c, Corrente.Id), "2026-11-05"));
    }

    [Fact]
    public async Task Banco_com_menos_historico_grava_a_data_mais_antiga()
    {
        await using var c = await IniciarAsync();
        var (item, _) = await NascerAsync(c, Corrente);
        c.Pluggy.CadastrarTransacao(Corrente.Id, Tx("tx-1", "2026-03-15", -10_000));
        c.Pluggy.CadastrarTransacao(Corrente.Id, Tx("tx-2", "2026-06-01", -20_000));

        await SincronizarOkAsync(c, item);

        var conta = await ContaAsync(c, Corrente.Id);
        Assert.Equal("2026-03-15", await EscalarAsync<string>(c.Banco,
            $"SELECT historico_desde::text FROM integracoes.vinculos WHERE id_externo = '{Corrente.Id}'"));
        Assert.Equal("180000|2026-03-15", await EscalarAsync<string>(c.Banco,
            $"SELECT saldo_inicial_centavos || '|' || saldo_inicial_em FROM contas.contas WHERE id = '{conta}'"));
        Assert.Equal("2026-03-15", (await ContaNaTelaAsync(c, item, conta)).GetProperty("historicoDesde").GetString());
    }

    [Fact]
    public async Task Diferenca_so_aparece_na_segunda_sincronizacao_seguida()
    {
        await using var c = await IniciarAsync();
        var (item, itemPluggy) = await NascerAsync(c, Corrente);
        c.Pluggy.CadastrarTransacao(Corrente.Id, Tx("tx-mercado", "2026-11-03", -30_000));
        await SincronizarOkAsync(c, item);
        var conta = await ContaAsync(c, Corrente.Id);

        // O banco passa a ter R$ 10,00 a mais, sem transação que explique.
        c.Pluggy.AtualizarConta(itemPluggy, Corrente with { SaldoCentavos = 151_000 });
        await SincronizarOkAsync(c, item);
        Assert.Equal(1, await EscalarAsync<short>(c.Banco, $"SELECT diferencas_seguidas FROM integracoes.vinculos WHERE id_externo = '{Corrente.Id}'"));
        Assert.Equal(JsonValueKind.Null, (await ContaNaTelaAsync(c, item, conta)).GetProperty("diferencaCentavos").ValueKind);

        await SincronizarOkAsync(c, item);
        Assert.Equal(1_000, (await ContaNaTelaAsync(c, item, conta)).GetProperty("diferencaCentavos").GetInt64());
        // Nada é ajustado.
        Assert.Equal(150_000, await SaldoAsync(c, conta, "2026-11-05"));

        c.Pluggy.AtualizarConta(itemPluggy, Corrente);
        await SincronizarOkAsync(c, item);
        Assert.Equal(0, await EscalarAsync<short>(c.Banco, $"SELECT diferencas_seguidas FROM integracoes.vinculos WHERE id_externo = '{Corrente.Id}'"));
        Assert.Equal(JsonValueKind.Null, (await ContaNaTelaAsync(c, item, conta)).GetProperty("diferencaCentavos").ValueKind);
    }

    [Fact]
    public async Task Falha_num_vinculo_nao_desfaz_o_outro()
    {
        await using var c = await IniciarAsync();
        var (item, _) = await NascerAsync(c, Corrente, Cartao);
        c.Pluggy.CadastrarTransacao(Corrente.Id, Tx("tx-mercado", "2026-11-03", -30_000));
        c.Pluggy.CadastrarTransacao(Cartao.Id, Tx("tx-cartao", "2026-11-02", 5_000));
        c.Pluggy.ContasComFalha[Cartao.Id] = true;

        var resumo = await SincronizarOkAsync(c, item);

        Assert.Equal(1, resumo.GetProperty("contasSincronizadas").GetInt32());
        Assert.Equal(1, resumo.GetProperty("contasComFalha").GetInt32());
        Assert.Equal(1L, await LancamentosAsync(c, await ContaAsync(c, Corrente.Id)));
        Assert.Equal(0L, await LancamentosAsync(c, await ContaAsync(c, Cartao.Id)));
        Assert.True(await EscalarAsync<bool>(c.Banco, $"SELECT importado_ate IS NULL FROM integracoes.vinculos WHERE id_externo = '{Cartao.Id}'"));
        Assert.True(await EscalarAsync<bool>(c.Banco,
            $"SELECT ultima_sincronizacao_em IS NULL AND ultima_tentativa_em IS NOT NULL FROM integracoes.items WHERE id = '{item}'"));

        // Volta: a próxima completa o cartão, sem duplicar a corrente.
        c.Pluggy.ContasComFalha.Clear();
        await SincronizarOkAsync(c, item);
        Assert.Equal(1L, await LancamentosAsync(c, await ContaAsync(c, Cartao.Id)));
        Assert.Equal(1L, await LancamentosAsync(c, await ContaAsync(c, Corrente.Id)));
    }

    [Fact]
    public async Task Sincronizacao_seguinte_usa_a_janela_de_10_dias()
    {
        await using var c = await IniciarAsync();
        var (item, _) = await NascerAsync(c, Corrente);
        await SincronizarOkAsync(c, item);
        c.Relogio.Agora = c.Relogio.Agora.AddDays(2);

        await SincronizarOkAsync(c, item);

        Assert.Equal([new DateOnly(2026, 1, 1), new DateOnly(2026, 10, 26)], c.Pluggy.LeiturasDeTransacoes.Select(l => l.Desde));
        Assert.Equal("2026-11-07", await EscalarAsync<string>(c.Banco,
            $"SELECT importado_ate::text FROM integracoes.vinculos WHERE id_externo = '{Corrente.Id}'"));
    }

    [Fact]
    public async Task Conta_arquivada_nao_sincroniza_e_mantem_o_historico()
    {
        await using var c = await IniciarAsync();
        var (item, _) = await NascerAsync(c, Corrente);
        c.Pluggy.CadastrarTransacao(Corrente.Id, Tx("tx-1", "2026-11-03", -30_000));
        await SincronizarOkAsync(c, item);
        var conta = await ContaAsync(c, Corrente.Id);
        await EscalarAsync<int>(c.Banco, $"UPDATE contas.contas SET arquivada = true WHERE id = '{conta}' RETURNING 1");
        c.Pluggy.RemoverTransacao(Corrente.Id, "tx-1");
        c.Pluggy.CadastrarTransacao(Corrente.Id, Tx("tx-2", "2026-11-20", -10_000));
        c.Relogio.Agora = c.Relogio.Agora.AddDays(20);
        var ana = await c.Api.ClienteLogadoAsync(c.AnaId); // a sessão de 20 dias atrás venceu

        var resumo = await SincronizarOkAsync(c, item, ana);

        Assert.Equal(0, resumo.GetProperty("contasSincronizadas").GetInt32());
        Assert.Single(c.Pluggy.LeiturasDeTransacoes);
        Assert.Equal(1L, await LancamentosAsync(c, conta));
        Assert.Equal("2026-11-05", await EscalarAsync<string>(c.Banco,
            $"SELECT importado_ate::text FROM integracoes.vinculos WHERE id_externo = '{Corrente.Id}'"));
    }

    [Fact]
    public async Task Desarquivar_importa_o_periodo_que_ficou_de_fora()
    {
        await using var c = await IniciarAsync();
        var (item, _) = await NascerAsync(c, Corrente);
        c.Pluggy.CadastrarTransacao(Corrente.Id, Tx("tx-janeiro", "2026-01-05", -1_000));
        c.Pluggy.CadastrarTransacao(Corrente.Id, Tx("tx-1", "2026-11-03", -30_000));
        await SincronizarOkAsync(c, item);
        var conta = await ContaAsync(c, Corrente.Id);
        await EscalarAsync<int>(c.Banco, $"UPDATE contas.contas SET arquivada = true WHERE id = '{conta}' RETURNING 1");
        c.Relogio.Agora = c.Relogio.Agora.AddDays(30);
        var ana = await c.Api.ClienteLogadoAsync(c.AnaId); // a sessão de 30 dias atrás venceu
        c.Pluggy.CadastrarTransacao(Corrente.Id, Tx("tx-no-arquivo", "2026-11-20", -10_000));
        await SincronizarOkAsync(c, item, ana);
        await EscalarAsync<int>(c.Banco, $"UPDATE contas.contas SET arquivada = false WHERE id = '{conta}' RETURNING 1");

        await SincronizarOkAsync(c, item, ana);

        Assert.Equal(new DateOnly(2026, 10, 26), c.Pluggy.LeiturasDeTransacoes.Last().Desde);
        Assert.Equal(3L, await LancamentosAsync(c, conta));
        Assert.Equal("2026-12-05", await EscalarAsync<string>(c.Banco,
            $"SELECT importado_ate::text FROM integracoes.vinculos WHERE id_externo = '{Corrente.Id}'"));
    }

    [Fact]
    public async Task Fatura_do_banco_diferente_so_aparece_na_segunda_sincronizacao_e_nao_muda_a_fatura()
    {
        await using var c = await IniciarAsync();
        var (item, _) = await NascerAsync(c, Cartao);
        c.Pluggy.CadastrarFatura(Cartao.Id, new FaturaPluggy("fatura-sintetica-abr", new DateOnly(2026, 4, 10), new DateOnly(2026, 4, 3), 50_000));
        c.Pluggy.CadastrarTransacao(Cartao.Id, Tx("tx-1", "2026-03-20", 30_000, fatura: "fatura-sintetica-abr"));
        c.Pluggy.CadastrarTransacao(Cartao.Id, Tx("tx-2", "2026-03-25", 10_000, fatura: "fatura-sintetica-abr"));
        c.Pluggy.CadastrarTransacao(Cartao.Id, Tx("tx-janeiro", "2026-01-15", 1_000));
        // Pendente, sem billId: fora da conferência; entra "a confirmar".
        c.Pluggy.CadastrarTransacao(Cartao.Id, Tx("tx-pendente", "2026-11-04", 2_000, status: "PENDING"));
        var conta = await ContaAsync(c, Cartao.Id);

        await SincronizarOkAsync(c, item);
        Assert.Empty((await ContaNaTelaAsync(c, item, conta)).GetProperty("faturasDivergentes").EnumerateArray());

        await SincronizarOkAsync(c, item);
        var fatura = (await ContaNaTelaAsync(c, item, conta)).GetProperty("faturasDivergentes").EnumerateArray().Single();
        Assert.Equal("2026-04-10", fatura.GetProperty("vencimento").GetString());
        Assert.Equal(10_000, fatura.GetProperty("diferencaCentavos").GetInt64());
        // A fatura do sistema continua a do calendário (fecha 3, vence 10) e nada é ajustado.
        Assert.Equal(2L, await EscalarAsync<long>(c.Banco,
            $"SELECT count(*) FROM lancamentos.transacoes WHERE conta_id = '{conta}' AND fatura_vencimento = '2026-04-10' AND NOT is_deleted"));
        Assert.Equal(4L, await LancamentosAsync(c, conta));
        Assert.Equal("A_CONFIRMAR", await EscalarAsync<string>(c.Banco,
            "SELECT confirmacao FROM lancamentos.transacoes WHERE id_externo = 'tx-pendente'"));
        // Cartão não tem conferência de saldo (decisão 12).
        Assert.Equal(JsonValueKind.Null, (await ContaNaTelaAsync(c, item, conta)).GetProperty("diferencaCentavos").ValueKind);
    }

    [Fact]
    public async Task Cartao_com_fatura_de_dezembro_paga_em_janeiro_nao_fica_positivo()
    {
        await using var c = await IniciarAsync();
        var (item, _) = await NascerAsync(c, Cartao);
        c.Pluggy.CadastrarFatura(Cartao.Id, new FaturaPluggy("fatura-sintetica-dez", new DateOnly(2026, 1, 5), new DateOnly(2025, 12, 28), 80_000));
        c.Pluggy.CadastrarTransacao(Cartao.Id, Tx("tx-pagamento", "2026-01-05", -80_000, "CREDIT", descricao: "PAGAMENTO EFETUADO"));

        await SincronizarOkAsync(c, item);

        var conta = await ContaAsync(c, Cartao.Id);
        Assert.Equal("-80000|2026-01-01", await EscalarAsync<string>(c.Banco,
            $"SELECT saldo_inicial_centavos || '|' || saldo_inicial_em FROM contas.contas WHERE id = '{conta}'"));
        Assert.Equal(-80_000, await SaldoAsync(c, conta, "2026-01-04"));
        Assert.Equal(0, await SaldoAsync(c, conta, "2026-01-06"));
    }

    [Fact]
    public async Task Cartao_sem_fatura_anterior_nasce_com_saldo_inicial_zero_e_sem_conferencia_de_saldo()
    {
        await using var c = await IniciarAsync();
        var (item, _) = await NascerAsync(c, Cartao);
        c.Pluggy.CadastrarTransacao(Cartao.Id, Tx("tx-1", "2026-01-20", 12_345));

        await SincronizarOkAsync(c, item);
        await SincronizarOkAsync(c, item);
        await SincronizarOkAsync(c, item);

        var conta = await ContaAsync(c, Cartao.Id);
        Assert.Equal("0|2026-01-01", await EscalarAsync<string>(c.Banco,
            $"SELECT saldo_inicial_centavos || '|' || saldo_inicial_em FROM contas.contas WHERE id = '{conta}'"));
        Assert.Equal(-12_345, await SaldoAsync(c, conta, "2026-11-05"));
        Assert.True(await EscalarAsync<bool>(c.Banco,
            $"SELECT diferenca_centavos IS NULL AND diferencas_seguidas = 0 FROM integracoes.vinculos WHERE id_externo = '{Cartao.Id}'"));
    }

    [Fact]
    public async Task Aplicacao_nova_espera_o_grupo()
    {
        await using var c = await IniciarAsync();
        var (item, itemPluggy) = await NascerAsync(c, Corrente);
        var contas = await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM contas.contas");
        c.Pluggy.CadastrarInvestimento(itemPluggy, new InvestimentoPluggy("cdb-sintetico-novo", "FIXED_INCOME", "CDB", "CDB Sintético", 1_000_000));

        await SincronizarOkAsync(c, item);
        await SincronizarOkAsync(c, item);

        Assert.Equal(1L, await EscalarAsync<long>(c.Banco,
            "SELECT count(*) FROM integracoes.vinculos WHERE id_externo = 'cdb-sintetico-novo' AND tipo = 'INVESTIMENTO' AND conta_id IS NULL AND NOT is_deleted"));
        Assert.Equal(contas, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM contas.contas"));
        var descoberta = await JsonAsync(await c.Ana.GetAsync($"/api/v1/integracoes/items/{item}/descoberta"));
        Assert.Equal("cdb-sintetico-novo", descoberta.GetProperty("aplicacoes").EnumerateArray().Single().GetProperty("idExterno").GetString());
    }

    /// <summary>
    /// D18 (E3, nota H) e D19 (item 3c): duas abas confirmando juntas podem deixar uma conta ligada que nenhum vínculo aponta.
    /// A sincronização a reconcilia: sem lançamentos, é arquivada; com lançamentos digitados, volta a ser manual (o casal
    /// decide o que fazer com eles). A conta ligada de verdade e a ligada já arquivada ficam como estão.
    /// </summary>
    [Fact]
    public async Task Conta_ligada_orfa_e_reconciliada()
    {
        await using var c = await IniciarAsync();
        var (item, _) = await NascerAsync(c, Corrente);
        var casal = await EscalarAsync<Guid>(c.Banco, $"SELECT casal_id FROM integracoes.items WHERE id = '{item}'");
        var vazia = Guid.CreateVersion7();
        var arquivada = Guid.CreateVersion7();
        foreach (var (id, apelido, arq) in new[] { (vazia, "Itaú Corrente · Ana (2)", false), (arquivada, "Antiga do banco", true) })
            await EscalarAsync<int>(c.Banco,
                "INSERT INTO contas.contas (id, casal_id, tipo, apelido, saldo_inicial_centavos, saldo_inicial_em, arquivada, ligada_em) " +
                $"VALUES ('{id}', '{casal}', 'CORRENTE', '{apelido}', 0, '2026-01-01', {arq}, now()) RETURNING 1");
        // Com lançamento digitado: manual com um lançamento, depois ligada (LIGAR pela metade) sem vínculo.
        var r = await c.Ana.PostAsJsonAsync("/api/v1/contas", new
        {
            tipo = "CORRENTE", apelido = "Carteira da Ana", saldoInicialCentavos = 0L, saldoInicialEm = "2026-01-01",
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var comLancamento = Guid.Parse(r.Headers.Location!.ToString().Split('/').Last());
        var categorias = await JsonAsync(await c.Ana.GetAsync("/api/v1/categorias"));
        var mercado = categorias.EnumerateArray().First(x => x.GetProperty("nome").GetString() == "Mercado").GetProperty("id").GetGuid();
        await EsperarAsync(c.Ana.PostAsJsonAsync("/api/v1/lancamentos", new
        {
            chaveIdempotente = Guid.NewGuid(), contaId = comLancamento, natureza = "DESPESA", valorCentavos = 2_500L, data = "2026-11-04",
            descricao = "Padaria", categoriaId = mercado,
        }), HttpStatusCode.Created);
        await EscalarAsync<int>(c.Banco, $"UPDATE contas.contas SET ligada_em = now() WHERE id = '{comLancamento}' RETURNING 1");

        await SincronizarOkAsync(c, item);

        Assert.Equal("false|true|false", await EstadoDaContaAsync(c, vazia));
        Assert.Equal("false|false|false", await EstadoDaContaAsync(c, comLancamento));
        Assert.Equal("true|true|false", await EstadoDaContaAsync(c, arquivada));
        Assert.Equal("true|false|false", await EstadoDaContaAsync(c, await ContaAsync(c, Corrente.Id)));
        // Volta a ser manual: recebe lançamento à mão de novo.
        await EsperarAsync(c.Ana.PostAsJsonAsync("/api/v1/lancamentos", new
        {
            chaveIdempotente = Guid.NewGuid(), contaId = comLancamento, natureza = "DESPESA", valorCentavos = 1_000L, data = "2026-11-04",
            descricao = "Feira", categoriaId = mercado,
        }), HttpStatusCode.Created);
    }

    /// <summary>"ligada|arquivada|apagada" da conta.</summary>
    private static Task<string> EstadoDaContaAsync(Ctx c, Guid conta) => EscalarAsync<string>(c.Banco,
        $"SELECT (ligada_em IS NOT NULL) || '|' || arquivada || '|' || is_deleted FROM contas.contas WHERE id = '{conta}'");

    [Fact]
    public async Task Conta_nao_importada_nao_sincroniza()
    {
        await using var c = await IniciarAsync();
        var itemPluggy = $"item-sintetico-{Guid.NewGuid():N}";
        c.Pluggy.Cadastrar(IdSlot1, itemPluggy);
        c.Pluggy.CadastrarConta(itemPluggy, Cartao);
        var r = await c.Ana.PostAsJsonAsync("/api/v1/integracoes/conexoes", new { slot = 1, itemId = itemPluggy, codigoBanco = "341" });
        var item = (await JsonAsync(r)).GetProperty("items")[0].GetProperty("id").GetGuid();
        await EsperarAsync(c.Ana.PostAsJsonAsync($"/api/v1/integracoes/items/{item}/nascimento",
            new { contas = new[] { new { idExterno = Cartao.Id, acao = "NAO_IMPORTAR" } }, aplicacoes = Array.Empty<object>() }), HttpStatusCode.NoContent);
        c.Pluggy.CadastrarTransacao(Cartao.Id, Tx("tx-1", "2026-11-03", 5_000));

        var resumo = await SincronizarOkAsync(c, item);

        Assert.Equal(0, resumo.GetProperty("contasSincronizadas").GetInt32());
        Assert.Empty(c.Pluggy.LeiturasDeTransacoes);
        Assert.Equal(0L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM lancamentos.transacoes"));
    }

    [Fact]
    public async Task Sincronizacao_nao_vaza_id_valor_nem_descricao_do_banco()
    {
        var logs = new LogsCapturados();
        await using var c = await IniciarAsync(s => s.AddSingleton<ILoggerProvider>(logs));
        var (item, itemPluggy) = await NascerAsync(c, Corrente, Cartao);
        c.Pluggy.CadastrarTransacao(Corrente.Id, Tx("tx-sintetica-vazamento", "2026-11-03", -31_337, descricao: "FARMACIA ZULEICA SINTETICA"));
        c.Pluggy.CadastrarFatura(Cartao.Id, new FaturaPluggy("fatura-sintetica-vazamento", new DateOnly(2026, 4, 10), new DateOnly(2026, 4, 3), 50_000));
        c.Pluggy.ContasComFalha[Cartao.Id] = true;
        c.Pluggy.CadastrarInvestimento(itemPluggy, new InvestimentoPluggy("cdb-sintetico-vazamento", "FIXED_INCOME", "CDB", "CDB", 1_000));

        var r = await SincronizarAsync(c.Ana, item);
        var corpo = await r.Content.ReadAsStringAsync();
        var tela = await (await c.Ana.GetAsync("/api/v1/integracoes/conexoes")).Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains(logs.Linhas, l => l.Contains("falhou", StringComparison.Ordinal));
        string[] proibidos = [Corrente.Id, Cartao.Id, itemPluggy, "tx-sintetica-vazamento", "fatura-sintetica-vazamento", "cdb-sintetico-vazamento",
            "ZULEICA", "31337", "313,37", "-31337"];
        foreach (var texto in logs.Linhas.Append(corpo).Append(tela))
            foreach (var p in proibidos)
                Assert.DoesNotContain(p, texto, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Outro_conjuge_recebe_403()
    {
        await using var c = await IniciarAsync();
        var (item, _) = await NascerAsync(c, Corrente);

        var r = await SincronizarAsync(c.Bruno, item);

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Empty(c.Pluggy.LeiturasDeTransacoes);
    }

    [Fact]
    public async Task Pluggy_fora_do_ar_da_503_e_registra_so_a_tentativa()
    {
        await using var c = await IniciarAsync();
        var (item, _) = await NascerAsync(c, Corrente);
        c.Pluggy.Falha = new HttpRequestException("rede sintética fora");

        var r = await SincronizarAsync(c.Ana, item);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.True(await EscalarAsync<bool>(c.Banco,
            $"SELECT ultima_sincronizacao_em IS NULL AND ultima_tentativa_em IS NOT NULL FROM integracoes.items WHERE id = '{item}'"));
    }
    /// <summary>
    /// Revisão da E4 (achado 1): conta manual da Fase 1 ligada ao banco (LIGAR) com o histórico digitado. O banco traz o mesmo
    /// histórico; a diferença com o banco aponta a duplicidade e o casal apaga os manuais (são dele). Depois disso, fecha.
    /// </summary>
    [Fact]
    public async Task Ligar_conta_com_historico_manual_deixa_o_casal_limpar_o_que_veio_em_dobro()
    {
        await using var c = await IniciarAsync();
        var r = await c.Ana.PostAsJsonAsync("/api/v1/contas", new
        {
            tipo = "CORRENTE", apelido = "Itaú da Fase 1", saldoInicialCentavos = 180_000L, saldoInicialEm = "2026-01-01",
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var conta = Guid.Parse(r.Headers.Location!.ToString().Split('/').Last());
        var categorias = await JsonAsync(await c.Ana.GetAsync("/api/v1/categorias"));
        var mercado = categorias.EnumerateArray().Single(k => k.GetProperty("nome").GetString() == "Mercado").GetProperty("id").GetGuid();
        var manual = await c.Ana.PostAsJsonAsync("/api/v1/lancamentos", new
        {
            chaveIdempotente = Guid.NewGuid(), contaId = conta, natureza = "DESPESA", valorCentavos = 30_000L, data = "2026-11-03",
            descricao = "Mercado", categoriaId = mercado,
        });
        Assert.Equal(HttpStatusCode.Created, manual.StatusCode);
        var idManual = (await JsonAsync(manual)).GetProperty("id").GetGuid();

        var itemPluggy = $"item-sintetico-{Guid.NewGuid():N}";
        c.Pluggy.Cadastrar(IdSlot1, itemPluggy);
        c.Pluggy.CadastrarConta(itemPluggy, Corrente);
        c.Pluggy.CadastrarTransacao(Corrente.Id, Tx("tx-janeiro", "2026-01-05", -1_000));
        c.Pluggy.CadastrarTransacao(Corrente.Id, Tx("tx-mercado", "2026-11-03", -30_000));
        var rc = await c.Ana.PostAsJsonAsync("/api/v1/integracoes/conexoes", new { slot = 1, itemId = itemPluggy, codigoBanco = "341" });
        var item = (await JsonAsync(rc)).GetProperty("items")[0].GetProperty("id").GetGuid();
        await EsperarAsync(c.Ana.PostAsJsonAsync($"/api/v1/integracoes/items/{item}/nascimento", new
        {
            contas = new[] { new { idExterno = Corrente.Id, acao = "LIGAR", contaExistenteId = conta } }, aplicacoes = Array.Empty<object>(),
        }), HttpStatusCode.NoContent);

        await SincronizarOkAsync(c, item);
        await SincronizarOkAsync(c, item);
        // Em dobro: o banco tem 1.500,00 e o sistema, 1.500,00 − 300,00 do manual.
        Assert.Equal(30_000, (await ContaNaTelaAsync(c, item, conta)).GetProperty("diferencaCentavos").GetInt64());

        await EsperarAsync(c.Ana.DeleteAsync($"/api/v1/lancamentos/{idManual}"), HttpStatusCode.NoContent);
        await SincronizarOkAsync(c, item);

        Assert.Equal(150_000, await SaldoAsync(c, conta, "2026-11-05"));
        Assert.Equal(JsonValueKind.Null, (await ContaNaTelaAsync(c, item, conta)).GetProperty("diferencaCentavos").ValueKind);
    }

    [Fact]
    public async Task Reconciliacao_nao_desliga_conta_do_conjuge_de_outro_casal_nem_do_nascimento_pela_metade()
    {
        await using var c = await IniciarAsync();
        var (item, _) = await NascerAsync(c, Corrente);
        var casal = await EscalarAsync<Guid>(c.Banco, $"SELECT casal_id FROM integracoes.items WHERE id = '{item}'");

        // Conta do Bruno, por um item da conexão dele.
        var pluggyDoBruno = $"item-sintetico-{Guid.NewGuid():N}";
        var contaDoBruno = Corrente with { Id = "conta-sintetica-do-bruno", AgenciaEConta = "0002/54321-0" };
        c.Pluggy.Cadastrar(IdSlot2, pluggyDoBruno);
        c.Pluggy.CadastrarConta(pluggyDoBruno, contaDoBruno);
        var rb = await c.Bruno.PostAsJsonAsync("/api/v1/integracoes/conexoes", new { slot = 2, itemId = pluggyDoBruno, codigoBanco = "341" });
        Assert.Equal(HttpStatusCode.Created, rb.StatusCode);
        var itemDoBruno = (await JsonAsync(rb)).GetProperty("items")[0].GetProperty("id").GetGuid();
        var bruno = await EscalarAsync<Guid>(c.Banco, "SELECT id FROM identidade.usuarios WHERE nome_completo = 'Bruno Teste'");
        await EsperarAsync(c.Bruno.PostAsJsonAsync($"/api/v1/integracoes/items/{itemDoBruno}/nascimento", new
        {
            contas = new[] { new { idExterno = contaDoBruno.Id, acao = "CRIAR", apelido = "Itaú Corrente · Bruno", titularUsuarioId = bruno } },
            aplicacoes = Array.Empty<object>(),
        }), HttpStatusCode.NoContent);

        // Conta ligada de outro casal, sem vínculo nenhum.
        var outroCasal = Guid.NewGuid();
        var contaAlheia = Guid.NewGuid();
        await EscalarAsync<int>(c.Banco, $"INSERT INTO identidade.casais (id, nome_familia) VALUES ('{outroCasal}', 'Outra Família') RETURNING 1");
        await EscalarAsync<int>(c.Banco,
            "INSERT INTO contas.contas (id, casal_id, tipo, apelido, saldo_inicial_centavos, saldo_inicial_em, ligada_em) " +
            $"VALUES ('{contaAlheia}', '{outroCasal}', 'CORRENTE', 'Alheia', 0, '2026-01-01', now()) RETURNING 1");

        // Nascimento pela metade: o vínculo já aponta para um id que ainda não é conta.
        var aindaNaoNasceu = Guid.CreateVersion7();
        c.Pluggy.CadastrarConta(pluggyDoBruno, Cartao);
        await EscalarAsync<int>(c.Banco,
            "INSERT INTO integracoes.vinculos (id, casal_id, item_id, tipo, id_externo, conta_id) " +
            $"VALUES ('{Guid.CreateVersion7()}', '{casal}', '{itemDoBruno}', 'CARTAO', '{Cartao.Id}', '{aindaNaoNasceu}') RETURNING 1");

        await SincronizarOkAsync(c, item);

        Assert.Equal(2L, await EscalarAsync<long>(c.Banco, $"SELECT count(*) FROM contas.contas WHERE casal_id = '{casal}' AND ligada_em IS NOT NULL"));
        Assert.True(await EscalarAsync<bool>(c.Banco, $"SELECT ligada_em IS NOT NULL FROM contas.contas WHERE id = '{contaAlheia}'"));
        Assert.Equal(aindaNaoNasceu, await EscalarAsync<Guid>(c.Banco, $"SELECT conta_id FROM integracoes.vinculos WHERE id_externo = '{Cartao.Id}'"));
    }

    [Fact]
    public async Task Sincronizacoes_simultaneas_do_mesmo_item_nao_duplicam_nem_perdem_a_conferencia()
    {
        await using var c = await IniciarAsync();
        var (item, itemPluggy) = await NascerAsync(c, Corrente);
        c.Pluggy.CadastrarTransacao(Corrente.Id, Tx("tx-janeiro", "2026-01-05", -1_000));
        c.Pluggy.CadastrarTransacao(Corrente.Id, Tx("tx-mercado", "2026-11-03", -30_000));

        // Leitura lenta: sem a trava, as duas se sobreporiam na Pluggy (revisão da E4, rodada 2, nota 5).
        c.Pluggy.AtrasoNasTransacoes = TimeSpan.FromMilliseconds(300);
        var primeiras = await Task.WhenAll(SincronizarAsync(c.Ana, item), SincronizarAsync(c.Ana, item));
        Assert.All(primeiras, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var conta = await ContaAsync(c, Corrente.Id);
        Assert.Equal(2L, await LancamentosAsync(c, conta));
        Assert.Equal(150_000, await SaldoAsync(c, conta, "2026-11-05"));

        c.Pluggy.AtualizarConta(itemPluggy, Corrente with { SaldoCentavos = 151_000 });
        var seguintes = await Task.WhenAll(SincronizarAsync(c.Ana, item), SincronizarAsync(c.Ana, item));
        Assert.All(seguintes, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(2, await EscalarAsync<short>(c.Banco, $"SELECT diferencas_seguidas FROM integracoes.vinculos WHERE id_externo = '{Corrente.Id}'"));
        Assert.Equal(4, c.Pluggy.LeiturasDeTransacoes.Count);
        Assert.Equal(1, c.Pluggy.MaximoDeLeiturasJuntas);
    }

    /// <summary>Revisão da E4 (achado 4): com o item ainda atualizando no banco, o que sumiu do lote não é apagado.</summary>
    [Fact]
    public async Task Item_que_nao_esta_atualizado_no_banco_nao_apaga_o_que_sumiu()
    {
        await using var c = await IniciarAsync();
        var (item, itemPluggy) = await NascerAsync(c, Corrente);
        c.Pluggy.CadastrarTransacao(Corrente.Id, Tx("tx-janeiro", "2026-01-05", -1_000));
        c.Pluggy.CadastrarTransacao(Corrente.Id, Tx("tx-mercado", "2026-11-03", -30_000));
        c.Pluggy.CadastrarTransacao(Corrente.Id, Tx("tx-padaria", "2026-11-04", -2_000));
        await SincronizarOkAsync(c, item);
        var conta = await ContaAsync(c, Corrente.Id);

        c.Pluggy.Cadastrar(IdSlot1, itemPluggy, status: "UPDATING");
        c.Pluggy.RemoverTransacao(Corrente.Id, "tx-padaria");
        await SincronizarOkAsync(c, item);
        Assert.Equal(3L, await LancamentosAsync(c, conta));

        // D19: com o item atualizado, só a segunda sincronização seguida sem a linha apaga.
        c.Pluggy.Cadastrar(IdSlot1, itemPluggy);
        await SincronizarOkAsync(c, item);
        Assert.Equal(3L, await LancamentosAsync(c, conta));
        await SincronizarOkAsync(c, item);
        Assert.Equal(2L, await LancamentosAsync(c, conta));

        // Apagada por ausência: se o banco manda de novo, ela volta (a mesma linha, sem duplicar).
        c.Pluggy.CadastrarTransacao(Corrente.Id, Tx("tx-padaria", "2026-11-04", -2_000));
        await SincronizarOkAsync(c, item);
        Assert.Equal(3L, await LancamentosAsync(c, conta));
        Assert.Equal(3L, await EscalarAsync<long>(c.Banco, $"SELECT count(*) FROM lancamentos.transacoes WHERE conta_id = '{conta}'"));
    }

    /// <summary>Revisão da E4 (achado 4): lote vazio com a janela cheia é resposta suspeita; nada é apagado.</summary>
    [Fact]
    public async Task Lote_vazio_com_a_janela_cheia_nao_apaga()
    {
        await using var c = await IniciarAsync();
        var (item, _) = await NascerAsync(c, Corrente);
        c.Pluggy.CadastrarTransacao(Corrente.Id, Tx("tx-janeiro", "2026-01-05", -1_000));
        c.Pluggy.CadastrarTransacao(Corrente.Id, Tx("tx-mercado", "2026-11-03", -30_000));
        await SincronizarOkAsync(c, item);
        var conta = await ContaAsync(c, Corrente.Id);

        c.Pluggy.RemoverTransacao(Corrente.Id, "tx-mercado");
        c.Pluggy.RemoverTransacao(Corrente.Id, "tx-janeiro");
        await SincronizarOkAsync(c, item);

        Assert.Equal(2L, await LancamentosAsync(c, conta));
    }
}
