using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GestorFinanceiro.IntegrationTests.Infra;
using GestorFinanceiro.Modules.Identidade.Aplicacao;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.UnitTests;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GestorFinanceiro.IntegrationTests;

[Collection("postgres")]
public class SessaoTests(PostgresFixture pg)
{
    private const string RotaEu = "/api/v1/auth/eu";
    private const string RotaSair = "/api/v1/auth/sair";

    private static RelogioFixo NovoRelogio() => new() { Agora = DateTimeOffset.UtcNow };

    /// <summary>Cadastra o casal pela API e devolve os ids (Ana, Bruno).</summary>
    private static async Task<(Guid Ana, Guid Bruno)> CadastrarAsync(ApiDeTeste api, BancoDeTeste banco)
    {
        var pedido = new CadastroDoCasalDto(
            api.CodigoInstalacao, "Família Teste",
            new ConjugeNovoDto("Ana Teste", CpfSintetico.Gerar(1), "senha-da-ana-123"),
            new ConjugeNovoDto("Bruno Teste", CpfSintetico.Gerar(2), "senha-do-bruno-1"));
        var resposta = await api.CreateClient().PostAsJsonAsync("/api/v1/casal", pedido);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);

        await using var db = new NpgsqlConnection(banco.ConexaoSuperusuario);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT id FROM identidade.usuarios ORDER BY nome_completo", db);
        await using var leitor = await cmd.ExecuteReaderAsync();
        await leitor.ReadAsync();
        var ana = leitor.GetGuid(0);
        await leitor.ReadAsync();
        return (ana, leitor.GetGuid(0));
    }

    private static async Task<long> ContarAsync(BancoDeTeste banco, string sql)
    {
        await using var db = new NpgsqlConnection(banco.ConexaoSuperusuario);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, db);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Rota_protegida_sem_cookie_responde_401()
    {
        await using var api = new ApiDeTeste(await pg.NovoBancoAsync());
        var resposta = await api.CreateClient().GetAsync("/api/v1/casal");
        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    [Fact]
    public async Task Cadastro_e_saude_continuam_anonimos()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco);
        var cliente = api.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync("/api/v1/saude")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync("/api/v1/casal/cadastro")).StatusCode);
        await CadastrarAsync(api, banco);
    }

    [Fact]
    public async Task Sessao_valida_da_acesso_e_cookie_tem_atributos_certos()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco);
        var (ana, _) = await CadastrarAsync(api, banco);
        var cliente = await api.ClienteLogadoAsync(ana);

        var resposta = await cliente.GetAsync(RotaEu);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var eu = (await resposta.Content.ReadFromJsonAsync<EuDto>())!;
        Assert.Equal(ana, eu.UsuarioId);
        Assert.Equal("Ana", eu.Apelido);
        Assert.Equal("Bruno", eu.ApelidoDoConjuge);
        Assert.True(eu.ConfirmacaoRecente);
        Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync("/api/v1/casal")).StatusCode);
    }

    [Fact]
    public async Task Cookie_de_sessao_criado_pelo_servico_tem_atributos_certos()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco);
        var (ana, _) = await CadastrarAsync(api, banco);

        await using var escopo = api.Services.CreateAsyncScope();
        var sessoes = escopo.ServiceProvider.GetRequiredService<ServicoDeSessoes>();
        var db = escopo.ServiceProvider.GetRequiredService<GestorFinanceiro.Modules.Identidade.Infra.IdentidadeDbContext>();
        var usuario = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstAsync(
            Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.IgnoreQueryFilters(db.Usuarios, [GestorFinanceiro.SharedKernel.Persistencia.Filtros.Casal]),
            u => u.Id == ana);
        var (token, expiraEm) = await sessoes.CriarAsync(usuario, CancellationToken.None);

        var http = new DefaultHttpContext { RequestServices = api.Services };
        ServicoDeSessoes.EscreverCookie(http.Response, token, expiraEm);
        var cookie = http.Response.Headers.SetCookie.ToString();

        Assert.StartsWith($"__Host-gf_sessao={token}", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain", cookie, StringComparison.OrdinalIgnoreCase);
        var maxAge = int.Parse(cookie.Split(';').Select(p => p.Trim()).First(p => p.StartsWith("max-age=", StringComparison.OrdinalIgnoreCase))[8..], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(maxAge, (int)TimeSpan.FromDays(30).TotalSeconds - 60, (int)TimeSpan.FromDays(30).TotalSeconds);
    }

    [Fact]
    public async Task Sessao_encerrada_responde_401()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco);
        var (ana, _) = await CadastrarAsync(api, banco);
        var cliente = await api.ClienteLogadoAsync(ana);

        var sair = await cliente.PostAsync(RotaSair, null);

        Assert.Equal(HttpStatusCode.NoContent, sair.StatusCode);
        Assert.Contains("__Host-gf_sessao=", sair.Headers.GetValues("Set-Cookie").Single());
        Assert.Equal(HttpStatusCode.Unauthorized, (await cliente.GetAsync(RotaEu)).StatusCode);
        Assert.Equal(1L, await ContarAsync(banco, "SELECT count(*) FROM identidade.sessoes WHERE is_deleted"));
        Assert.Equal(1L, await ContarAsync(banco, "SELECT count(*) FROM identidade.sessoes"));
    }

    [Fact]
    public async Task Sessao_parada_por_7_dias_expira()
    {
        var banco = await pg.NovoBancoAsync();
        var relogio = NovoRelogio();
        await using var api = new ApiDeTeste(banco, relogio: relogio);
        var (ana, _) = await CadastrarAsync(api, banco);
        var cliente = await api.ClienteLogadoAsync(ana);

        relogio.Agora += TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1);
        Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync(RotaEu)).StatusCode);

        relogio.Agora += TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1);
        Assert.Equal(HttpStatusCode.Unauthorized, (await cliente.GetAsync(RotaEu)).StatusCode);
    }

    [Fact]
    public async Task Sessao_expira_em_30_dias_mesmo_em_uso()
    {
        var banco = await pg.NovoBancoAsync();
        var relogio = NovoRelogio();
        await using var api = new ApiDeTeste(banco, relogio: relogio);
        var (ana, _) = await CadastrarAsync(api, banco);
        var cliente = await api.ClienteLogadoAsync(ana);

        for (var dia = 0; dia < 29; dia++)
        {
            relogio.Agora += TimeSpan.FromDays(1);
            Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync(RotaEu)).StatusCode);
        }

        relogio.Agora += TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1);
        Assert.Equal(HttpStatusCode.Unauthorized, (await cliente.GetAsync(RotaEu)).StatusCode);
    }

    [Fact]
    public async Task Uso_frequente_nao_inunda_a_auditoria()
    {
        var banco = await pg.NovoBancoAsync();
        var relogio = NovoRelogio();
        await using var api = new ApiDeTeste(banco, relogio: relogio);
        var (ana, _) = await CadastrarAsync(api, banco);
        var cliente = await api.ClienteLogadoAsync(ana);
        const string consulta = "SELECT count(*) FROM audit.eventos WHERE tabela = 'identidade.sessoes' AND acao = 'UPDATE'";

        for (var i = 0; i < 10; i++)
        {
            relogio.Agora += TimeSpan.FromMinutes(1);
            Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync(RotaEu)).StatusCode);
        }
        Assert.Equal(0L, await ContarAsync(banco, consulta));

        relogio.Agora += TimeSpan.FromHours(2);
        Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync(RotaEu)).StatusCode);
        Assert.Equal(1L, await ContarAsync(banco, consulta));
    }

    [Fact]
    public async Task Post_sem_cabecalho_anticsrf_responde_403()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco);
        var (ana, _) = await CadastrarAsync(api, banco);
        var cliente = await api.ClienteLogadoAsync(ana);
        cliente.DefaultRequestHeaders.Remove("X-Requested-With");

        Assert.Equal(HttpStatusCode.Forbidden, (await cliente.PostAsync(RotaSair, null)).StatusCode);
        // A sessão continua de pé: o POST barrado não fez nada.
        Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync(RotaEu)).StatusCode);
    }

    [Fact]
    public async Task Get_sem_cabecalho_passa()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco);
        var (ana, _) = await CadastrarAsync(api, banco);
        var cliente = await api.ClienteLogadoAsync(ana);
        cliente.DefaultRequestHeaders.Remove("X-Requested-With");

        Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync(RotaEu)).StatusCode);
    }

    [Fact]
    public async Task Token_nunca_e_gravado_em_claro()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco);
        var (ana, _) = await CadastrarAsync(api, banco);
        string token;
        await using (var escopo = api.Services.CreateAsyncScope())
        {
            var db = escopo.ServiceProvider.GetRequiredService<GestorFinanceiro.Modules.Identidade.Infra.IdentidadeDbContext>();
            var usuario = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstAsync(
                Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.IgnoreQueryFilters(db.Usuarios, [GestorFinanceiro.SharedKernel.Persistencia.Filtros.Casal]),
                u => u.Id == ana);
            token = (await escopo.ServiceProvider.GetRequiredService<ServicoDeSessoes>().CriarAsync(usuario, CancellationToken.None)).Token;
        }

        await using var conexao = new NpgsqlConnection(banco.ConexaoSuperusuario);
        await conexao.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT token_hmac FROM identidade.sessoes", conexao);
        var hmac = (byte[])(await cmd.ExecuteScalarAsync())!;
        Assert.NotEqual(token, System.Text.Encoding.UTF8.GetString(hmac));
        Assert.NotEqual(Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/') + "="), hmac);

        await using var cmdAud = new NpgsqlCommand("SELECT alteracoes::text FROM audit.eventos WHERE tabela = 'identidade.sessoes' AND acao = 'INSERT'", conexao);
        var json = (string)(await cmdAud.ExecuteScalarAsync())!;
        Assert.Equal("alterado", JsonDocument.Parse(json).RootElement.GetProperty("token_hmac").GetString());
        Assert.DoesNotContain(token, json);
    }

    [Fact]
    public async Task Confirmacao_com_mais_de_5_minutos_nao_e_recente()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco);
        var (ana, _) = await CadastrarAsync(api, banco);
        var cliente = await api.ClienteLogadoAsync(ana, confirmadaEm: DateTimeOffset.UtcNow.AddMinutes(-6));

        var eu = (await cliente.GetFromJsonAsync<EuDto>(RotaEu))!;

        Assert.False(eu.ConfirmacaoRecente);
    }
}
