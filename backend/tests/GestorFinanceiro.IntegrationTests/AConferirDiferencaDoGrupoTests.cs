using System.Net;
using System.Text.Json;
using GestorFinanceiro.Modules.Lancamentos.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GestorFinanceiro.IntegrationTests;

/// <summary>
/// Fase 2, Tarefa 6.3 pela ordem D25 do CEO: sem rendimento automático. Por grupo de investimento, no máximo um item aberto
/// em "A conferir" com a diferença valor no banco − saldo do sistema, recalculada a cada sincronização; zero fecha sozinho.
/// Nada é lançado sem a ação do casal. Grupo "Reserva": saldo inicial R$ 10.000,00 em 01/11; hoje é 05/11.
/// </summary>
public partial class AConferirTests
{
    private const string Resgate = "03060000";
    private static readonly DateOnly Hoje = new(2026, 11, 5);

    private static async Task ConferirGrupoAsync(Ctx c, long noBanco, DateOnly? dia = null)
    {
        await using var escopo = c.Api.Services.CreateAsyncScope();
        await escopo.ServiceProvider.GetRequiredService<IImportacaoDeLancamentos>()
            .ConferirDiferencaDoGrupoAsync(c.Grupo, dia ?? Hoje, noBanco, CancellationToken.None);
    }

    /// <summary>"valor|estado|resolucao (ou -)|primeiro" dos itens de diferença do grupo, pela chave.</summary>
    private static Task<string> DiferencasAsync(Ctx c) => EscalarAsync<string>(c.Banco,
        "SELECT coalesce(string_agg(valor_centavos || '|' || estado || '|' || coalesce(resolucao, '-') || '|' || primeiro_do_grupo, ';' ORDER BY chave), '') " +
        "FROM lancamentos.itens_a_conferir WHERE tipo = 'DIFERENCA_DO_GRUPO'");

    private static Task<long> DoSistemaNoGrupoAsync(Ctx c) => EscalarAsync<long>(c.Banco,
        $"SELECT count(*) FROM lancamentos.transacoes WHERE conta_id = '{c.Grupo}' AND origem = 'SISTEMA'");

    private static async Task<JsonElement> ItemDoGrupoAsync(Ctx c) =>
        (await JsonAsync(await c.Cliente.GetAsync("/api/v1/a-conferir"))).EnumerateArray()
            .Single(i => i.GetProperty("tipo").GetString() == "DIFERENCA_DO_GRUPO");

    private static string[] Opcoes(JsonElement item) => item.GetProperty("opcoes").EnumerateArray().Select(o => o.GetString()!).ToArray();

    private static async Task<Guid> ItemDeMovimentoAsync(Ctx c) =>
        (await JsonAsync(await c.Cliente.GetAsync("/api/v1/a-conferir"))).EnumerateArray()
            .Single(i => i.GetProperty("tipo").GetString() == "APORTE_OU_RESGATE").GetProperty("id").GetGuid();

    // ------------------------------------------------------------------ recálculo e fechamento sozinho

    /// <summary>D25, 1: um item aberto por grupo, recalculado a cada leitura (valor e dia); nada é lançado.</summary>
    [Fact]
    public async Task Diferenca_do_grupo_abre_um_item_e_a_leitura_seguinte_o_atualiza_sem_lancar()
    {
        await using var c = await IniciarAsync();

        await ConferirGrupoAsync(c, 1_010_000, Hoje.AddDays(-1));
        Assert.Equal("10000|ABERTO|-|true", await DiferencasAsync(c));
        await ConferirGrupoAsync(c, 1_012_500);

        Assert.Equal("12500|ABERTO|-|true", await DiferencasAsync(c));
        var item = await ItemDoGrupoAsync(c);
        Assert.Equal(12_500, item.GetProperty("valorCentavos").GetInt64());
        Assert.Equal("2026-11-05", item.GetProperty("data").GetString());
        Assert.Equal(c.Grupo, item.GetProperty("contaId").GetGuid());
        Assert.Equal(0L, await DoSistemaNoGrupoAsync(c));
        Assert.Equal(1_000_000, await EscalarAsync<long>(c.Banco, $"SELECT saldo_inicial_centavos FROM contas.contas WHERE id = '{c.Grupo}'"));
    }

    /// <summary>D25, 1: o banco garante um item aberto só por grupo (índice parcial).</summary>
    [Fact]
    public async Task Segundo_item_aberto_no_mesmo_grupo_e_recusado_pelo_banco()
    {
        await using var c = await IniciarAsync();
        await ConferirGrupoAsync(c, 1_010_000);
        var casal = await EscalarAsync<Guid>(c.Banco, "SELECT casal_id FROM lancamentos.itens_a_conferir");

        var erro = await Assert.ThrowsAsync<PostgresException>(() => EscalarAsync<int>(c.Banco,
            "INSERT INTO lancamentos.itens_a_conferir (id, casal_id, tipo, chave, conta_id, valor_centavos, data, valor_banco_centavos) " +
            $"VALUES ('{Guid.NewGuid()}', '{casal}', 'DIFERENCA_DO_GRUPO', 'diferenca:{c.Grupo}:9', '{c.Grupo}', 1, '2026-11-05', 1) RETURNING 1"));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, erro.SqlState);
    }

    /// <summary>D25, 1: diferença zero fecha o item sozinho ("resolvido pelo banco"), sem lançamento e com auditoria.</summary>
    [Fact]
    public async Task Diferenca_zero_fecha_o_item_pelo_banco_sem_lancamento()
    {
        await using var c = await IniciarAsync();
        await ConferirGrupoAsync(c, 990_000);
        Assert.Equal("-10000|ABERTO|-|true", await DiferencasAsync(c));
        var id = await EscalarAsync<Guid>(c.Banco, "SELECT id FROM lancamentos.itens_a_conferir");

        await ConferirGrupoAsync(c, 1_000_000);
        await ConferirGrupoAsync(c, 1_000_000);

        Assert.Equal("0|RESOLVIDO|RESOLVIDO_PELO_BANCO|true", await DiferencasAsync(c));
        Assert.Equal(0L, await DoSistemaNoGrupoAsync(c));
        Assert.Empty((await JsonAsync(await c.Cliente.GetAsync("/api/v1/a-conferir"))).EnumerateArray());
        Assert.Equal(1L, await EscalarAsync<long>(c.Banco,
            $"SELECT count(*) FROM audit.eventos WHERE tabela = 'lancamentos.itens_a_conferir' AND entidade_id = '{id}' AND acao = 'UPDATE' " +
            "AND alteracoes::text LIKE '%RESOLVIDO_PELO_BANCO%'"));
    }

    /// <summary>D25, 1: sem diferença, nenhum item; a diferença que volta depois de fechada abre outro, que não é o primeiro.</summary>
    [Fact]
    public async Task Diferenca_que_volta_depois_de_fechada_abre_outro_item()
    {
        await using var c = await IniciarAsync();
        await ConferirGrupoAsync(c, 1_000_000);
        Assert.Equal("", await DiferencasAsync(c));
        await ConferirGrupoAsync(c, 1_005_000);
        await ConferirGrupoAsync(c, 1_000_000);

        await ConferirGrupoAsync(c, 1_003_000);

        Assert.Equal("0|RESOLVIDO|RESOLVIDO_PELO_BANCO|true;3000|ABERTO|-|false", await DiferencasAsync(c));
    }

    // ------------------------------------------------------------------ as ações do casal

    /// <summary>D25, 2: as opções pelo sinal; "ajustar o saldo inicial" só no primeiro item; sem aporte/resgate aberto, sem "parear".</summary>
    [Fact]
    public async Task Opcoes_pelo_sinal_e_pelo_primeiro_item()
    {
        await using var c = await IniciarAsync();
        await ConferirGrupoAsync(c, 1_010_000);
        Assert.Equal(["RENDIMENTO", "AJUSTAR_SALDO_INICIAL"], Opcoes(await ItemDoGrupoAsync(c)));
        await ConferirGrupoAsync(c, 995_000);
        Assert.Equal(["IR_IOF_TAXA", "AJUSTAR_SALDO_INICIAL", "DEIXAR_PARA_DEPOIS"], Opcoes(await ItemDoGrupoAsync(c)));

        await ImportarAsync(c, c.Corrente, Linha("tx-aplicacao", -5_000, descricao: "APLICACAO CDB", codigo: Aplicacao));
        await ReconhecerAsync(c);
        Assert.Equal(["IR_IOF_TAXA", "PAREAR_MOVIMENTO", "AJUSTAR_SALDO_INICIAL", "DEIXAR_PARA_DEPOIS"], Opcoes(await ItemDoGrupoAsync(c)));
        await ConferirGrupoAsync(c, 1_010_000);
        Assert.Equal(["RENDIMENTO", "PAREAR_MOVIMENTO", "AJUSTAR_SALDO_INICIAL"], Opcoes(await ItemDoGrupoAsync(c)));

        var item = (await ItemDoGrupoAsync(c)).GetProperty("id").GetGuid();
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        Assert.Equal(HttpStatusCode.BadRequest, (await ResolverAsync(c, item, new { opcao = "IR_IOF_TAXA", categoriaId = mercado })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ResolverAsync(c, item, new { opcao = "DEIXAR_PARA_DEPOIS" })).StatusCode);
        Assert.Equal(0L, await DoSistemaNoGrupoAsync(c));
    }

    /// <summary>D25, 2: "é rendimento" lança a receita Rendimentos com a data de hoje e a chave do dia; não entra no orçamento.</summary>
    [Fact]
    public async Task E_rendimento_lanca_receita_de_hoje_com_a_chave_do_dia()
    {
        await using var c = await IniciarAsync();
        await ConferirGrupoAsync(c, 1_010_000, Hoje.AddDays(-2));

        var r = await ResolverAsync(c, (await ItemDoGrupoAsync(c)).GetProperty("id").GetGuid(), new { opcao = "RENDIMENTO" });

        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal("10000|RESOLVIDO|RENDIMENTO|true", await DiferencasAsync(c));
        Assert.Equal("RECEITA|10000|2026-11-05|RENDIMENTOS", await EscalarAsync<string>(c.Banco,
            "SELECT t.natureza || '|' || t.valor_centavos || '|' || t.data || '|' || k.papel FROM lancamentos.transacoes t " +
            $"JOIN lancamentos.categorias k ON k.id = t.categoria_id WHERE t.origem = 'SISTEMA' AND t.id_externo = 'rendimento:{c.Grupo}:2026-11-05'"));
        Assert.Equal(0, (await OrcamentoAsync(c)).Receitas);
        // A leitura seguinte, com o mesmo valor no banco, não abre nada: o sistema já bate.
        await ConferirGrupoAsync(c, 1_010_000);
        Assert.Equal("10000|RESOLVIDO|RENDIMENTO|true", await DiferencasAsync(c));
    }

    /// <summary>D25, 2: dois rendimentos no mesmo dia somam na mesma chave.</summary>
    [Fact]
    public async Task Dois_rendimentos_no_mesmo_dia_somam_na_mesma_chave()
    {
        await using var c = await IniciarAsync();
        await ConferirGrupoAsync(c, 1_010_000);
        await ResolverAsync(c, (await ItemDoGrupoAsync(c)).GetProperty("id").GetGuid(), new { opcao = "RENDIMENTO" });
        await ConferirGrupoAsync(c, 1_012_000);

        Assert.Equal(HttpStatusCode.NoContent,
            (await ResolverAsync(c, (await ItemDoGrupoAsync(c)).GetProperty("id").GetGuid(), new { opcao = "RENDIMENTO" })).StatusCode);

        Assert.Equal(12_000, await EscalarAsync<long>(c.Banco,
            $"SELECT valor_centavos FROM lancamentos.transacoes WHERE origem = 'SISTEMA' AND id_externo = 'rendimento:{c.Grupo}:2026-11-05'"));
        Assert.Equal(1L, await DoSistemaNoGrupoAsync(c));
    }

    /// <summary>D25, 2: "IR/IOF ou taxa" lança a despesa na categoria escolhida (chave do dia); outra categoria no mesmo dia é 409.</summary>
    [Fact]
    public async Task IR_IOF_lanca_despesa_na_categoria_escolhida()
    {
        await using var c = await IniciarAsync();
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        var salario = await CategoriaAsync(c.Cliente, "Salário");
        await ConferirGrupoAsync(c, 995_000);
        var item = (await ItemDoGrupoAsync(c)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.BadRequest, (await ResolverAsync(c, item, new { opcao = "IR_IOF_TAXA" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ResolverAsync(c, item, new { opcao = "IR_IOF_TAXA", categoriaId = salario })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ResolverAsync(c, item, new { opcao = "IR_IOF_TAXA", categoriaId = mercado })).StatusCode);

        Assert.Equal("-5000|RESOLVIDO|IR_IOF_TAXA|true", await DiferencasAsync(c));
        var chave = $"ajuste-investimento:{c.Grupo}:2026-11-05";
        Assert.Equal($"DESPESA|-5000|2026-11-05|{mercado}", await EscalarAsync<string>(c.Banco,
            $"SELECT natureza || '|' || valor_centavos || '|' || data || '|' || categoria_id FROM lancamentos.transacoes WHERE id_externo = '{chave}'"));

        await ConferirGrupoAsync(c, 992_000);
        var segundo = (await ItemDoGrupoAsync(c)).GetProperty("id").GetGuid();
        var outra = await CategoriaAsync(c.Cliente, "Casa e contas");
        Assert.Equal(HttpStatusCode.Conflict, (await ResolverAsync(c, segundo, new { opcao = "IR_IOF_TAXA", categoriaId = outra })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ResolverAsync(c, segundo, new { opcao = "IR_IOF_TAXA", categoriaId = mercado })).StatusCode);
        Assert.Equal(-8_000, await EscalarAsync<long>(c.Banco, $"SELECT valor_centavos FROM lancamentos.transacoes WHERE id_externo = '{chave}'"));
    }

    /// <summary>D25, 2: "deixar para depois" não lança e mantém o item aberto.</summary>
    [Fact]
    public async Task Deixar_para_depois_nao_lanca_e_mantem_aberto()
    {
        await using var c = await IniciarAsync();
        await ConferirGrupoAsync(c, 995_000);

        var r = await ResolverAsync(c, (await ItemDoGrupoAsync(c)).GetProperty("id").GetGuid(), new { opcao = "DEIXAR_PARA_DEPOIS" });

        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal("-5000|ABERTO|-|true", await DiferencasAsync(c));
        Assert.Equal(0L, await DoSistemaNoGrupoAsync(c));
    }

    /// <summary>D25, 2: "ajustar o saldo inicial" só no primeiro item; põe a diferença no saldo inicial, sem lançamento.</summary>
    [Fact]
    public async Task Ajustar_o_saldo_inicial_so_no_primeiro_item()
    {
        await using var c = await IniciarAsync();
        await ConferirGrupoAsync(c, 1_010_000);

        Assert.Equal(HttpStatusCode.NoContent,
            (await ResolverAsync(c, (await ItemDoGrupoAsync(c)).GetProperty("id").GetGuid(), new { opcao = "AJUSTAR_SALDO_INICIAL" })).StatusCode);

        Assert.Equal("10000|RESOLVIDO|AJUSTAR_SALDO_INICIAL|true", await DiferencasAsync(c));
        Assert.Equal(1_010_000, await EscalarAsync<long>(c.Banco, $"SELECT saldo_inicial_centavos FROM contas.contas WHERE id = '{c.Grupo}'"));
        Assert.Equal(0L, await DoSistemaNoGrupoAsync(c));

        await ConferirGrupoAsync(c, 1_015_000);
        var segundo = await ItemDoGrupoAsync(c);
        Assert.Equal(["RENDIMENTO"], Opcoes(segundo));
        Assert.Equal(HttpStatusCode.BadRequest,
            (await ResolverAsync(c, segundo.GetProperty("id").GetGuid(), new { opcao = "AJUSTAR_SALDO_INICIAL" })).StatusCode);
        Assert.Equal(1_010_000, await EscalarAsync<long>(c.Banco, $"SELECT saldo_inicial_centavos FROM contas.contas WHERE id = '{c.Grupo}'"));
    }

    /// <summary>
    /// D25, 2: se o Contas gravou o saldo inicial e o item não fechou (queda no meio), pedir de novo recalcula zero e só fecha;
    /// o saldo não soma duas vezes. Outra opção com a diferença já zerada é 409 e o item fecha.
    /// </summary>
    [Fact]
    public async Task Ajustar_de_novo_depois_da_queda_nao_soma()
    {
        await using var c = await IniciarAsync();
        await ConferirGrupoAsync(c, 1_010_000);
        var item = (await ItemDoGrupoAsync(c)).GetProperty("id").GetGuid();
        await EscalarAsync<int>(c.Banco, $"UPDATE contas.contas SET saldo_inicial_centavos = 1010000 WHERE id = '{c.Grupo}' RETURNING 1");

        Assert.Equal(HttpStatusCode.NoContent, (await ResolverAsync(c, item, new { opcao = "AJUSTAR_SALDO_INICIAL" })).StatusCode);

        Assert.Equal(1_010_000, await EscalarAsync<long>(c.Banco, $"SELECT saldo_inicial_centavos FROM contas.contas WHERE id = '{c.Grupo}'"));
        Assert.Equal("RESOLVIDO|AJUSTAR_SALDO_INICIAL", await EscalarAsync<string>(c.Banco,
            $"SELECT estado || '|' || resolucao FROM lancamentos.itens_a_conferir WHERE id = '{item}'"));
    }

    /// <summary>D25, 2: o valor usado é o de agora; se mudou desde a tela, nada é lançado com o velho (409).</summary>
    [Fact]
    public async Task Rendimento_com_o_valor_velho_da_409_e_o_item_mostra_o_novo()
    {
        await using var c = await IniciarAsync();
        await ConferirGrupoAsync(c, 1_010_000);
        var item = (await ItemDoGrupoAsync(c)).GetProperty("id").GetGuid();
        await EscalarAsync<int>(c.Banco, $"UPDATE contas.contas SET saldo_inicial_centavos = 1004000 WHERE id = '{c.Grupo}' RETURNING 1");

        Assert.Equal(HttpStatusCode.Conflict, (await ResolverAsync(c, item, new { opcao = "RENDIMENTO" })).StatusCode);
        Assert.Equal("6000|ABERTO|-|true", await DiferencasAsync(c));
        Assert.Equal(0L, await DoSistemaNoGrupoAsync(c));
        await EscalarAsync<int>(c.Banco, $"UPDATE contas.contas SET saldo_inicial_centavos = 1010000 WHERE id = '{c.Grupo}' RETURNING 1");
        Assert.Equal(HttpStatusCode.Conflict, (await ResolverAsync(c, item, new { opcao = "RENDIMENTO" })).StatusCode);
        Assert.Equal("0|RESOLVIDO|RESOLVIDO_PELO_BANCO|true", await DiferencasAsync(c));
        Assert.Equal(0L, await DoSistemaNoGrupoAsync(c));
    }

    /// <summary>D25, 2: "parear com aporte": o aporte aberto na corrente vira o par com o grupo; zerou, o item fecha.</summary>
    [Fact]
    public async Task Parear_com_aporte_fecha_quando_zera()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Corrente, Linha("tx-aplicacao", -10_000, descricao: "APLICACAO CDB", codigo: Aplicacao));
        await ReconhecerAsync(c);
        await ConferirGrupoAsync(c, 1_010_000);
        var movimento = await ItemDeMovimentoAsync(c);
        var item = (await ItemDoGrupoAsync(c)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.BadRequest, (await ResolverAsync(c, item, new { opcao = "PAREAR_MOVIMENTO" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ResolverAsync(c, item, new { opcao = "PAREAR_MOVIMENTO", movimentoId = item })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ResolverAsync(c, item, new { opcao = "PAREAR_MOVIMENTO", movimentoId = movimento })).StatusCode);

        Assert.Equal("0|RESOLVIDO|PAREADO_COM_MOVIMENTO|true", await DiferencasAsync(c));
        Assert.Equal("RESOLVIDO|APORTE", await EscalarAsync<string>(c.Banco,
            $"SELECT estado || '|' || resolucao FROM lancamentos.itens_a_conferir WHERE id = '{movimento}'"));
        Assert.True(await MesmoGrupoAsync(c, "tx-aplicacao", $"par:{await IdAsync(c, "tx-aplicacao")}"));
        Assert.Equal(10_000, (await OrcamentoAsync(c)).Aportes);
        Assert.Equal(0L, await EscalarAsync<long>(c.Banco,
            $"SELECT count(*) FROM lancamentos.transacoes WHERE conta_id = '{c.Grupo}' AND natureza <> 'TRANSFERENCIA'"));
        Assert.Empty((await JsonAsync(await c.Cliente.GetAsync("/api/v1/a-conferir"))).EnumerateArray());
    }

    /// <summary>D25, 2: "parear com resgate" que não cobre a diferença deixa o resto aberto, com o valor de agora.</summary>
    [Fact]
    public async Task Parear_com_resgate_deixa_o_resto_aberto()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Corrente, Linha("tx-resgate", 4_000, descricao: "RESGATE CDB", codigo: Resgate));
        await ReconhecerAsync(c);
        await ConferirGrupoAsync(c, 990_000);
        var item = (await ItemDoGrupoAsync(c)).GetProperty("id").GetGuid();

        var r = await ResolverAsync(c, item, new { opcao = "PAREAR_MOVIMENTO", movimentoId = await ItemDeMovimentoAsync(c) });

        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal("-6000|ABERTO|-|true", await DiferencasAsync(c));
        Assert.Equal(-6_000, (await ItemDoGrupoAsync(c)).GetProperty("valorCentavos").GetInt64());
        Assert.Equal(1L, await DoSistemaNoGrupoAsync(c)); // só a perna do resgate
    }

    /// <summary>D25, 1: o aporte resolvido pela própria corrente recalcula o item do grupo (o rendimento nunca conta o aporte).</summary>
    [Fact]
    public async Task Aporte_resolvido_pela_corrente_recalcula_o_item_do_grupo()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, c.Corrente, Linha("tx-aplicacao", -10_000, descricao: "APLICACAO CDB", codigo: Aplicacao));
        await ReconhecerAsync(c);
        await ConferirGrupoAsync(c, 1_012_000);

        Assert.Equal(HttpStatusCode.NoContent, (await ResolverAsync(c, await ItemDeMovimentoAsync(c), new { opcao = "APORTE", contaId = c.Grupo })).StatusCode);

        Assert.Equal("2000|ABERTO|-|true", await DiferencasAsync(c));
        Assert.Equal(["RENDIMENTO", "AJUSTAR_SALDO_INICIAL"], Opcoes(await ItemDoGrupoAsync(c)));
    }

    /// <summary>Grupo arquivado: o item não lança nada (409) e a leitura não o mexe.</summary>
    [Fact]
    public async Task Item_de_grupo_arquivado_da_409()
    {
        await using var c = await IniciarAsync();
        await ConferirGrupoAsync(c, 1_010_000);
        var item = (await ItemDoGrupoAsync(c)).GetProperty("id").GetGuid();
        await EscalarAsync<int>(c.Banco, $"UPDATE contas.contas SET arquivada = true WHERE id = '{c.Grupo}' RETURNING 1");

        Assert.Equal(HttpStatusCode.Conflict, (await ResolverAsync(c, item, new { opcao = "RENDIMENTO" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ResolverAsync(c, item, new { opcao = "AJUSTAR_SALDO_INICIAL" })).StatusCode);
        Assert.Equal("10000|ABERTO|-|true", await DiferencasAsync(c));
        Assert.Equal(0L, await DoSistemaNoGrupoAsync(c));
    }

    /// <summary>Outra opção num item já resolvido (pelo banco, pelo outro cônjuge, numa tela velha) é 409; a mesma, 204.</summary>
    [Fact]
    public async Task Outra_opcao_num_item_resolvido_da_409()
    {
        await using var c = await IniciarAsync();
        await ConferirGrupoAsync(c, 1_010_000);
        var item = (await ItemDoGrupoAsync(c)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await ResolverAsync(c, item, new { opcao = "RENDIMENTO" })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await ResolverAsync(c, item, new { opcao = "RENDIMENTO" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ResolverAsync(c, item, new { opcao = "AJUSTAR_SALDO_INICIAL" })).StatusCode);
        Assert.Equal(1L, await DoSistemaNoGrupoAsync(c));
    }

    // ------------------------------------------------------------------ isolamento entre casais

    /// <summary>D25, 4: a leitura de um casal não toca o item do outro, mesmo no mesmo conta_id; o outro não o vê nem o resolve.</summary>
    [Fact]
    public async Task Diferenca_do_grupo_de_outro_casal_fica_isolada()
    {
        await using var c = await IniciarAsync();
        var outro = Guid.NewGuid();
        var alheio = Guid.NewGuid();
        await EscalarAsync<int>(c.Banco, $"INSERT INTO identidade.casais (id, nome_familia) VALUES ('{outro}', 'Outra Família') RETURNING 1");
        await EscalarAsync<int>(c.Banco,
            "INSERT INTO lancamentos.itens_a_conferir (id, casal_id, tipo, chave, conta_id, valor_centavos, data, valor_banco_centavos, primeiro_do_grupo) " +
            $"VALUES ('{alheio}', '{outro}', 'DIFERENCA_DO_GRUPO', 'diferenca:{c.Grupo}:1', '{c.Grupo}', 777700, '2026-11-04', 1777700, true) RETURNING 1");

        await ConferirGrupoAsync(c, 1_000_000);
        await ConferirGrupoAsync(c, 1_010_000);

        Assert.Equal("777700|ABERTO|-|1777700", await EscalarAsync<string>(c.Banco,
            $"SELECT valor_centavos || '|' || estado || '|' || coalesce(resolucao, '-') || '|' || valor_banco_centavos FROM lancamentos.itens_a_conferir WHERE id = '{alheio}'"));
        var corpo = await (await c.Cliente.GetAsync("/api/v1/a-conferir")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(alheio.ToString(), corpo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("777700", corpo, StringComparison.Ordinal);
        Assert.Equal(10_000, (await ItemDoGrupoAsync(c)).GetProperty("valorCentavos").GetInt64());
        Assert.Equal(HttpStatusCode.NotFound, (await ResolverAsync(c, alheio, new { opcao = "RENDIMENTO" })).StatusCode);
        Assert.Equal("ABERTO", await EscalarAsync<string>(c.Banco, $"SELECT estado FROM lancamentos.itens_a_conferir WHERE id = '{alheio}'"));
    }
}
