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
/// Fase 2, Tarefa 4.4: conta ligada é do banco (opção B). Lançar, transferir, apagar e conciliar à mão dão 409; editar só
/// descrição e categoria. Orçamento: "a revisar" fica fora do uso e da receita; "a confirmar" conta (D14). Dados sintéticos;
/// relógio em 05/11/2026.
/// </summary>
[Collection("postgres")]
public class ContaLigadaTests(PostgresFixture pg)
{
    private const string TituloLancamentos = "Esta conta vem do banco. Os lançamentos chegam sozinhos.";
    private const string TituloSaldo = "Esta conta vem do banco. O saldo é conferido sozinho.";
    private static readonly DateOnly Desde = new(2026, 1, 1);
    private static readonly DateOnly Novembro = new(2026, 11, 1);

    private sealed class CasalFixo { public Guid? Id { get; set; } }

    private sealed class UsuarioDoTeste(IHttpContextAccessor acessor, CasalFixo fixo) : IUsuarioAtual
    {
        private IUsuarioAtual Atual => acessor.HttpContext is null ? new UsuarioSistema(fixo.Id) : new UsuarioAtualHttp(acessor);
        public Guid? UsuarioId => Atual.UsuarioId;
        public Guid? CasalId => Atual.CasalId;
        public string? Ip => Atual.Ip;
    }

    private sealed record Ctx(ApiDeTeste Api, BancoDeTeste Banco, HttpClient Cliente, Guid Ligada, Guid Poupanca, Guid Manual) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Api.DisposeAsync();

        public async Task<T> ComAsync<TS, T>(Func<TS, Task<T>> acao) where TS : notnull
        {
            await using var escopo = Api.Services.CreateAsyncScope();
            return await acao(escopo.ServiceProvider.GetRequiredService<TS>());
        }
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
        var cliente = await api.ClienteLogadoAsync(ana);

        var ligada = Guid.CreateVersion7();
        var poupanca = Guid.CreateVersion7();
        await using (var escopo = api.Services.CreateAsyncScope())
        {
            var ligadas = escopo.ServiceProvider.GetRequiredService<IContasLigadas>();
            Assert.IsType<ResultadoDaContaLigada.Pronta>(await ligadas.CriarAsync(
                new NovaContaLigada(ligada, "CORRENTE", "Itaú Corrente · Ana", "Itaú", ana, 0, Desde, null, null, null), CancellationToken.None));
            Assert.IsType<ResultadoDaContaLigada.Pronta>(await ligadas.CriarAsync(
                new NovaContaLigada(poupanca, "POUPANCA", "Itaú Poupança · Ana", "Itaú", ana, 0, Desde, null, null, null), CancellationToken.None));
        }
        var r = await cliente.PostAsJsonAsync("/api/v1/contas", new
        {
            tipo = "CORRENTE", apelido = "Carteira manual", saldoInicialCentavos = 100_000L, saldoInicialEm = "2026-01-01",
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var manual = Guid.Parse(r.Headers.Location!.ToString().Split('/').Last());
        return new Ctx(api, banco, cliente, ligada, poupanca, manual);
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

    private static async Task<string> TituloAsync(HttpResponseMessage r) => (await JsonAsync(r)).GetProperty("title").GetString()!;

    private static async Task<Guid> CategoriaAsync(HttpClient cliente, string nome)
    {
        var lista = await JsonAsync(await cliente.GetAsync("/api/v1/categorias"));
        return lista.EnumerateArray().Single(c => c.GetProperty("nome").GetString() == nome).GetProperty("id").GetGuid();
    }

    private static Task ImportarAsync(Ctx c, params LancamentoImportado[] lote) =>
        c.ComAsync<IImportacaoDeLancamentos, ResumoDaImportacao>(i => i.ImportarAsync(c.Ligada, Desde, lote, CancellationToken.None));

    private static LancamentoImportado Linha(string id, long valor, bool aConfirmar = false) =>
        new(id, new DateOnly(2026, 11, 3), valor, "MERCADO SINTETICO", aConfirmar, null, null, null, null);

    private static Task<Guid> IdAsync(Ctx c, string idExterno) =>
        EscalarAsync<Guid>(c.Banco, $"SELECT id FROM lancamentos.transacoes WHERE origem = 'OPEN_FINANCE' AND id_externo = '{idExterno}'");

    private static object Edicao(long valor, string data, string descricao, Guid categoria, string natureza = "DESPESA") =>
        new { natureza, valorCentavos = valor, data, descricao, categoriaId = categoria };

    private static Task<long> TransacoesAsync(Ctx c) => EscalarAsync<long>(c.Banco, "SELECT count(*) FROM lancamentos.transacoes");

    [Fact]
    public async Task Lancamento_manual_em_conta_ligada_da_409()
    {
        await using var c = await IniciarAsync();

        var r = await c.Cliente.PostAsJsonAsync("/api/v1/lancamentos", new
        {
            chaveIdempotente = Guid.NewGuid(), contaId = c.Ligada, natureza = "DESPESA", valorCentavos = 2_500L, data = "2026-11-04",
            descricao = "Padaria", categoriaId = await CategoriaAsync(c.Cliente, "Mercado"),
        });

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal(TituloLancamentos, await TituloAsync(r));
        Assert.Equal(0L, await TransacoesAsync(c));
    }

    [Fact]
    public async Task Em_conta_ligada_so_descricao_e_categoria_mudam()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, Linha("tx-sintetica-1", -5_000));
        var id = await IdAsync(c, "tx-sintetica-1");
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        var rota = $"/api/v1/lancamentos/{id}";

        foreach (var (corpo, motivo) in new[]
                 {
                     (Edicao(5_001, "2026-11-03", "Mercado", mercado), "valor"),
                     (Edicao(5_000, "2026-11-04", "Mercado", mercado), "data"),
                     (Edicao(5_000, "2026-11-03", "Mercado", await CategoriaAsync(c.Cliente, "Salário"), "RECEITA"), "natureza"),
                 })
        {
            var recusa = await c.Cliente.PutAsJsonAsync(rota, corpo);
            Assert.True(recusa.StatusCode == HttpStatusCode.Conflict, $"{motivo}: {(int)recusa.StatusCode}");
            Assert.Equal(TituloLancamentos, await TituloAsync(recusa));
        }
        var apagar = await c.Cliente.DeleteAsync(rota);
        Assert.Equal(HttpStatusCode.Conflict, apagar.StatusCode);
        Assert.Equal(TituloLancamentos, await TituloAsync(apagar));

        var ok = await c.Cliente.PutAsJsonAsync(rota, Edicao(5_000, "2026-11-03", "Compras da semana", mercado));

        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        var l = await JsonAsync(await c.Cliente.GetAsync(rota));
        Assert.Equal("Compras da semana", l.GetProperty("descricao").GetString());
        Assert.Equal(mercado, l.GetProperty("categoria").GetProperty("id").GetGuid());
        Assert.Equal("REVISADA", l.GetProperty("revisao").GetString());
        Assert.Equal(-5_000, l.GetProperty("valorCentavos").GetInt64());
        Assert.False(await EscalarAsync<bool>(c.Banco, $"SELECT is_deleted FROM lancamentos.transacoes WHERE id = '{id}'"));
    }

    [Fact]
    public async Task Transferencia_manual_com_conta_ligada_da_409()
    {
        await using var c = await IniciarAsync();

        foreach (var (origem, destino) in new[] { (c.Manual, c.Ligada), (c.Ligada, c.Manual) })
        {
            var r = await c.Cliente.PostAsJsonAsync("/api/v1/transferencias", new
            {
                chaveIdempotente = Guid.NewGuid(), contaOrigemId = origem, contaDestinoId = destino, valorCentavos = 1_000L,
                data = "2026-11-04", descricao = "Transferência",
            });
            Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
            Assert.Equal(TituloLancamentos, await TituloAsync(r));
        }
        Assert.Equal(0L, await TransacoesAsync(c));
    }

    [Fact]
    public async Task Conciliacao_manual_em_conta_ligada_da_409()
    {
        await using var c = await IniciarAsync();

        var r = await c.Cliente.PostAsJsonAsync($"/api/v1/contas/{c.Poupanca}/conciliacao",
            new { saldoInformadoCentavos = 10_000L, saldoExibidoCentavos = 0L });

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal(TituloSaldo, await TituloAsync(r));
        Assert.Equal(0L, await TransacoesAsync(c));
    }

    [Fact]
    public async Task Conta_manual_continua_aceitando_tudo()
    {
        await using var c = await IniciarAsync();

        var r = await c.Cliente.PostAsJsonAsync("/api/v1/lancamentos", new
        {
            chaveIdempotente = Guid.NewGuid(), contaId = c.Manual, natureza = "DESPESA", valorCentavos = 2_500L, data = "2026-11-04",
            descricao = "Padaria", categoriaId = await CategoriaAsync(c.Cliente, "Mercado"),
        });

        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var id = (await JsonAsync(r)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await c.Cliente.PutAsJsonAsync($"/api/v1/lancamentos/{id}",
            Edicao(3_000, "2026-11-05", "Padaria", await CategoriaAsync(c.Cliente, "Mercado")))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.Cliente.DeleteAsync($"/api/v1/lancamentos/{id}")).StatusCode);
    }

    [Fact]
    public async Task A_revisar_fica_fora_do_uso_e_da_receita()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, Linha("tx-despesa", -5_000), Linha("tx-receita", 300_000));
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        var salario = await CategoriaAsync(c.Cliente, "Salário");
        // A sugestão da etapa 5 deixa a categoria preenchida ainda "a revisar": continua fora.
        await EscalarAsync<int>(c.Banco, $"UPDATE lancamentos.transacoes SET categoria_id = '{mercado}' WHERE id_externo = 'tx-despesa' RETURNING 1");
        await EscalarAsync<int>(c.Banco, $"UPDATE lancamentos.transacoes SET categoria_id = '{salario}' WHERE id_externo = 'tx-receita' RETURNING 1");

        Assert.DoesNotContain(await c.ComAsync<IConsultaDeUso, IReadOnlyList<UsoDaCategoria>>(u => u.UsoPorCategoriaAsync(Novembro)),
            u => u.CategoriaId == mercado);
        Assert.Equal(0L, await c.ComAsync<IConsultaDeUso, long>(u => u.ReceitasAsync(Novembro)));

        var despesa = await IdAsync(c, "tx-despesa");
        Assert.Equal(HttpStatusCode.NoContent, (await c.Cliente.PutAsJsonAsync($"/api/v1/lancamentos/{despesa}",
            Edicao(5_000, "2026-11-03", "Mercado", mercado))).StatusCode);
        var receita = await IdAsync(c, "tx-receita");
        Assert.Equal(HttpStatusCode.NoContent, (await c.Cliente.PutAsJsonAsync($"/api/v1/lancamentos/{receita}",
            Edicao(300_000, "2026-11-03", "Salário", salario, "RECEITA"))).StatusCode);

        Assert.Equal(5_000, (await c.ComAsync<IConsultaDeUso, IReadOnlyList<UsoDaCategoria>>(u => u.UsoPorCategoriaAsync(Novembro)))
            .Single(u => u.CategoriaId == mercado).UsadoCentavos);
        Assert.Equal(300_000L, await c.ComAsync<IConsultaDeUso, long>(u => u.ReceitasAsync(Novembro)));
    }

    [Fact]
    public async Task A_confirmar_conta_no_uso()
    {
        await using var c = await IniciarAsync();
        await ImportarAsync(c, Linha("tx-pendente", -4_000, aConfirmar: true));
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        var id = await IdAsync(c, "tx-pendente");
        Assert.Equal(HttpStatusCode.NoContent, (await c.Cliente.PutAsJsonAsync($"/api/v1/lancamentos/{id}",
            Edicao(4_000, "2026-11-03", "Mercado", mercado))).StatusCode);

        var uso = await c.ComAsync<IConsultaDeUso, IReadOnlyList<UsoDaCategoria>>(u => u.UsoPorCategoriaAsync(Novembro));

        Assert.Equal(4_000, uso.Single(u => u.CategoriaId == mercado).UsadoCentavos);
        var l = await JsonAsync(await c.Cliente.GetAsync($"/api/v1/lancamentos/{id}"));
        Assert.Equal("A_CONFIRMAR", l.GetProperty("confirmacao").GetString());
    }
}
