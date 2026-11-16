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
/// Fase 2, etapa 6 (Tarefas 6.1 e 6.2): reconhecimento de transferência e pagamento de fatura e a caixa "A conferir".
/// Dados sintéticos; relógio em 05/11/2026 (quinta-feira).
/// </summary>
[Collection("postgres")]
public class AConferirTests(PostgresFixture pg)
{
    private static readonly DateOnly Desde = new(2026, 1, 1);
    private const string Fatura = "05100000";
    private const string MesmaPessoa = "04000000";
    private const string Aplicacao = "03000000";

    private sealed class CasalFixo { public Guid? Id { get; set; } }

    private sealed class UsuarioDoTeste(IHttpContextAccessor acessor, CasalFixo fixo) : IUsuarioAtual
    {
        private IUsuarioAtual Atual => acessor.HttpContext is null ? new UsuarioSistema(fixo.Id) : new UsuarioAtualHttp(acessor);
        public Guid? UsuarioId => Atual.UsuarioId;
        public Guid? CasalId => Atual.CasalId;
        public string? Ip => Atual.Ip;
    }

    private sealed record Ctx(ApiDeTeste Api, BancoDeTeste Banco, HttpClient Cliente, Guid Corrente, Guid Poupanca, Guid OutraCorrente, Guid Cartao,
        Guid Grupo, Guid Dinheiro) : IAsyncDisposable
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
        var bruno = await EscalarAsync<Guid>(banco, "SELECT id FROM identidade.usuarios WHERE nome_completo = 'Bruno Teste'");
        fixo.Id = await EscalarAsync<Guid>(banco, "SELECT casal_id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'");

        Guid corrente = Guid.CreateVersion7(), poupanca = Guid.CreateVersion7(), outra = Guid.CreateVersion7(), cartao = Guid.CreateVersion7(),
            grupo = Guid.CreateVersion7();
        await using (var escopo = api.Services.CreateAsyncScope())
        {
            var ligadas = escopo.ServiceProvider.GetRequiredService<IContasLigadas>();
            foreach (var conta in new[]
                     {
                         new NovaContaLigada(corrente, "CORRENTE", "Itaú Corrente · Ana", "Itaú", ana, 0, Desde, null, null, null),
                         new NovaContaLigada(poupanca, "POUPANCA", "Itaú Poupança · Ana", "Itaú", ana, 0, Desde, null, null, null),
                         new NovaContaLigada(outra, "CORRENTE", "Inter Corrente · Bruno", "Inter", bruno, 0, Desde, null, null, null),
                         new NovaContaLigada(cartao, "CARTAO", "Itaú Cartão · Ana", "Itaú", ana, 0, Desde, 3, 10, 500_000),
                         new NovaContaLigada(grupo, "INVESTIMENTO", "Reserva", "Itaú", ana, 1_000_000, new DateOnly(2026, 11, 1), null, null, null),
                     })
                Assert.IsType<ResultadoDaContaLigada.Pronta>(await ligadas.CriarAsync(conta, CancellationToken.None));
        }
        var cliente = await api.ClienteLogadoAsync(ana);
        var r = await cliente.PostAsJsonAsync("/api/v1/contas", new { tipo = "DINHEIRO", apelido = "Carteira", saldoInicialCentavos = 0L, saldoInicialEm = "2026-01-01" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var dinheiro = Guid.Parse(r.Headers.Location!.ToString().Split('/').Last());
        return new Ctx(api, banco, cliente, corrente, poupanca, outra, cartao, grupo, dinheiro);
    }

    private static async Task<T> EscalarAsync<T>(BancoDeTeste banco, string sql)
    {
        await using var db = new NpgsqlConnection(banco.ConexaoSuperusuario);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, db);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task ImportarAsync(Ctx c, Guid conta, params LancamentoImportado[] lote)
    {
        await using var escopo = c.Api.Services.CreateAsyncScope();
        await escopo.ServiceProvider.GetRequiredService<IImportacaoDeLancamentos>().ImportarAsync(conta, Desde, lote, apagarAusentes: true, CancellationToken.None);
    }

    private static async Task ReconhecerAsync(Ctx c)
    {
        await using var escopo = c.Api.Services.CreateAsyncScope();
        await escopo.ServiceProvider.GetRequiredService<IImportacaoDeLancamentos>().ReconhecerMovimentosAsync(Desde, CancellationToken.None);
    }

    private static LancamentoImportado Linha(string id, long valor, string data = "2026-11-03", string descricao = "MOVIMENTO SINTETICO",
        string? codigo = null, bool pendente = false) =>
        new(id, DateOnly.Parse(data, System.Globalization.CultureInfo.InvariantCulture), valor, descricao, pendente, null, null, codigo, null, null);

    private static Task<Guid> IdAsync(Ctx c, string idExterno) =>
        EscalarAsync<Guid>(c.Banco, $"SELECT id FROM lancamentos.transacoes WHERE id_externo = '{idExterno}'");

    /// <summary>"natureza|categoria (ou -)|revisao|grupo preenchido".</summary>
    private static Task<string> EstadoAsync(Ctx c, string idExterno) => EscalarAsync<string>(c.Banco,
        "SELECT natureza || '|' || coalesce(categoria_id::text, '-') || '|' || revisao || '|' || (grupo_id IS NOT NULL) " +
        $"FROM lancamentos.transacoes WHERE id_externo = '{idExterno}'");

    private static Task<bool> MesmoGrupoAsync(Ctx c, string a, string b) => EscalarAsync<bool>(c.Banco,
        $"SELECT (SELECT grupo_id FROM lancamentos.transacoes WHERE id_externo = '{a}') = (SELECT grupo_id FROM lancamentos.transacoes WHERE id_externo = '{b}')");

    /// <summary>"tipo|estado|resolucao (ou -)" dos itens, pela chave.</summary>
    private static Task<string> ItensAsync(Ctx c) => EscalarAsync<string>(c.Banco,
        "SELECT coalesce(string_agg(tipo || '|' || estado || '|' || coalesce(resolucao, '-'), ';' ORDER BY chave), '') FROM lancamentos.itens_a_conferir");

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<Guid> CategoriaAsync(HttpClient cliente, string nome) =>
        (await JsonAsync(await cliente.GetAsync("/api/v1/categorias"))).EnumerateArray()
            .Single(k => k.GetProperty("nome").GetString() == nome).GetProperty("id").GetGuid();

    private static async Task<Guid> ItemAsync(Ctx c) =>
        (await JsonAsync(await c.Cliente.GetAsync("/api/v1/a-conferir"))).EnumerateArray().Single().GetProperty("id").GetGuid();

    private static Task<HttpResponseMessage> ResolverAsync(Ctx c, Guid item, object corpo) =>
        c.Cliente.PostAsJsonAsync($"/api/v1/a-conferir/{item}/resolucao", corpo);

    private static async Task<(long Uso, long Receitas, long Aportes)> OrcamentoAsync(Ctx c)
    {
        await using var escopo = c.Api.Services.CreateAsyncScope();
        var uso = escopo.ServiceProvider.GetRequiredService<IConsultaDeUso>();
        var mes = new DateOnly(2026, 11, 1);
        return ((await uso.UsoPorCategoriaAsync(mes)).Sum(u => u.UsadoCentavos), await uso.ReceitasAsync(mes), await uso.AportesAsync(mes));
    }

    // ------------------------------------------------------------------ Tarefa 6.2: reconhecimento

    [Fact]
    public async Task Transferencia_entre_ligadas_vira_par_nunca_gasto_e_receita()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Corrente, Linha("tx-saida", -10_000));
        await ImportarAsync(c, c.Poupanca, Linha("tx-entrada", 10_000));

        await ReconhecerAsync(c);

        Assert.Equal("TRANSFERENCIA|-|REVISADA|true", await EstadoAsync(c, "tx-saida"));
        Assert.Equal("TRANSFERENCIA|-|REVISADA|true", await EstadoAsync(c, "tx-entrada"));
        Assert.True(await MesmoGrupoAsync(c, "tx-saida", "tx-entrada"));
        var (uso, receitas, _) = await OrcamentoAsync(c);
        Assert.Equal((0L, 0L), (uso, receitas));
        Assert.Equal("", await ItensAsync(c));
    }

    [Fact]
    public async Task Transferencia_entre_items_diferentes_do_casal_vira_par()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Corrente, Linha("tx-saida", -5_000, "2026-11-02"));
        await ImportarAsync(c, c.OutraCorrente, Linha("tx-entrada", 5_000, "2026-11-03"));

        await ReconhecerAsync(c);

        Assert.True(await MesmoGrupoAsync(c, "tx-saida", "tx-entrada"));
    }

    [Fact]
    public async Task Mesma_titularidade_entre_items_diferentes_vira_par()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Corrente, Linha("tx-saida", -7_000, codigo: MesmaPessoa));
        await ImportarAsync(c, c.OutraCorrente, Linha("tx-entrada", 7_000, codigo: MesmaPessoa));

        await ReconhecerAsync(c);

        Assert.True(await MesmoGrupoAsync(c, "tx-saida", "tx-entrada"));
        Assert.Equal("TRANSFERENCIA|-|REVISADA|true", await EstadoAsync(c, "tx-entrada"));
    }

    [Fact]
    public async Task Sem_par_so_vai_para_a_conferir_depois_de_2_dias_uteis()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Corrente, Linha("tx-quarta", -3_000, "2026-11-04", codigo: MesmaPessoa), Linha("tx-terca", -4_000, "2026-11-03", codigo: MesmaPessoa),
            Linha("tx-comum", -2_500, "2026-11-02"));

        await ReconhecerAsync(c);

        Assert.Equal("TRANSFERENCIA_SEM_PAR|ABERTO|-", await ItensAsync(c));
        Assert.Equal($"sem-par:{await IdAsync(c, "tx-terca")}", await EscalarAsync<string>(c.Banco, "SELECT chave FROM lancamentos.itens_a_conferir"));
    }

    [Fact]
    public async Task Item_sem_par_que_ganha_par_depois_se_resolve_sozinho()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Corrente, Linha("tx-saida", -4_000, "2026-11-03", codigo: MesmaPessoa));
        await ReconhecerAsync(c);
        Assert.Equal("TRANSFERENCIA_SEM_PAR|ABERTO|-", await ItensAsync(c));

        await ImportarAsync(c, c.OutraCorrente, Linha("tx-entrada", 4_000, "2026-11-04", codigo: MesmaPessoa));
        await ReconhecerAsync(c);

        Assert.True(await MesmoGrupoAsync(c, "tx-saida", "tx-entrada"));
        Assert.Equal("TRANSFERENCIA_SEM_PAR|RESOLVIDO|PAREADO_AUTOMATICAMENTE", await ItensAsync(c));
    }

    [Fact]
    public async Task Dois_candidatos_vao_para_a_conferir()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Corrente, Linha("tx-saida", -2_000, codigo: MesmaPessoa));
        await ImportarAsync(c, c.Poupanca, Linha("tx-entrada-1", 2_000, codigo: MesmaPessoa));
        await ImportarAsync(c, c.OutraCorrente, Linha("tx-entrada-2", 2_000, codigo: MesmaPessoa));

        await ReconhecerAsync(c);

        Assert.Equal("DESPESA|-|A_REVISAR|false", await EstadoAsync(c, "tx-saida"));
        Assert.Contains("TRANSFERENCIA_SEM_PAR|ABERTO|-", await ItensAsync(c));
    }

    [Fact]
    public async Task Pagamento_de_fatura_usa_as_duas_pernas_do_banco_e_nao_conta_como_gasto()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Corrente, Linha("tx-pagamento", -50_000, codigo: Fatura));
        await ImportarAsync(c, c.Cartao, Linha("tx-credito", 50_000, codigo: Fatura));

        await ReconhecerAsync(c);

        Assert.True(await MesmoGrupoAsync(c, "tx-pagamento", "tx-credito"));
        Assert.Equal("TRANSFERENCIA|-|REVISADA|true", await EstadoAsync(c, "tx-credito"));
        var (uso, receitas, aportes) = await OrcamentoAsync(c);
        Assert.Equal((0L, 0L, 0L), (uso, receitas, aportes));
    }

    /// <summary>E6, item 3: linha de fatura que o casal revisou antes da D23 perde a categoria ao parear, com auditoria.</summary>
    [Fact]
    public async Task Linha_revisada_antes_da_D23_ao_parear_perde_a_categoria_com_auditoria()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Corrente, Linha("tx-pagamento", -50_000, codigo: Fatura));
        await ImportarAsync(c, c.Cartao, Linha("tx-credito", 50_000, codigo: Fatura));
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        var id = await IdAsync(c, "tx-pagamento");
        await EscalarAsync<int>(c.Banco, $"UPDATE lancamentos.transacoes SET revisao = 'REVISADA', categoria_id = '{mercado}' WHERE id = '{id}' RETURNING 1");
        Assert.Equal(50_000, (await OrcamentoAsync(c)).Uso);

        await ReconhecerAsync(c);

        Assert.Equal("TRANSFERENCIA|-|REVISADA|true", await EstadoAsync(c, "tx-pagamento"));
        Assert.Equal(0, (await OrcamentoAsync(c)).Uso);
        Assert.Equal(1L, await EscalarAsync<long>(c.Banco,
            $"SELECT count(*) FROM audit.eventos WHERE tabela = 'lancamentos.transacoes' AND entidade_id = '{id}' AND acao = 'UPDATE' " +
            "AND alteracoes ? 'categoria_id' AND alteracoes ? 'natureza'"));
    }

    [Fact]
    public async Task Aplicacao_vai_para_a_conferir_como_aporte()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Corrente, Linha("tx-aplicacao", -100_000, descricao: "APLICACAO CDB", codigo: Aplicacao));

        await ReconhecerAsync(c);

        Assert.Equal("APORTE_OU_RESGATE|ABERTO|-", await ItensAsync(c));
        var item = (await JsonAsync(await c.Cliente.GetAsync("/api/v1/a-conferir"))).EnumerateArray().Single();
        Assert.Equal("APORTE_OU_RESGATE", item.GetProperty("tipo").GetString());
        Assert.Equal("APLICACAO CDB", item.GetProperty("descricao").GetString());
        Assert.Contains("APORTE", item.GetProperty("opcoes").EnumerateArray().Select(o => o.GetString()));
    }

    // ------------------------------------------------------------------ Tarefa 6.1: "A conferir" e o par manual

    [Fact]
    public async Task Pendente_que_sumiu_aparece_uma_vez()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Cartao, Linha("tx-outra", -100), Linha("tx-pendente", -2_550, descricao: "LOJA SINTETICA", pendente: true));

        foreach (var _ in Enumerable.Range(0, 3)) await ImportarAsync(c, c.Cartao, Linha("tx-outra", -100));

        Assert.True(await EscalarAsync<bool>(c.Banco, "SELECT is_deleted FROM lancamentos.transacoes WHERE id_externo = 'tx-pendente'"));
        Assert.Equal("PENDENTE_SUMIU|ABERTO|-", await ItensAsync(c));
    }

    [Fact]
    public async Task Pendente_sem_par_unico_vai_para_a_conferir()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Cartao, Linha("tx-pendente", -2_550, descricao: "PADARIA DO ZE", pendente: true));

        await ImportarAsync(c, c.Cartao, Linha("tx-def-1", -2_550, "2026-11-03", "PADARIA DO ZE"), Linha("tx-def-2", -2_550, "2026-11-04", "PADARIA DO ZE"));

        Assert.Equal("PENDENTE_SEM_PAR|ABERTO|-", await ItensAsync(c));
        Assert.False(await EscalarAsync<bool>(c.Banco, "SELECT is_deleted FROM lancamentos.transacoes WHERE id_externo = 'tx-pendente'"));
    }

    [Fact]
    public async Task Transferencia_para_conta_manual_cria_o_lado_manual_com_chave_par()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Corrente, Linha("tx-saque", -3_000, codigo: MesmaPessoa));
        await ReconhecerAsync(c);
        var item = await ItemAsync(c);

        var r = await ResolverAsync(c, item, new { opcao = "TRANSFERENCIA", contaId = c.Dinheiro });

        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        var id = await IdAsync(c, "tx-saque");
        Assert.Equal($"SISTEMA|3000|{c.Dinheiro}", await EscalarAsync<string>(c.Banco,
            $"SELECT origem || '|' || valor_centavos || '|' || conta_id FROM lancamentos.transacoes WHERE id_externo = 'par:{id}'"));
        Assert.True(await MesmoGrupoAsync(c, "tx-saque", $"par:{id}"));
        Assert.Equal("TRANSFERENCIA_SEM_PAR|RESOLVIDO|TRANSFERENCIA", await ItensAsync(c));
    }

    [Fact]
    public async Task Repetir_a_resolucao_nao_duplica()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Corrente, Linha("tx-saque", -3_000, codigo: MesmaPessoa));
        await ReconhecerAsync(c);
        var item = await ItemAsync(c);
        Assert.Equal(HttpStatusCode.NoContent, (await ResolverAsync(c, item, new { opcao = "TRANSFERENCIA", contaId = c.Dinheiro })).StatusCode);

        var segunda = await ResolverAsync(c, item, new { opcao = "TRANSFERENCIA", contaId = c.Dinheiro });

        Assert.Equal(HttpStatusCode.NoContent, segunda.StatusCode);
        Assert.Equal(1L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM lancamentos.transacoes WHERE id_externo LIKE 'par:%'"));
    }

    [Fact]
    public async Task Nada_e_descartado()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Corrente, Linha("tx-a", -3_000, codigo: MesmaPessoa), Linha("tx-b", -4_000, codigo: MesmaPessoa));
        await ReconhecerAsync(c);
        var itens = (await JsonAsync(await c.Cliente.GetAsync("/api/v1/a-conferir"))).EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(2, itens.Count);

        Assert.Equal(HttpStatusCode.NoContent,
            (await ResolverAsync(c, itens[0], new { opcao = "GASTO", categoriaId = await CategoriaAsync(c.Cliente, "Mercado") })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ResolverAsync(c, itens[1], new { opcao = "DEIXAR_PARA_DEPOIS" })).StatusCode);

        Assert.Equal(2L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM lancamentos.itens_a_conferir WHERE NOT is_deleted"));
        Assert.Equal("ABERTO|-;RESOLVIDO|GASTO", await EscalarAsync<string>(c.Banco,
            "SELECT string_agg(estado || '|' || coalesce(resolucao, '-'), ';' ORDER BY estado) FROM lancamentos.itens_a_conferir"));
        Assert.Single((await JsonAsync(await c.Cliente.GetAsync("/api/v1/a-conferir"))).EnumerateArray());
    }

    [Fact]
    public async Task Aporte_cria_a_perna_no_grupo_e_conta_como_aporte()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Corrente, Linha("tx-aplicacao", -100_000, descricao: "APLICACAO CDB", codigo: Aplicacao));
        await ReconhecerAsync(c);

        var r = await ResolverAsync(c, await ItemAsync(c), new { opcao = "APORTE", contaId = c.Grupo });

        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        var id = await IdAsync(c, "tx-aplicacao");
        Assert.True(await MesmoGrupoAsync(c, "tx-aplicacao", $"par:{id}"));
        Assert.Equal(100_000, (await OrcamentoAsync(c)).Aportes);
    }

    // ------------------------------------------------------------------ Revisão da E6 (revisor-seguranca, 16/11)

    /// <summary>Achado 4: parcela nunca é candidata a par (nem no reconhecimento nem em "A conferir"); nada quebra.</summary>
    [Fact]
    public async Task Parcela_nunca_entra_no_par()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Corrente, Linha("tx-fatura", -30_000, codigo: Fatura));
        await ImportarAsync(c, c.Cartao, new LancamentoImportado("tx-estorno", new DateOnly(2026, 11, 3), 30_000, "ESTORNO PARCELADO", false, 1, 3, Fatura, null));

        await ReconhecerAsync(c);

        Assert.StartsWith("RECEITA|", await EstadoAsync(c, "tx-estorno"));
        Assert.Equal("TRANSFERENCIA_SEM_PAR|ABERTO|-", await ItensAsync(c));
        var r = await ResolverAsync(c, await ItemAsync(c), new { opcao = "TRANSFERENCIA", contaId = c.Cartao });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("O outro lado ainda não veio do banco nessa conta. Tente depois da próxima sincronização.",
            (await JsonAsync(r)).GetProperty("title").GetString());
    }

    /// <summary>Achado 6: dois lados possíveis na conta escolhida dão 409, sem parear nenhum.</summary>
    [Fact]
    public async Task Dois_lados_possiveis_na_conta_escolhida_dao_409()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Corrente, Linha("tx-saida", -3_000, codigo: MesmaPessoa));
        await ImportarAsync(c, c.Poupanca, Linha("tx-entrada-1", 3_000), Linha("tx-entrada-2", 3_000, "2026-11-04"));
        await ReconhecerAsync(c);

        var r = await ResolverAsync(c, await ItemAsync(c), new { opcao = "TRANSFERENCIA", contaId = c.Poupanca });

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("Há mais de um lançamento que pode ser o outro lado nessa conta. Resolva pelo extrato.",
            (await JsonAsync(r)).GetProperty("title").GetString());
        Assert.Equal(0L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM lancamentos.transacoes WHERE natureza = 'TRANSFERENCIA'"));
    }

    /// <summary>Achado 6: item de outro casal não aparece, não é resolvido (404) e não pareia com as linhas deste casal.</summary>
    [Fact]
    public async Task Item_e_linha_de_outro_casal_nao_aparecem_nem_sao_resolvidos_nem_pareiam()
    {
        await using var c = await IniciarAsync();
        var outro = Guid.NewGuid();
        var contaAlheia = Guid.NewGuid();
        var linhaAlheia = Guid.NewGuid();
        var itemAlheio = Guid.NewGuid();
        await EscalarAsync<int>(c.Banco, $"INSERT INTO identidade.casais (id, nome_familia) VALUES ('{outro}', 'Outra Família') RETURNING 1");
        await EscalarAsync<int>(c.Banco,
            "INSERT INTO lancamentos.transacoes (id, casal_id, conta_id, data, valor_centavos, natureza, descricao_cifrada, origem, id_externo, revisao, categoria_externa) " +
            $"VALUES ('{linhaAlheia}', '{outro}', '{c.Poupanca}', '2026-11-03', 3000, 'RECEITA', '\\x01', 'OPEN_FINANCE', 'tx-alheia', 'A_REVISAR', '{MesmaPessoa}') RETURNING 1");
        await EscalarAsync<int>(c.Banco,
            "INSERT INTO lancamentos.itens_a_conferir (id, casal_id, tipo, chave, conta_id, transacao_id, valor_centavos, data) " +
            $"VALUES ('{itemAlheio}', '{outro}', 'TRANSFERENCIA_SEM_PAR', 'sem-par:{linhaAlheia}', '{contaAlheia}', '{linhaAlheia}', 3000, '2026-11-03') RETURNING 1");
        // A linha alheia está no mesmo conta_id da poupança deste casal: só o filtro Casal a separa (rodada 2, nota 5).
        await ImportarAsync(c, c.Corrente, Linha("tx-saida", -3_000, codigo: MesmaPessoa));

        await ReconhecerAsync(c);

        Assert.Equal("RECEITA", await EscalarAsync<string>(c.Banco, $"SELECT natureza FROM lancamentos.transacoes WHERE id = '{linhaAlheia}'"));
        Assert.Equal("DESPESA", (await EstadoAsync(c, "tx-saida")).Split('|')[0]);
        var ids = (await JsonAsync(await c.Cliente.GetAsync("/api/v1/a-conferir"))).EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.DoesNotContain(itemAlheio, ids);
        Assert.Equal(HttpStatusCode.NotFound, (await ResolverAsync(c, itemAlheio, new { opcao = "CIENTE" })).StatusCode);
        Assert.Equal("ABERTO", await EscalarAsync<string>(c.Banco, $"SELECT estado FROM lancamentos.itens_a_conferir WHERE id = '{itemAlheio}'"));
    }

    // ------------------------------------------------------------------ Revisão do E6a (D24), rodada 1

    /// <summary>D1: o item da linha que virou o outro lado de um par resolvido em "A conferir" se resolve junto.</summary>
    [Fact]
    public async Task Item_da_linha_que_virou_o_outro_lado_se_resolve_junto()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Corrente, Linha("tx-saida", -3_000, codigo: MesmaPessoa));
        await ImportarAsync(c, c.Poupanca, Linha("tx-e1", 3_000, codigo: MesmaPessoa));
        await ImportarAsync(c, c.OutraCorrente, Linha("tx-e2", 3_000, codigo: MesmaPessoa));
        await ReconhecerAsync(c);
        var saida = await EscalarAsync<Guid>(c.Banco,
            $"SELECT i.id FROM lancamentos.itens_a_conferir i WHERE i.transacao_id = '{await IdAsync(c, "tx-saida")}'");

        Assert.Equal(HttpStatusCode.NoContent, (await ResolverAsync(c, saida, new { opcao = "TRANSFERENCIA", contaId = c.Poupanca })).StatusCode);

        Assert.True(await MesmoGrupoAsync(c, "tx-saida", "tx-e1"));
        Assert.Equal("RESOLVIDO|TRANSFERENCIA", await EscalarAsync<string>(c.Banco,
            $"SELECT estado || '|' || resolucao FROM lancamentos.itens_a_conferir WHERE transacao_id = '{await IdAsync(c, "tx-e1")}'"));
        Assert.Equal("ABERTO", await EscalarAsync<string>(c.Banco,
            $"SELECT estado FROM lancamentos.itens_a_conferir WHERE transacao_id = '{await IdAsync(c, "tx-e2")}'"));
    }

    /// <summary>D1: o item cuja linha o banco tirou só oferece "Ciente" (antes, toda opção dava 409 e o item não fechava).</summary>
    [Fact]
    public async Task Item_cuja_linha_o_banco_tirou_so_oferece_ciente()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Corrente, Linha("tx-pix", -3_000, codigo: MesmaPessoa));
        await ReconhecerAsync(c);
        await ImportarAsync(c, c.Corrente, Linha("tx-outra", -100));
        await ImportarAsync(c, c.Corrente, Linha("tx-outra", -100));

        var item = (await JsonAsync(await c.Cliente.GetAsync("/api/v1/a-conferir"))).EnumerateArray().Single();
        Assert.Equal(["CIENTE"], item.GetProperty("opcoes").EnumerateArray().Select(o => o.GetString()).ToList());
        Assert.Equal(HttpStatusCode.NoContent, (await ResolverAsync(c, item.GetProperty("id").GetGuid(), new { opcao = "CIENTE" })).StatusCode);
        Assert.Equal("TRANSFERENCIA_SEM_PAR|RESOLVIDO|CIENTE", await ItensAsync(c));
    }

    /// <summary>D1: o reconhecimento fecha o item aberto cuja linha já é transferência (pareada por outro caminho).</summary>
    [Fact]
    public async Task Reconhecimento_fecha_o_item_cuja_linha_ja_e_transferencia()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Corrente, Linha("tx-saida", -3_000, codigo: MesmaPessoa));
        await ReconhecerAsync(c);
        var linha = await IdAsync(c, "tx-saida");
        await EscalarAsync<int>(c.Banco,
            $"UPDATE lancamentos.transacoes SET natureza = 'TRANSFERENCIA', categoria_id = NULL, grupo_id = '{Guid.NewGuid()}' WHERE id = '{linha}' RETURNING 1");

        await ReconhecerAsync(c);

        Assert.Equal("TRANSFERENCIA_SEM_PAR|RESOLVIDO|PAREADO_AUTOMATICAMENTE", await ItensAsync(c));
    }

    /// <summary>D2: resolvida como gasto, a linha conta no orçamento, o extrato não a marca entre contas e a categoria se corrige.</summary>
    [Fact]
    public async Task Linha_resolvida_como_gasto_conta_no_orcamento_e_a_categoria_se_corrige()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Corrente, Linha("tx-pix", -3_000, codigo: MesmaPessoa));
        await ReconhecerAsync(c);
        Assert.Equal(HttpStatusCode.NoContent,
            (await ResolverAsync(c, await ItemAsync(c), new { opcao = "GASTO", categoriaId = await CategoriaAsync(c.Cliente, "Mercado") })).StatusCode);
        var id = await IdAsync(c, "tx-pix");

        Assert.Equal(3_000, (await OrcamentoAsync(c)).Uso);
        var extrato = (await JsonAsync(await c.Cliente.GetAsync("/api/v1/lancamentos?mes=2026-11"))).GetProperty("lancamentos").EnumerateArray()
            .Single(l => l.GetProperty("id").GetGuid() == id);
        Assert.False(extrato.GetProperty("entreContas").GetBoolean());
        var casa = await CategoriaAsync(c.Cliente, "Casa e contas");
        var r = await c.Cliente.PutAsJsonAsync($"/api/v1/lancamentos/{id}",
            new { natureza = "DESPESA", valorCentavos = 3_000L, data = "2026-11-03", descricao = "PIX", categoriaId = casa });
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        Assert.Equal(casa, await EscalarAsync<Guid>(c.Banco, $"SELECT categoria_id FROM lancamentos.transacoes WHERE id = '{id}'"));
    }

    /// <summary>D3: o par entre duas pernas do banco (conta destino ligada) pelo "A conferir".</summary>
    [Fact]
    public async Task Transferencia_com_o_outro_lado_do_banco_pareia_as_duas_pernas()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Corrente, Linha("tx-saida", -3_000, codigo: MesmaPessoa));
        await ImportarAsync(c, c.OutraCorrente, Linha("tx-entrada", 3_000, "2026-11-07"));
        await ReconhecerAsync(c);

        var r = await ResolverAsync(c, await ItemAsync(c), new { opcao = "TRANSFERENCIA", contaId = c.OutraCorrente });

        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.True(await MesmoGrupoAsync(c, "tx-saida", "tx-entrada"));
        Assert.Equal("TRANSFERENCIA|-|REVISADA|true", await EstadoAsync(c, "tx-entrada"));
        Assert.Equal(0L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM lancamentos.transacoes WHERE origem = 'SISTEMA'"));
    }

    /// <summary>D3: opção que não é do item, aporte em conta que não é poupança nem investimento e categoria de outro casal: 400.</summary>
    [Fact]
    public async Task Opcao_de_fora_conta_errada_e_categoria_alheia_dao_400()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Corrente, Linha("tx-aplicacao", -100_000, descricao: "APLICACAO CDB", codigo: Aplicacao));
        await ReconhecerAsync(c);
        var item = await ItemAsync(c);
        var outro = Guid.NewGuid();
        var alheia = Guid.NewGuid();
        await EscalarAsync<int>(c.Banco, $"INSERT INTO identidade.casais (id, nome_familia) VALUES ('{outro}', 'Outra Família') RETURNING 1");
        await EscalarAsync<int>(c.Banco,
            $"INSERT INTO lancamentos.categorias (id, casal_id, nome, tipo) VALUES ('{alheia}', '{outro}', 'Alheia', 'DESPESA') RETURNING 1");

        Assert.Equal(HttpStatusCode.BadRequest, (await ResolverAsync(c, item, new { opcao = "TRANSFERENCIA", contaId = c.Dinheiro })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ResolverAsync(c, item, new { opcao = "APORTE", contaId = c.Dinheiro })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ResolverAsync(c, item, new { opcao = "GASTO", categoriaId = alheia })).StatusCode);
        Assert.Equal("APORTE_OU_RESGATE|ABERTO|-", await ItensAsync(c));
    }
}
