using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GestorFinanceiro.Api.Infra;
using GestorFinanceiro.IntegrationTests.Infra;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Contracts;
using GestorFinanceiro.SharedKernel.Aplicacao;
using GestorFinanceiro.UnitTests;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GestorFinanceiro.IntegrationTests;

/// <summary>
/// Contratos de uso do orçamento. Os dados nascem pela API HTTP; as consultas rodam num escopo do container
/// (sem requisição) com o casal fixo, porque o orçamento as chamará de dentro do próprio processo.
/// </summary>
[Collection("postgres")]
public class UsoDoOrcamentoTests(PostgresFixture pg)
{
    private static readonly DateOnly Novembro = new(2026, 11, 1);
    private static readonly DateOnly Dezembro = new(2026, 12, 1);
    private const string SqlAna = "SELECT id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'";
    private const string SqlCasal = "SELECT casal_id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'";

    /// <summary>Dentro de uma requisição vale a sessão de verdade; fora dela (o escopo do teste), o casal fixo.</summary>
    private sealed class CasalFixo { public Guid? Id { get; set; } }

    private sealed class UsuarioDoTeste(IHttpContextAccessor acessor, CasalFixo fixo) : IUsuarioAtual
    {
        private IUsuarioAtual Atual => acessor.HttpContext is null ? new UsuarioSistema(fixo.Id) : new UsuarioAtualHttp(acessor);
        public Guid? UsuarioId => Atual.UsuarioId;
        public Guid? CasalId => Atual.CasalId;
        public string? Ip => Atual.Ip;
    }

    private sealed record Contexto(ApiDeTeste Api, BancoDeTeste Banco, HttpClient Cliente, Guid Casal) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Api.DisposeAsync();

        public async Task<T> ComConsultasAsync<T>(Func<IConsultaDeUso, IConsultaDeCategorias, Task<T>> acao)
        {
            await using var escopo = Api.Services.CreateAsyncScope();
            return await acao(escopo.ServiceProvider.GetRequiredService<IConsultaDeUso>(),
                escopo.ServiceProvider.GetRequiredService<IConsultaDeCategorias>());
        }

        public Task<long> AportesAsync(DateOnly mes) => ComConsultasAsync((u, _) => u.AportesAsync(mes));
        public Task<long> ReceitasAsync(DateOnly mes) => ComConsultasAsync((u, _) => u.ReceitasAsync(mes));
        public Task<IReadOnlyList<UsoDaCategoria>> UsoAsync(DateOnly mes) => ComConsultasAsync((u, _) => u.UsoPorCategoriaAsync(mes));
    }

    private static async Task<T> EscalarAsync<T>(BancoDeTeste banco, string sql)
    {
        await using var db = new NpgsqlConnection(banco.ConexaoSuperusuario);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, db);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task ExecutarAsync(BancoDeTeste banco, string sql)
    {
        await using var db = new NpgsqlConnection(banco.ConexaoSuperusuario);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, db);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<Contexto> IniciarAsync()
    {
        var banco = await pg.NovoBancoAsync();
        var fixo = new CasalFixo();
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
        var casal = await EscalarAsync<Guid>(banco, SqlCasal);
        fixo.Id = casal;
        var ana = await EscalarAsync<Guid>(banco, SqlAna);
        return new Contexto(api, banco, await api.ClienteLogadoAsync(ana), casal);
    }

    private static async Task<Guid> CriarContaAsync(HttpClient cliente, object corpo)
    {
        var resposta = await cliente.PostAsJsonAsync("/api/v1/contas", corpo);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return Guid.Parse(resposta.Headers.Location!.ToString().Split('/').Last());
    }

    private static Task<Guid> CriarContaSimplesAsync(HttpClient cliente, string tipo, string apelido) =>
        CriarContaAsync(cliente, new { tipo, apelido, saldoInicialCentavos = 0L, saldoInicialEm = "2026-01-01" });

    private static Task<Guid> CriarCartaoAsync(HttpClient cliente) =>
        CriarContaAsync(cliente, new
        {
            tipo = "CARTAO", apelido = "Nubank", saldoInicialCentavos = 0L, saldoInicialEm = "2026-01-01",
            diaFechamento = 5, diaVencimento = 12, limiteCentavos = 8_000_000L,
        });

    private static async Task<Guid> CategoriaAsync(HttpClient cliente, string nome)
    {
        var lista = await JsonAsync(await cliente.GetAsync("/api/v1/categorias"));
        return lista.EnumerateArray().Single(c => c.GetProperty("nome").GetString() == nome).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> LancarAsync(HttpClient cliente, Guid conta, Guid categoria, string natureza, long valor,
        string data, int parcelas = 1)
    {
        var resposta = await cliente.PostAsJsonAsync("/api/v1/lancamentos", new
        {
            chaveIdempotente = Guid.NewGuid(), contaId = conta, natureza, valorCentavos = valor, data,
            descricao = "Teste", contraparte = (string?)null, categoriaId = categoria, parcelas,
        });
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return (await JsonAsync(resposta)).GetProperty("id").GetGuid();
    }

    private static async Task TransferirAsync(HttpClient cliente, Guid origem, Guid destino, long valor, string data = "2026-11-04")
    {
        var resposta = await cliente.PostAsJsonAsync("/api/v1/transferencias", new
        {
            chaveIdempotente = Guid.NewGuid(), contaOrigemId = origem, contaDestinoId = destino, valorCentavos = valor, data,
            descricao = "Transferência",
        });
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
    }

    private static long Usado(IReadOnlyList<UsoDaCategoria> uso, Guid categoria) =>
        uso.SingleOrDefault(u => u.CategoriaId == categoria)?.UsadoCentavos ?? 0;

    [Fact]
    public async Task UsoPorCategoria_subtrai_estorno_e_conta_parcela_pela_data()
    {
        await using var c = await IniciarAsync();
        var corrente = await CriarContaSimplesAsync(c.Cliente, "CORRENTE", "Itaú");
        var cartao = await CriarCartaoAsync(c.Cliente);
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        var lazer = await CategoriaAsync(c.Cliente, "Lazer");
        await LancarAsync(c.Cliente, corrente, mercado, "DESPESA", 10_000, "2026-11-03");
        await LancarAsync(c.Cliente, corrente, mercado, "RECEITA", 2_500, "2026-11-04");
        // 06/11 é depois do fechamento (dia 5): a parcela 1 vence em 12/12. Por data ela é de novembro; por fatura seria de dezembro.
        await LancarAsync(c.Cliente, cartao, lazer, "DESPESA", 30_000, "2026-11-06", parcelas: 3);
        await LancarAsync(c.Cliente, corrente, await CategoriaAsync(c.Cliente, "Salário"), "RECEITA", 900_000, "2026-11-05");

        var novembro = await c.UsoAsync(Novembro);
        var dezembro = await c.UsoAsync(Dezembro);

        Assert.Equal(7_500, Usado(novembro, mercado));
        Assert.Equal(10_000, Usado(novembro, lazer));
        Assert.Equal(10_000, Usado(dezembro, lazer));
        Assert.Equal(0, Usado(dezembro, mercado));
        // Categoria de receita (Salário) nunca aparece no uso.
        Assert.Equal(new[] { mercado, lazer }.Order(), novembro.Select(u => u.CategoriaId).Order());
    }

    [Fact]
    public async Task Transferencia_e_pagamento_de_fatura_nao_entram_no_uso()
    {
        await using var c = await IniciarAsync();
        var itau = await CriarContaSimplesAsync(c.Cliente, "CORRENTE", "Itaú");
        var nubank = await CriarContaSimplesAsync(c.Cliente, "CORRENTE", "Nubank conta");
        var cartao = await CriarCartaoAsync(c.Cliente);
        await TransferirAsync(c.Cliente, itau, nubank, 12_000);
        await TransferirAsync(c.Cliente, itau, cartao, 25_000, "2026-11-05");

        var uso = await c.UsoAsync(Novembro);

        Assert.Empty(uso);
    }

    [Fact]
    public async Task Aporte_corrente_para_poupanca_conta()
    {
        await using var c = await IniciarAsync();
        var corrente = await CriarContaSimplesAsync(c.Cliente, "CORRENTE", "Itaú");
        var poupanca = await CriarContaSimplesAsync(c.Cliente, "POUPANCA", "Reserva");
        await TransferirAsync(c.Cliente, corrente, poupanca, 50_000);
        await TransferirAsync(c.Cliente, corrente, poupanca, 7_000, "2026-10-20");
        await TransferirAsync(c.Cliente, corrente, poupanca, 9_000, "2026-12-01");

        Assert.Equal(50_000, await c.AportesAsync(Novembro));
        Assert.Equal(7_000, await c.AportesAsync(new DateOnly(2026, 10, 1)));
        Assert.Equal(9_000, await c.AportesAsync(Dezembro));
    }

    [Fact]
    public async Task Aporte_corrente_para_investimento_conta()
    {
        await using var c = await IniciarAsync();
        var corrente = await CriarContaSimplesAsync(c.Cliente, "CORRENTE", "Itaú");
        var investimento = await CriarContaSimplesAsync(c.Cliente, "INVESTIMENTO", "Corretora");
        await TransferirAsync(c.Cliente, corrente, investimento, 20_000);

        Assert.Equal(20_000, await c.AportesAsync(Novembro));
    }

    [Fact]
    public async Task Poupanca_para_investimento_e_movimentacao_interna()
    {
        await using var c = await IniciarAsync();
        var poupanca = await CriarContaSimplesAsync(c.Cliente, "POUPANCA", "Reserva");
        var investimento = await CriarContaSimplesAsync(c.Cliente, "INVESTIMENTO", "Corretora");
        await TransferirAsync(c.Cliente, poupanca, investimento, 30_000);

        Assert.Equal(0, await c.AportesAsync(Novembro));
    }

    [Fact]
    public async Task Resgate_nao_conta_nem_abate()
    {
        await using var c = await IniciarAsync();
        var corrente = await CriarContaSimplesAsync(c.Cliente, "CORRENTE", "Itaú");
        var investimento = await CriarContaSimplesAsync(c.Cliente, "INVESTIMENTO", "Corretora");
        await TransferirAsync(c.Cliente, corrente, investimento, 40_000);
        await TransferirAsync(c.Cliente, investimento, corrente, 15_000);

        Assert.Equal(40_000, await c.AportesAsync(Novembro));
    }

    [Fact]
    public async Task Receitas_do_mes_nao_incluem_estorno()
    {
        await using var c = await IniciarAsync();
        var corrente = await CriarContaSimplesAsync(c.Cliente, "CORRENTE", "Itaú");
        await LancarAsync(c.Cliente, corrente, await CategoriaAsync(c.Cliente, "Salário"), "RECEITA", 900_000, "2026-11-05");
        await LancarAsync(c.Cliente, corrente, await CategoriaAsync(c.Cliente, "Mercado"), "RECEITA", 2_500, "2026-11-04");
        await LancarAsync(c.Cliente, corrente, await CategoriaAsync(c.Cliente, "Salário"), "RECEITA", 800_000, "2026-10-31");
        await LancarAsync(c.Cliente, corrente, await CategoriaAsync(c.Cliente, "Salário"), "RECEITA", 700_000, "2026-12-01");

        Assert.Equal(900_000, await c.ReceitasAsync(Novembro));
        Assert.Equal(800_000, await c.ReceitasAsync(new DateOnly(2026, 10, 1)));
        Assert.Equal(700_000, await c.ReceitasAsync(Dezembro));
    }

    [Fact]
    public async Task Lancamento_apagado_e_de_outro_casal_nao_entram_no_uso()
    {
        await using var c = await IniciarAsync();
        var corrente = await CriarContaSimplesAsync(c.Cliente, "CORRENTE", "Itaú");
        var poupanca = await CriarContaSimplesAsync(c.Cliente, "POUPANCA", "Reserva");
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        var salario = await CategoriaAsync(c.Cliente, "Salário");
        await LancarAsync(c.Cliente, corrente, mercado, "DESPESA", 10_000, "2026-11-03");
        await TransferirAsync(c.Cliente, corrente, poupanca, 50_000);
        var apagada = await LancarAsync(c.Cliente, corrente, mercado, "DESPESA", 99_000, "2026-11-03");
        Assert.Equal(HttpStatusCode.NoContent, (await c.Cliente.DeleteAsync($"/api/v1/lancamentos/{apagada}")).StatusCode);
        var outroCasal = Guid.NewGuid();
        await ExecutarAsync(c.Banco, $"INSERT INTO identidade.casais (id, nome_familia) VALUES ('{outroCasal}', 'Outra')");
        await ExecutarAsync(c.Banco,
            "INSERT INTO lancamentos.transacoes (id, casal_id, conta_id, categoria_id, data, valor_centavos, natureza, descricao_cifrada, origem, id_externo, grupo_id) VALUES " +
            $"('{Guid.NewGuid()}', '{outroCasal}', '{corrente}', '{mercado}', '2026-11-03', -77000, 'DESPESA', '\\x01', 'MANUAL', '{Guid.NewGuid()}', NULL), " +
            $"('{Guid.NewGuid()}', '{outroCasal}', '{corrente}', '{salario}', '2026-11-03', 55000, 'RECEITA', '\\x01', 'MANUAL', '{Guid.NewGuid()}', NULL)");
        var grupo = Guid.NewGuid();
        await ExecutarAsync(c.Banco,
            "INSERT INTO lancamentos.transacoes (id, casal_id, conta_id, data, valor_centavos, natureza, descricao_cifrada, origem, id_externo, grupo_id) VALUES " +
            $"('{Guid.NewGuid()}', '{outroCasal}', '{corrente}', '2026-11-03', -60000, 'TRANSFERENCIA', '\\x01', 'MANUAL', '{Guid.NewGuid()}', '{grupo}'), " +
            $"('{Guid.NewGuid()}', '{outroCasal}', '{poupanca}', '2026-11-03', 60000, 'TRANSFERENCIA', '\\x01', 'MANUAL', '{Guid.NewGuid()}', '{grupo}')");

        var uso = await c.UsoAsync(Novembro);

        Assert.Equal(10_000, Usado(uso, mercado));
        Assert.Single(uso);
        Assert.Equal(50_000, await c.AportesAsync(Novembro));
        Assert.Equal(0, await c.ReceitasAsync(Novembro));
    }

    [Fact]
    public async Task ListarDeDespesa_traz_papel_e_arquivadas()
    {
        await using var c = await IniciarAsync();
        var lazer = await CategoriaAsync(c.Cliente, "Lazer");
        var resposta = await c.Cliente.PutAsJsonAsync($"/api/v1/categorias/{lazer}", new { nome = "Lazer", tipo = "DESPESA", arquivada = true });
        Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);

        var lista = await c.ComConsultasAsync((_, cat) => cat.ListarDeDespesaAsync());

        Assert.Equal(10, lista.Count);
        Assert.Equal(PapelDeCategoria.Investimento, lista.Single(x => x.Nome == "Investimento").Papel);
        Assert.Equal(PapelDeCategoria.Dividas, lista.Single(x => x.Nome == "Dívidas e financiamentos").Papel);
        Assert.Equal(8, lista.Count(x => x.Papel is null));
        Assert.True(lista.Single(x => x.Id == lazer).Arquivada);
        Assert.Equal(1, lista.Count(x => x.Arquivada));
    }

    [Fact]
    public async Task EhDeReceita_distingue_receita_de_despesa_inexistente_apagada_e_de_outro_casal()
    {
        await using var c = await IniciarAsync();
        var salario = await CategoriaAsync(c.Cliente, "Salário");
        var mercado = await CategoriaAsync(c.Cliente, "Mercado");
        var outras = await CategoriaAsync(c.Cliente, "Outras receitas");
        Assert.Equal(HttpStatusCode.NoContent, (await c.Cliente.DeleteAsync($"/api/v1/categorias/{outras}")).StatusCode);
        var outroCasal = Guid.NewGuid();
        var alheia = Guid.NewGuid();
        await ExecutarAsync(c.Banco, $"INSERT INTO identidade.casais (id, nome_familia) VALUES ('{outroCasal}', 'Outra')");
        await ExecutarAsync(c.Banco,
            $"INSERT INTO lancamentos.categorias (id, casal_id, nome, tipo) VALUES ('{alheia}', '{outroCasal}', 'Salário', 'RECEITA')");

        Task<bool> EhDeReceita(Guid id) => c.ComConsultasAsync((_, cat) => cat.EhDeReceitaAsync(id));

        Assert.True(await EhDeReceita(salario));
        Assert.False(await EhDeReceita(mercado));
        Assert.False(await EhDeReceita(outras));
        Assert.False(await EhDeReceita(alheia));
        Assert.False(await EhDeReceita(Guid.NewGuid()));
    }
}
