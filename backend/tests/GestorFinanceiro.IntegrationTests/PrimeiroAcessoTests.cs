using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GestorFinanceiro.IntegrationTests.Infra;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.UnitTests;
using Npgsql;

namespace GestorFinanceiro.IntegrationTests;

[Collection("postgres")]
public class PrimeiroAcessoTests(PostgresFixture pg)
{
    private const string Rota = "/api/v1/auth/primeiro-acesso";
    private const string SenhaDaAna = "senha-da-ana-123";
    private static readonly string CpfDaAna = CpfSintetico.Gerar(1);
    private const string MensagemFalha = "Não foi possível entrar. Confira os dados e tente de novo.";
    private const string MensagemBloqueio = "Conta bloqueada por excesso de tentativas. Tente mais tarde ou peça ao outro cônjuge para destravar.";
    private const string MensagemJaConfigurado = "Este acesso já foi configurado. Entre com sua passkey ou TOTP.";

    private static async Task CadastrarAsync(ApiDeTeste api)
    {
        var pedido = new CadastroDoCasalDto(
            api.CodigoInstalacao, "Família Teste",
            new ConjugeNovoDto("Ana Teste", CpfDaAna, SenhaDaAna),
            new ConjugeNovoDto("Bruno Teste", CpfSintetico.Gerar(2), "senha-do-bruno-1"));
        var resposta = await api.CreateClient().PostAsJsonAsync("/api/v1/casal", pedido);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
    }

    private static Task<HttpResponseMessage> EntrarAsync(HttpClient cliente, string cpf, string senha) =>
        cliente.PostAsJsonAsync(Rota, new PrimeiroAcessoDto(cpf, senha));

    private static async Task<T> EscalarAsync<T>(BancoDeTeste banco, string sql)
    {
        await using var db = new NpgsqlConnection(banco.ConexaoSuperusuario);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, db);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<(string Titulo, int Status)> ProblemaAsync(HttpResponseMessage r)
    {
        var json = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        return (json.GetProperty("title").GetString()!, json.GetProperty("status").GetInt32());
    }

    private static string SqlFalhas =>
        $"SELECT falhas_seguidas FROM identidade.usuarios WHERE cpf_hmac IS NOT NULL ORDER BY nome_completo LIMIT 1";

    [Fact]
    public async Task Senha_certa_emite_autorizacao_de_registro()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco);
        await CadastrarAsync(api);

        var resposta = await EntrarAsync(api.CreateClient(), CpfDaAna, SenhaDaAna);

        Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);
        var cookie = resposta.Headers.GetValues("Set-Cookie").Single();
        Assert.StartsWith("__Host-gf_registro=", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max-age=600", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Senha_errada_responde_401_e_conta_falha()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco);
        await CadastrarAsync(api);

        var resposta = await EntrarAsync(api.CreateClient(), CpfDaAna, "senha-errada-xyz");

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        Assert.Equal((MensagemFalha, 401), await ProblemaAsync(resposta));
        Assert.False(resposta.Headers.Contains("Set-Cookie"));
        Assert.Equal(1, await EscalarAsync<int>(banco, SqlFalhas));
    }

    [Fact]
    public async Task Cpf_inexistente_responde_igual_a_senha_errada()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco);
        await CadastrarAsync(api);
        var cliente = api.CreateClient();

        var errada = await EntrarAsync(cliente, CpfDaAna, "senha-errada-xyz");
        var inexistente = await EntrarAsync(cliente, CpfSintetico.Gerar(9), "senha-errada-xyz");

        Assert.Equal(errada.StatusCode, inexistente.StatusCode);
        Assert.Equal(await ProblemaAsync(errada), await ProblemaAsync(inexistente));
        Assert.False(inexistente.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Cinco_senhas_erradas_bloqueiam_e_a_sexta_responde_423_mesmo_certa()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco);
        await CadastrarAsync(api);
        var cliente = api.CreateClient();

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await EntrarAsync(cliente, CpfDaAna, "senha-errada-xyz")).StatusCode);

        var sexta = await EntrarAsync(cliente, CpfDaAna, SenhaDaAna);

        Assert.Equal(HttpStatusCode.Locked, sexta.StatusCode);
        Assert.Equal((MensagemBloqueio, 423), await ProblemaAsync(sexta));
        Assert.False(sexta.Headers.Contains("Set-Cookie"));
        // Bloqueado não conta outra falha.
        Assert.Equal(0, await EscalarAsync<int>(banco, SqlFalhas));
        Assert.Equal(1, await EscalarAsync<int>(banco, "SELECT bloqueios FROM identidade.usuarios ORDER BY nome_completo LIMIT 1"));
    }

    [Fact]
    public async Task Primeiro_acesso_encerrado_recebe_403_sem_testar_a_senha()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco);
        await CadastrarAsync(api);
        await using (var db = new NpgsqlConnection(banco.ConexaoSuperusuario))
        {
            await db.OpenAsync();
            await using var cmd = new NpgsqlCommand("UPDATE identidade.usuarios SET primeiro_acesso_encerrado_em = now() WHERE nome_completo = 'Ana Teste'", db);
            await cmd.ExecuteNonQueryAsync();
        }

        var resposta = await EntrarAsync(api.CreateClient(), CpfDaAna, "senha-errada-xyz");

        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
        Assert.Equal((MensagemJaConfigurado, 403), await ProblemaAsync(resposta));
        Assert.Equal(0, await EscalarAsync<int>(banco, SqlFalhas));
        // Nem com a senha certa há cookie.
        var certa = await EntrarAsync(api.CreateClient(), CpfDaAna, SenhaDaAna);
        Assert.Equal(HttpStatusCode.Forbidden, certa.StatusCode);
        Assert.False(certa.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Corpo_de_erro_nao_ecoa_cpf_nem_senha()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco);
        await CadastrarAsync(api);
        const string senha = "senha-errada-xyz";

        var resposta = await EntrarAsync(api.CreateClient(), CpfDaAna, senha);
        var corpo = await resposta.Content.ReadAsStringAsync();

        Assert.DoesNotContain(CpfDaAna, corpo);
        Assert.DoesNotContain(senha, corpo);
        Assert.Equal("PrimeiroAcessoDto", new PrimeiroAcessoDto(CpfDaAna, senha).ToString());
    }
}
