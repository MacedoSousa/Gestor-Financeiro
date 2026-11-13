using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GestorFinanceiro.Api.Infra;
using GestorFinanceiro.IntegrationTests.Infra;
using GestorFinanceiro.Modules.Contas.Contracts;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.Modules.Investimentos.Contracts;
using GestorFinanceiro.SharedKernel.Aplicacao;
using GestorFinanceiro.UnitTests;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GestorFinanceiro.IntegrationTests;

/// <summary>
/// Contratos de escrita da Fase 2, etapa 3 (D5, D17): o Contas cria e marca conta ligada, o Investimentos liga conta a
/// meta. Conta ligada vem do banco: saldo inicial calculado (D3), apagar recusado, arquivar permitido (decisão 7).
/// Dados sintéticos.
/// </summary>
[Collection("postgres")]
public class ContasLigadasTests(PostgresFixture pg)
{
    private const string TituloSaldoDoBanco = "Esta conta vem do banco. O saldo inicial é calculado.";
    private const string TituloApagarLigada = "Esta conta vem do banco. Arquive para parar a sincronização.";
    private static readonly DateOnly InicioDoHistorico = new(2026, 1, 1);

    /// <summary>Dentro de uma requisição vale a sessão; fora dela (o escopo do teste), o casal fixo.</summary>
    private sealed class CasalFixo { public Guid? Id { get; set; } }

    private sealed class UsuarioDoTeste(IHttpContextAccessor acessor, CasalFixo fixo) : IUsuarioAtual
    {
        private IUsuarioAtual Atual => acessor.HttpContext is null ? new UsuarioSistema(fixo.Id) : new UsuarioAtualHttp(acessor);
        public Guid? UsuarioId => Atual.UsuarioId;
        public Guid? CasalId => Atual.CasalId;
        public string? Ip => Atual.Ip;
    }

    private sealed record Ctx(ApiDeTeste Api, BancoDeTeste Banco, HttpClient Cliente, Guid Ana) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Api.DisposeAsync();
    }

    private async Task<Ctx> IniciarAsync()
    {
        var fixo = new CasalFixo();
        var banco = await pg.NovoBancoAsync();
        var api = new ApiDeTeste(banco, servicos: s =>
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
        return new Ctx(api, banco, await api.ClienteLogadoAsync(ana), ana);
    }

    private static async Task<T> EscalarAsync<T>(BancoDeTeste banco, string sql)
    {
        await using var db = new NpgsqlConnection(banco.ConexaoSuperusuario);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, db);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<TR> ComAsync<TS, TR>(Ctx c, Func<TS, Task<TR>> acao) where TS : notnull
    {
        await using var escopo = c.Api.Services.CreateAsyncScope();
        return await acao(escopo.ServiceProvider.GetRequiredService<TS>());
    }

    private static NovaContaLigada Corrente(Guid id, string apelido = "Itaú Corrente · Ana", Guid? titular = null) =>
        new(id, "CORRENTE", apelido, "Itaú", titular, 0, InicioDoHistorico, null, null, null);

    private static NovaContaLigada Cartao(Guid id, string apelido = "Itaú Cartão · Ana") =>
        new(id, "CARTAO", apelido, "Itaú", null, 0, InicioDoHistorico, 3, 10, 500_000);

    private static Task<ResultadoDaContaLigada> CriarLigadaAsync(Ctx c, NovaContaLigada pedido) =>
        ComAsync<IContasLigadas, ResultadoDaContaLigada>(c, s => s.CriarAsync(pedido, CancellationToken.None));

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<string> TituloAsync(HttpResponseMessage r) => (await JsonAsync(r)).GetProperty("title").GetString()!;

    private static object Put(string apelido, long saldo, string saldoEm, string tipo = "CORRENTE", int? fechamento = null,
        int? vencimento = null, bool? arquivada = null) => new
        {
            tipo, apelido, instituicao = "Itaú", saldoInicialCentavos = saldo, saldoInicialEm = saldoEm,
            diaFechamento = fechamento, diaVencimento = vencimento, limiteCentavos = (long?)null, arquivada,
        };

    private static async Task<Guid> CriarManualAsync(HttpClient cliente, string apelido, string tipo = "CORRENTE")
    {
        var r = await cliente.PostAsJsonAsync("/api/v1/contas", new
        {
            tipo, apelido, instituicao = "Itaú", saldoInicialCentavos = 10_000L, saldoInicialEm = "2026-01-01",
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return Guid.Parse(r.Headers.Location!.ToString().Split('/').Last());
    }

    [Fact]
    public async Task Conta_ligada_nasce_com_ligada_em()
    {
        await using var c = await IniciarAsync();
        var id = Guid.CreateVersion7();

        var resultado = await CriarLigadaAsync(c, Corrente(id));

        Assert.Equal(new ResultadoDaContaLigada.Pronta(id), resultado);
        Assert.True(await EscalarAsync<bool>(c.Banco, $"SELECT ligada_em IS NOT NULL FROM contas.contas WHERE id = '{id}'"));
    }

    [Fact]
    public async Task CriarAsync_duas_vezes_com_o_mesmo_id_nao_duplica()
    {
        await using var c = await IniciarAsync();
        var id = Guid.CreateVersion7();

        Assert.Equal(new ResultadoDaContaLigada.Pronta(id), await CriarLigadaAsync(c, Corrente(id)));
        Assert.Equal(new ResultadoDaContaLigada.Pronta(id), await CriarLigadaAsync(c, Corrente(id)));

        Assert.Equal(1L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM contas.contas"));
    }

    [Fact]
    public async Task Apelido_repetido_devolve_a_sugestao()
    {
        await using var c = await IniciarAsync();
        await CriarManualAsync(c.Cliente, "Itaú Corrente · Ana");
        await CriarManualAsync(c.Cliente, "Itaú Corrente · Ana (2)");

        var resultado = await CriarLigadaAsync(c, Corrente(Guid.CreateVersion7()));

        Assert.Equal(new ResultadoDaContaLigada.ApelidoRepetido("Itaú Corrente · Ana (3)"), resultado);
        Assert.Equal(2L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM contas.contas"));
    }

    [Fact]
    public async Task Conta_ligada_invalida_nao_e_gravada()
    {
        await using var c = await IniciarAsync();

        var resultado = await CriarLigadaAsync(c, Cartao(Guid.CreateVersion7()) with { DiaFechamento = null });

        Assert.IsType<ResultadoDaContaLigada.Invalida>(resultado);
        Assert.Equal(0L, await EscalarAsync<long>(c.Banco, "SELECT count(*) FROM contas.contas"));
    }

    [Fact]
    public async Task Put_nao_muda_o_saldo_inicial_de_conta_ligada()
    {
        await using var c = await IniciarAsync();
        var id = Guid.CreateVersion7();
        await CriarLigadaAsync(c, Corrente(id));

        var outroValor = await c.Cliente.PutAsJsonAsync($"/api/v1/contas/{id}", Put("Itaú Corrente · Ana", 999, "2026-01-01"));
        var outraData = await c.Cliente.PutAsJsonAsync($"/api/v1/contas/{id}", Put("Itaú Corrente · Ana", 0, "2026-02-01"));

        Assert.Equal(HttpStatusCode.Conflict, outroValor.StatusCode);
        Assert.Equal(TituloSaldoDoBanco, await TituloAsync(outroValor));
        Assert.Equal(HttpStatusCode.Conflict, outraData.StatusCode);
        Assert.Equal(0L, await EscalarAsync<long>(c.Banco, $"SELECT saldo_inicial_centavos FROM contas.contas WHERE id = '{id}'"));
    }

    [Fact]
    public async Task Put_de_conta_ligada_aceita_apelido_e_dias()
    {
        await using var c = await IniciarAsync();
        var id = Guid.CreateVersion7();
        await CriarLigadaAsync(c, Cartao(id));

        var r = await c.Cliente.PutAsJsonAsync($"/api/v1/contas/{id}",
            Put("Cartão da Ana", 0, "2026-01-01", "CARTAO", fechamento: 5, vencimento: 12));

        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal("Cartão da Ana|5|12", await EscalarAsync<string>(c.Banco,
            $"SELECT apelido || '|' || dia_fechamento || '|' || dia_vencimento FROM contas.contas WHERE id = '{id}'"));
    }

    [Fact]
    public async Task Conta_ligada_pode_ser_arquivada_e_desarquivada()
    {
        await using var c = await IniciarAsync();
        var id = Guid.CreateVersion7();
        await CriarLigadaAsync(c, Corrente(id));

        var arquivar = await c.Cliente.PutAsJsonAsync($"/api/v1/contas/{id}", Put("Itaú Corrente · Ana", 0, "2026-01-01", arquivada: true));
        Assert.Equal(HttpStatusCode.NoContent, arquivar.StatusCode);
        Assert.True(await EscalarAsync<bool>(c.Banco, $"SELECT arquivada FROM contas.contas WHERE id = '{id}'"));

        var desarquivar = await c.Cliente.PutAsJsonAsync($"/api/v1/contas/{id}", Put("Itaú Corrente · Ana", 0, "2026-01-01", arquivada: false));
        Assert.Equal(HttpStatusCode.NoContent, desarquivar.StatusCode);
        Assert.False(await EscalarAsync<bool>(c.Banco, $"SELECT arquivada FROM contas.contas WHERE id = '{id}'"));
    }

    [Fact]
    public async Task Apagar_conta_ligada_da_409()
    {
        await using var c = await IniciarAsync();
        var id = Guid.CreateVersion7();
        await CriarLigadaAsync(c, Corrente(id));

        var r = await c.Cliente.DeleteAsync($"/api/v1/contas/{id}");

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal(TituloApagarLigada, await TituloAsync(r));
        Assert.False(await EscalarAsync<bool>(c.Banco, $"SELECT is_deleted FROM contas.contas WHERE id = '{id}'"));
    }

    [Fact]
    public async Task ContaResumo_expoe_Ligada()
    {
        await using var c = await IniciarAsync();
        var ligada = Guid.CreateVersion7();
        await CriarLigadaAsync(c, Corrente(ligada));
        var manual = await CriarManualAsync(c.Cliente, "Carteira", "DINHEIRO");

        Assert.True((await ComAsync<IConsultaDeContas, ContaResumo?>(c, s => s.ObterAsync(ligada)))!.Ligada);
        Assert.False((await ComAsync<IConsultaDeContas, ContaResumo?>(c, s => s.ObterAsync(manual)))!.Ligada);

        var lista = await JsonAsync(await c.Cliente.GetAsync("/api/v1/contas"));
        Assert.True(lista.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == ligada).GetProperty("ligada").GetBoolean());
        Assert.False(lista.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == manual).GetProperty("ligada").GetBoolean());
    }

    [Fact]
    public async Task Marcar_ligada_marca_conta_existente_e_recusa_a_que_nao_existe()
    {
        await using var c = await IniciarAsync();
        var manual = await CriarManualAsync(c.Cliente, "Corrente antiga");

        Assert.True(await ComAsync<IContasLigadas, bool>(c, s => s.MarcarLigadaAsync(manual, CancellationToken.None)));
        Assert.True(await ComAsync<IContasLigadas, bool>(c, s => s.MarcarLigadaAsync(manual, CancellationToken.None)));
        Assert.False(await ComAsync<IContasLigadas, bool>(c, s => s.MarcarLigadaAsync(Guid.CreateVersion7(), CancellationToken.None)));

        Assert.True(await EscalarAsync<bool>(c.Banco, $"SELECT ligada_em IS NOT NULL FROM contas.contas WHERE id = '{manual}'"));
    }

    [Fact]
    public async Task Saldo_inicial_so_e_definido_em_conta_ligada()
    {
        await using var c = await IniciarAsync();
        var ligada = Guid.CreateVersion7();
        await CriarLigadaAsync(c, Corrente(ligada));
        var manual = await CriarManualAsync(c.Cliente, "Corrente antiga");

        await ComAsync<IContasLigadas, bool>(c, async s =>
        {
            await s.DefinirSaldoInicialAsync(ligada, -12_345, new DateOnly(2026, 1, 2), CancellationToken.None);
            return true;
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => ComAsync<IContasLigadas, bool>(c, async s =>
        {
            await s.DefinirSaldoInicialAsync(manual, 1, InicioDoHistorico, CancellationToken.None);
            return true;
        }));

        Assert.Equal("-12345|2026-01-02", await EscalarAsync<string>(c.Banco,
            $"SELECT saldo_inicial_centavos || '|' || saldo_inicial_em FROM contas.contas WHERE id = '{ligada}'"));
        Assert.Equal(10_000L, await EscalarAsync<long>(c.Banco, $"SELECT saldo_inicial_centavos FROM contas.contas WHERE id = '{manual}'"));
    }

    [Fact]
    public async Task LigarContaAMeta_e_idempotente()
    {
        await using var c = await IniciarAsync();
        var poupanca = await CriarManualAsync(c.Cliente, "Poupança", "POUPANCA");
        var criada = await c.Cliente.PostAsJsonAsync("/api/v1/metas", new
        {
            nome = "Casa", valorAlvoCentavos = 1_000_000L, aportePlanejadoCentavos = 100_000L,
            rendimentoMensalPercentual = 0m, contaIds = new[] { poupanca },
        });
        Assert.Equal(HttpStatusCode.Created, criada.StatusCode);
        var meta = (await JsonAsync(criada)).GetProperty("id").GetGuid();
        var grupo = Guid.CreateVersion7();
        await CriarLigadaAsync(c, new NovaContaLigada(grupo, "INVESTIMENTO", "CDBs Casa", "Itaú", null, 0, InicioDoHistorico,
            null, null, null));

        Assert.True(await ComAsync<IMetasDeContas, bool>(c, s => s.LigarContaAMetaAsync(grupo, meta, CancellationToken.None)));
        Assert.True(await ComAsync<IMetasDeContas, bool>(c, s => s.LigarContaAMetaAsync(grupo, meta, CancellationToken.None)));
        Assert.False(await ComAsync<IMetasDeContas, bool>(c, s => s.LigarContaAMetaAsync(grupo, Guid.CreateVersion7(), CancellationToken.None)));

        Assert.Equal(1L, await EscalarAsync<long>(c.Banco,
            $"SELECT count(*) FROM investimentos.metas_contas WHERE conta_id = '{grupo}' AND meta_id = '{meta}' AND NOT is_deleted"));
    }

    /// <summary>Revisão de segurança da E3: o id de uma conta manual, apagada ou de outro casal não vira "Pronta".</summary>
    [Fact]
    public async Task Id_de_conta_manual_apagada_ou_de_outro_casal_nao_e_Pronta()
    {
        await using var c = await IniciarAsync();
        var manual = await CriarManualAsync(c.Cliente, "Manual");
        var apagada = await CriarManualAsync(c.Cliente, "Apagada");
        Assert.Equal(HttpStatusCode.NoContent, (await c.Cliente.DeleteAsync($"/api/v1/contas/{apagada}")).StatusCode);
        var deOutro = Guid.CreateVersion7();
        var outroCasal = Guid.CreateVersion7();
        await EscalarAsync<int>(c.Banco, $"""
            INSERT INTO identidade.casais (id, nome_familia) VALUES ('{outroCasal}', 'Outra Família');
            INSERT INTO contas.contas (id, casal_id, tipo, apelido, saldo_inicial_em, ligada_em) VALUES ('{deOutro}', '{outroCasal}', 'CORRENTE', 'De outro', '2026-01-01', now());
            SELECT 1;
            """);

        foreach (var id in new[] { manual, apagada, deOutro })
            Assert.Equal(new ResultadoDaContaLigada.IdEmUso(), await CriarLigadaAsync(c, Corrente(id, $"Nova {id:N}")));

        Assert.False(await EscalarAsync<bool>(c.Banco, $"SELECT ligada_em IS NOT NULL FROM contas.contas WHERE id = '{manual}'"));
    }
}
