using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GestorFinanceiro.Api.Infra;
using GestorFinanceiro.IntegrationTests.Infra;
using GestorFinanceiro.Modules.Contas.Contracts;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Contracts;
using GestorFinanceiro.SharedKernel.Aplicacao;
using GestorFinanceiro.UnitTests;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GestorFinanceiro.IntegrationTests;

/// <summary>
/// Fase 2, Tarefa 4.3: importação "criar ou atualizar pela chave natural" (OPEN_FINANCE, id da Pluggy). Os testes de risco
/// da etapa: importar de novo não duplica; categoria e descrição revisadas pelo casal ficam; linha apagada não volta;
/// pendente vira definitiva mantendo a categoria; falha no meio não deixa nada visível. Dados sintéticos; relógio em
/// 05/11/2026.
/// </summary>
[Collection("postgres")]
public class ImportacaoTests(PostgresFixture pg)
{
    private static readonly DateOnly Desde = new(2026, 1, 1);

    /// <summary>Dentro de uma requisição vale a sessão; fora dela (o escopo do teste, como o job), o casal fixo.</summary>
    private sealed class CasalFixo { public Guid? Id { get; set; } }

    private sealed class UsuarioDoTeste(IHttpContextAccessor acessor, CasalFixo fixo) : IUsuarioAtual
    {
        private IUsuarioAtual Atual => acessor.HttpContext is null ? new UsuarioSistema(fixo.Id) : new UsuarioAtualHttp(acessor);
        public Guid? UsuarioId => Atual.UsuarioId;
        public Guid? CasalId => Atual.CasalId;
        public string? Ip => Atual.Ip;
    }

    private sealed record Ctx(ApiDeTeste Api, BancoDeTeste Banco, HttpClient Cliente, Guid Corrente, Guid Cartao) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Api.DisposeAsync();
    }

    private async Task<Ctx> IniciarAsync()
    {
        var fixo = new CasalFixo();
        var banco = await pg.NovoBancoAsync();
        var api = new ApiDeTeste(banco, relogio: new RelogioFixo(), servicos: s =>
        {
            s.AddSingleton(fixo);
            s.AddScoped<IUsuarioAtual, UsuarioDoTeste>();
        });
        var pedido = new CadastroDoCasalDto(
            api.CodigoInstalacao, "Família Teste",
            new ConjugeNovoDto("Ana Teste", CpfSintetico.Gerar(1), "senha-da-ana-123"),
            new ConjugeNovoDto("Bruno Teste", CpfSintetico.Gerar(2), "senha-do-bruno-1"));
        Assert.Equal(HttpStatusCode.Created, (await api.CreateClient().PostAsJsonAsync("/api/v1/casal", pedido)).StatusCode);
        var ana = await EscalarAsync<Guid>(banco, "SELECT id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'");
        fixo.Id = await EscalarAsync<Guid>(banco, "SELECT casal_id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'");

        var corrente = Guid.CreateVersion7();
        var cartao = Guid.CreateVersion7();
        await using (var escopo = api.Services.CreateAsyncScope())
        {
            var ligadas = escopo.ServiceProvider.GetRequiredService<IContasLigadas>();
            Assert.IsType<ResultadoDaContaLigada.Pronta>(await ligadas.CriarAsync(
                new NovaContaLigada(corrente, "CORRENTE", "Itaú Corrente · Ana", "Itaú", ana, 0, Desde, null, null, null), CancellationToken.None));
            Assert.IsType<ResultadoDaContaLigada.Pronta>(await ligadas.CriarAsync(
                new NovaContaLigada(cartao, "CARTAO", "Itaú Cartão · Ana", "Itaú", ana, 0, Desde, 3, 10, 500_000), CancellationToken.None));
        }
        return new Ctx(api, banco, await api.ClienteLogadoAsync(ana), corrente, cartao);
    }

    private static async Task<T> EscalarAsync<T>(BancoDeTeste banco, string sql)
    {
        await using var db = new NpgsqlConnection(banco.ConexaoSuperusuario);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, db);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<ResumoDaImportacao> ImportarAsync(Ctx c, Guid conta, params LancamentoImportado[] lote)
    {
        await using var escopo = c.Api.Services.CreateAsyncScope();
        return await escopo.ServiceProvider.GetRequiredService<IImportacaoDeLancamentos>().ImportarAsync(conta, Desde, lote, CancellationToken.None);
    }

    private static LancamentoImportado Linha(string id, long valor, string data = "2026-11-03", string? descricao = "PADARIA SINTETICA",
        bool aConfirmar = false, int? parcela = null, int? parcelas = null, string? categoria = "01010000", string? fatura = null) =>
        new(id, DateOnly.Parse(data, System.Globalization.CultureInfo.InvariantCulture), valor, descricao, aConfirmar, parcela, parcelas, categoria, fatura);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<Guid> CategoriaAsync(HttpClient cliente, string nome)
    {
        var lista = await JsonAsync(await cliente.GetAsync("/api/v1/categorias"));
        return lista.EnumerateArray().Single(c => c.GetProperty("nome").GetString() == nome).GetProperty("id").GetGuid();
    }

    private static Task<Guid> IdAsync(Ctx c, string idExterno) =>
        EscalarAsync<Guid>(c.Banco, $"SELECT id FROM lancamentos.transacoes WHERE origem = 'OPEN_FINANCE' AND id_externo = '{idExterno}'");

    private static async Task<JsonElement> LancamentoAsync(Ctx c, Guid id)
    {
        var r = await c.Cliente.GetAsync($"/api/v1/lancamentos/{id}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await JsonAsync(r);
    }

    /// <summary>O casal revisa: mesma data e valor, descrição e categoria dele.</summary>
    private static async Task RevisarAsync(Ctx c, Guid id, string descricao, Guid categoria, long valorAbsoluto, string data = "2026-11-03")
    {
        var r = await c.Cliente.PutAsJsonAsync($"/api/v1/lancamentos/{id}", new
        {
            natureza = "DESPESA", valorCentavos = valorAbsoluto, data, descricao, categoriaId = categoria,
        });
        Assert.True(r.StatusCode == HttpStatusCode.NoContent, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
    }

    private static Task<long> AtivasAsync(Ctx c) =>
        EscalarAsync<long>(c.Banco, "SELECT count(*) FROM lancamentos.transacoes WHERE NOT is_deleted");

    [Fact]
    public async Task Importar_cria_a_revisar_com_origem_OPEN_FINANCE()
    {
        await using var c = await IniciarAsync();

        var resumo = await ImportarAsync(c, c.Corrente, Linha("tx-sintetica-1", -2_550), Linha("tx-sintetica-2", 900_000, descricao: "SALARIO SINTETICO"));

        Assert.Equal(new ResumoDaImportacao(2, 0, 0, 0, 0, 0), resumo);
        Assert.Equal(2L, await EscalarAsync<long>(c.Banco,
            "SELECT count(*) FROM lancamentos.transacoes WHERE origem = 'OPEN_FINANCE' AND revisao = 'A_REVISAR' AND confirmacao = 'CONFIRMADA' " +
            "AND categoria_id IS NULL AND autor_usuario_id IS NULL AND categoria_externa = '01010000'"));
        var despesa = await LancamentoAsync(c, await IdAsync(c, "tx-sintetica-1"));
        Assert.Equal("DESPESA", despesa.GetProperty("natureza").GetString());
        Assert.Equal(-2_550, despesa.GetProperty("valorCentavos").GetInt64());
        Assert.Equal("PADARIA SINTETICA", despesa.GetProperty("descricao").GetString());
        Assert.Equal("2026-11-03", despesa.GetProperty("data").GetString());
        Assert.Equal("RECEITA", (await LancamentoAsync(c, await IdAsync(c, "tx-sintetica-2"))).GetProperty("natureza").GetString());
    }

    [Fact]
    public async Task Importar_de_novo_nao_duplica()
    {
        await using var c = await IniciarAsync();
        var lote = new[] { Linha("tx-sintetica-1", -2_550), Linha("tx-sintetica-2", -1_000) };
        await ImportarAsync(c, c.Corrente, lote);
        var eventos = await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM audit.eventos WHERE tabela = 'lancamentos.transacoes'");

        var resumo = await ImportarAsync(c, c.Corrente, lote);

        Assert.Equal(new ResumoDaImportacao(0, 0, 0, 0, 0, 0), resumo);
        Assert.Equal(2L, await AtivasAsync(c));
        // Nada mudou: nenhuma linha regravada.
        Assert.Equal(eventos, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM audit.eventos WHERE tabela = 'lancamentos.transacoes'"));
    }

    [Fact]
    public async Task Reimportar_atualiza_valor_e_data()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Corrente, Linha("tx-sintetica-1", -2_550));

        var resumo = await ImportarAsync(c, c.Corrente, Linha("tx-sintetica-1", -2_600, data: "2026-11-04", descricao: "PADARIA SINTETICA LTDA"));

        Assert.Equal(new ResumoDaImportacao(0, 1, 0, 0, 0, 0), resumo);
        var l = await LancamentoAsync(c, await IdAsync(c, "tx-sintetica-1"));
        Assert.Equal(-2_600, l.GetProperty("valorCentavos").GetInt64());
        Assert.Equal("2026-11-04", l.GetProperty("data").GetString());
        // Ainda "a revisar": a descrição do banco acompanha.
        Assert.Equal("PADARIA SINTETICA LTDA", l.GetProperty("descricao").GetString());
    }

    [Fact]
    public async Task Categoria_revisada_pelo_casal_e_preservada()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Corrente, Linha("tx-sintetica-1", -2_550));
        var id = await IdAsync(c, "tx-sintetica-1");
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        await RevisarAsync(c, id, "PADARIA SINTETICA", mercado, 2_550);

        await ImportarAsync(c, c.Corrente, Linha("tx-sintetica-1", -2_550, categoria: "02000000"));

        Assert.Equal(mercado, (await LancamentoAsync(c, id)).GetProperty("categoria").GetProperty("id").GetGuid());
        Assert.Equal("REVISADA", await EscalarAsync<string>(c.Banco, $"SELECT revisao FROM lancamentos.transacoes WHERE id = '{id}'"));
    }

    [Fact]
    public async Task Descricao_revisada_pelo_casal_e_preservada()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Corrente, Linha("tx-sintetica-1", -2_550));
        var id = await IdAsync(c, "tx-sintetica-1");
        await RevisarAsync(c, id, "Pão da semana", await CategoriaAsync(c.Cliente, "Mercado"), 2_550);

        var resumo = await ImportarAsync(c, c.Corrente, Linha("tx-sintetica-1", -2_550, data: "2026-11-04", descricao: "OUTRA DESCRICAO DO BANCO"));

        Assert.Equal(1, resumo.Atualizados);
        var l = await LancamentoAsync(c, id);
        Assert.Equal("Pão da semana", l.GetProperty("descricao").GetString());
        Assert.Equal("2026-11-04", l.GetProperty("data").GetString());
    }

    [Fact]
    public async Task Apagada_pelo_banco_vira_soft_delete_com_auditoria()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Corrente, Linha("tx-sintetica-1", -2_550), Linha("tx-sintetica-2", -1_000));
        var id = await IdAsync(c, "tx-sintetica-2");

        var resumo = await ImportarAsync(c, c.Corrente, Linha("tx-sintetica-1", -2_550));

        Assert.Equal(new ResumoDaImportacao(0, 0, 0, 1, 0, 0), resumo);
        Assert.True(await EscalarAsync<bool>(c.Banco, $"SELECT is_deleted FROM lancamentos.transacoes WHERE id = '{id}'"));
        Assert.Equal(1L, await EscalarAsync<long>(c.Banco,
            $"SELECT count(*) FROM audit.eventos WHERE tabela = 'lancamentos.transacoes' AND entidade_id = '{id}' AND acao = 'SOFT_DELETE'"));
    }

    [Fact]
    public async Task Linha_apagada_nao_e_recriada()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Corrente, Linha("tx-sintetica-1", -2_550), Linha("tx-sintetica-2", -1_000));
        await ImportarAsync(c, c.Corrente, Linha("tx-sintetica-1", -2_550));

        var resumo = await ImportarAsync(c, c.Corrente, Linha("tx-sintetica-1", -2_550), Linha("tx-sintetica-2", -1_000));

        Assert.Equal(new ResumoDaImportacao(0, 0, 1, 0, 0, 0), resumo);
        Assert.Equal(1L, await AtivasAsync(c));
        Assert.Equal(2L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM lancamentos.transacoes"));
    }

    [Fact]
    public async Task Linha_fora_da_janela_nao_e_apagada()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Corrente, Linha("tx-antiga", -2_550, data: "2026-10-01"), Linha("tx-recente", -1_000));

        await using (var escopo = c.Api.Services.CreateAsyncScope())
            await escopo.ServiceProvider.GetRequiredService<IImportacaoDeLancamentos>()
                .ImportarAsync(c.Corrente, new DateOnly(2026, 10, 26), [Linha("tx-recente", -1_000)], CancellationToken.None);

        Assert.Equal(2L, await AtivasAsync(c));
    }

    [Fact]
    public async Task Pendente_vira_definitiva_com_outro_id_mantendo_a_categoria()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Cartao, Linha("tx-pendente", -4_990, data: "2026-11-02", descricao: "LOJA SINTETICA*123", aConfirmar: true));
        var pendente = await IdAsync(c, "tx-pendente");
        var restaurantes = await CategoriaAsync(c.Cliente, "Restaurantes");
        await RevisarAsync(c, pendente, "Camiseta", restaurantes, 4_990, "2026-11-02");

        var resumo = await ImportarAsync(c, c.Cartao, Linha("tx-definitiva", -4_990, data: "2026-11-04", descricao: "LOJA SINTETICA"));

        Assert.Equal(new ResumoDaImportacao(1, 0, 0, 0, 1, 0), resumo);
        Assert.True(await EscalarAsync<bool>(c.Banco, $"SELECT is_deleted FROM lancamentos.transacoes WHERE id = '{pendente}'"));
        var definitiva = await LancamentoAsync(c, await IdAsync(c, "tx-definitiva"));
        Assert.Equal(restaurantes, definitiva.GetProperty("categoria").GetProperty("id").GetGuid());
        Assert.Equal("Camiseta", definitiva.GetProperty("descricao").GetString());
        Assert.Equal(-4_990, definitiva.GetProperty("valorCentavos").GetInt64());
        Assert.Equal("REVISADA|CONFIRMADA", await EscalarAsync<string>(c.Banco,
            "SELECT revisao || '|' || confirmacao FROM lancamentos.transacoes WHERE id_externo = 'tx-definitiva'"));
    }

    [Fact]
    public async Task Pendente_sem_par_unico_fica()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Cartao, Linha("tx-pendente", -4_990, data: "2026-11-02", descricao: "LOJA SINTETICA", aConfirmar: true));
        var pendente = await IdAsync(c, "tx-pendente");

        // Duas definitivas iguais (mesmo valor, datas perto, mesma descrição): não dá para escolher.
        var resumo = await ImportarAsync(c, c.Cartao,
            Linha("tx-definitiva-1", -4_990, data: "2026-11-03", descricao: "LOJA SINTETICA"),
            Linha("tx-definitiva-2", -4_990, data: "2026-11-04", descricao: "LOJA SINTETICA"));

        Assert.Equal(new ResumoDaImportacao(2, 0, 0, 0, 0, 1), resumo);
        Assert.False(await EscalarAsync<bool>(c.Banco, $"SELECT is_deleted FROM lancamentos.transacoes WHERE id = '{pendente}'"));
        Assert.Equal(3L, await AtivasAsync(c));
    }

    [Theory]
    [InlineData(4_991, "2026-11-04", "LOJA SINTETICA")]   // outro valor
    [InlineData(4_990, "2026-11-06", "LOJA SINTETICA")]   // 4 dias depois
    [InlineData(4_990, "2026-11-04", "OUTRA LOJA")]       // descrição diferente
    public async Task Pendente_sem_definitiva_parecida_fica(long valor, string data, string descricao)
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Cartao, Linha("tx-pendente", -4_990, data: "2026-11-02", descricao: "LOJA SINTÉTICA 01", aConfirmar: true));

        var resumo = await ImportarAsync(c, c.Cartao, Linha("tx-definitiva", -valor, data: data, descricao: descricao));

        Assert.Equal(1, resumo.PendentesSemPar);
        Assert.Equal(2L, await AtivasAsync(c));
    }

    [Fact]
    public async Task Parcela_futura_entra_no_mes_dela_e_fora_do_patrimonio_ate_a_data()
    {
        await using var c = await IniciarAsync();

        await ImportarAsync(c, c.Cartao,
            Linha("tx-parcela-3", -10_000, data: "2026-11-02", parcela: 3, parcelas: 36),
            Linha("tx-parcela-4", -10_000, data: "2026-12-02", parcela: 4, parcelas: 36));

        var dezembro = await JsonAsync(await c.Cliente.GetAsync("/api/v1/lancamentos?mes=2026-12"));
        var parcela = dezembro.GetProperty("lancamentos").EnumerateArray().Single();
        Assert.Equal(4, parcela.GetProperty("parcela").GetInt32());
        Assert.Equal(36, parcela.GetProperty("parcelas").GetInt32());
        // Fatura do calendário do sistema (fecha 3, vence 10): compra de 02/12 vence em 10/12.
        Assert.Equal("2026-12-10", parcela.GetProperty("faturaVencimento").GetString());
        var saldos = await JsonAsync(await c.Cliente.GetAsync("/api/v1/saldos?em=2026-11-05"));
        var cartao = saldos.EnumerateArray().Single(s => s.GetProperty("contaId").GetGuid() == c.Cartao);
        Assert.Equal(-10_000, cartao.GetProperty("saldoCentavos").GetInt64());
    }

    [Fact]
    public async Task Valor_zero_nao_entra_e_nao_derruba_o_lote()
    {
        await using var c = await IniciarAsync();

        var resumo = await ImportarAsync(c, c.Corrente, Linha("tx-sintetica-1", -2_550), Linha("tx-iof-estornado", 0), Linha("tx-sintetica-3", -100));

        Assert.Equal(new ResumoDaImportacao(2, 0, 1, 0, 0, 0), resumo);
        Assert.Equal(2L, await AtivasAsync(c));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Descricao_vazia_vira_sem_descricao(string? descricao)
    {
        await using var c = await IniciarAsync();

        await ImportarAsync(c, c.Corrente, Linha("tx-sintetica-1", -2_550, descricao: descricao));

        Assert.Equal("(sem descrição)", (await LancamentoAsync(c, await IdAsync(c, "tx-sintetica-1"))).GetProperty("descricao").GetString());
    }

    [Fact]
    public async Task Falha_no_meio_nao_deixa_nada_visivel()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Corrente, Linha("tx-ja-existia", -500, data: "2026-11-01"));

        await using var escopo = c.Api.Services.CreateAsyncScope();
        var importacao = escopo.ServiceProvider.GetRequiredService<IImportacaoDeLancamentos>();
        await Assert.ThrowsAsync<ArgumentException>(() => importacao.ImportarAsync(c.Corrente, Desde,
            [Linha("tx-sintetica-1", -2_550), Linha("tx-sintetica-2", -1_000), Linha("tx-invalida", -300, parcela: 5, parcelas: 3), Linha("tx-sintetica-4", -10)],
            CancellationToken.None));

        Assert.Equal(1L, await AtivasAsync(c));
        Assert.Equal(1L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM lancamentos.transacoes"));
        // O mesmo escopo (o job importa vínculo por vínculo) continua utilizável e não regrava o lote que falhou.
        var resumo = await importacao.ImportarAsync(c.Corrente, Desde, [Linha("tx-ja-existia", -500, data: "2026-11-01"), Linha("tx-outra", -700)],
            CancellationToken.None);
        Assert.Equal(new ResumoDaImportacao(1, 0, 0, 0, 0, 0), resumo);
        Assert.Equal(2L, await AtivasAsync(c));
    }

    [Fact]
    public async Task Conta_manual_nao_recebe_importacao()
    {
        await using var c = await IniciarAsync();
        var r = await c.Cliente.PostAsJsonAsync("/api/v1/contas", new
        {
            tipo = "CORRENTE", apelido = "Carteira manual", instituicao = "Itaú", saldoInicialCentavos = 0L, saldoInicialEm = "2026-01-01",
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var manual = Guid.Parse(r.Headers.Location!.ToString().Split('/').Last());

        await Assert.ThrowsAsync<InvalidOperationException>(() => ImportarAsync(c, manual, Linha("tx-sintetica-1", -2_550)));
        Assert.Equal(0L, await AtivasAsync(c));
    }
    [Fact]
    public async Task Chave_que_ja_esta_em_outra_conta_do_casal_nao_muda_de_conta()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Corrente, Linha("tx-sintetica-1", -2_550));

        var resumo = await ImportarAsync(c, c.Cartao, Linha("tx-sintetica-1", -9_999));

        Assert.Equal(new ResumoDaImportacao(0, 0, 1, 0, 0, 0), resumo);
        Assert.Equal($"{c.Corrente}|-2550", await EscalarAsync<string>(c.Banco,
            "SELECT conta_id || '|' || valor_centavos FROM lancamentos.transacoes WHERE id_externo = 'tx-sintetica-1'"));
    }

    [Fact]
    public async Task Chave_de_outro_casal_derruba_o_lote_sem_tocar_na_linha_dele()
    {
        await using var c = await IniciarAsync();
        var outro = Guid.NewGuid();
        var contaDele = Guid.NewGuid();
        await using (var db = new NpgsqlConnection(c.Banco.ConexaoSuperusuario))
        {
            await db.OpenAsync();
            foreach (var sql in new[]
                     {
                         $"INSERT INTO identidade.casais (id, nome_familia) VALUES ('{outro}', 'Outra Família')",
                         "INSERT INTO lancamentos.transacoes (id, casal_id, conta_id, data, valor_centavos, natureza, descricao_cifrada, origem, id_externo, revisao) " +
                         $"VALUES ('{Guid.NewGuid()}', '{outro}', '{contaDele}', '2026-11-01', -777, 'DESPESA', '\\x01', 'OPEN_FINANCE', 'tx-de-outro-casal', 'A_REVISAR')",
                     })
            {
                await using var cmd = new NpgsqlCommand(sql, db);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        await Assert.ThrowsAnyAsync<Exception>(() => ImportarAsync(c, c.Corrente, Linha("tx-nossa", -100), Linha("tx-de-outro-casal", -2_550)));

        Assert.Equal(0L, await EscalarAsync<long>(c.Banco, $"SELECT count(*) FROM lancamentos.transacoes WHERE casal_id <> '{outro}'"));
        Assert.Equal($"{contaDele}|-777|false", await EscalarAsync<string>(c.Banco,
            "SELECT conta_id || '|' || valor_centavos || '|' || is_deleted FROM lancamentos.transacoes WHERE id_externo = 'tx-de-outro-casal'"));
    }

    [Fact]
    public async Task Sem_apagar_ausentes_o_que_sumiu_fica()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Corrente, Linha("tx-sintetica-1", -2_550), Linha("tx-sintetica-2", -1_000));

        await using var escopo = c.Api.Services.CreateAsyncScope();
        var resumo = await escopo.ServiceProvider.GetRequiredService<IImportacaoDeLancamentos>()
            .ImportarAsync(c.Corrente, Desde, [Linha("tx-sintetica-1", -2_550)], CancellationToken.None, apagarAusentes: false);

        Assert.Equal(new ResumoDaImportacao(0, 0, 0, 0, 0, 0), resumo);
        Assert.Equal(2L, await AtivasAsync(c));
    }
}
