using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GestorFinanceiro.IntegrationTests.Infra;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.Modules.Identidade.Dominio;
using GestorFinanceiro.SharedKernel.Seguranca;
using GestorFinanceiro.UnitTests;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OtpNet;

namespace GestorFinanceiro.IntegrationTests;

[Collection("postgres")]
public class TotpTests(PostgresFixture pg)
{
    private const string SenhaDaAna = "senha-da-ana-123";
    private static readonly string CpfDaAna = CpfSintetico.Gerar(1);
    private const string MensagemFalha = "Não foi possível entrar. Confira os dados e tente de novo.";

    private static RelogioFixo NovoRelogio() => new() { Agora = new DateTimeOffset(2026, 11, 5, 12, 0, 10, TimeSpan.Zero) };

    private static async Task CadastrarAsync(ApiDeTeste api)
    {
        var pedido = new CadastroDoCasalDto(
            api.CodigoInstalacao, "Família Teste",
            new ConjugeNovoDto("Ana Teste", CpfDaAna, SenhaDaAna),
            new ConjugeNovoDto("Bruno Teste", CpfSintetico.Gerar(2), "senha-do-bruno-1"));
        Assert.Equal(HttpStatusCode.Created, (await api.CreateClient().PostAsJsonAsync("/api/v1/casal", pedido)).StatusCode);
    }

    /// <summary>Cliente sem container de cookies, com o cookie de registro obtido pelo primeiro acesso.</summary>
    private static async Task<(HttpClient Cliente, string Cookie)> ComRegistroAsync(ApiDeTeste api)
    {
        var resposta = await api.CreateClient().PostAsJsonAsync("/api/v1/auth/primeiro-acesso", new PrimeiroAcessoDto(CpfDaAna, SenhaDaAna));
        Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);
        var cookie = resposta.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        return (ClienteSemCookies(api, cookie), cookie);
    }

    private static HttpClient ClienteSemCookies(ApiDeTeste api, string? cookie = null)
    {
        var c = api.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        { HandleCookies = false, BaseAddress = api.ClientOptions.BaseAddress });
        if (cookie is not null) c.DefaultRequestHeaders.Add("Cookie", cookie);
        return c;
    }

    private static async Task<(Guid Id, byte[] Segredo, string Uri)> RegistrarAsync(HttpClient cliente)
    {
        var resposta = await cliente.PostAsJsonAsync("/api/v1/auth/totp/registro", new NovoTotpDto("Meu celular"));
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("no-store", resposta.Headers.CacheControl?.ToString());
        var dto = (await resposta.Content.ReadFromJsonAsync<TotpGeradoDto>())!;
        var uri = new Uri(dto.OtpauthUri);
        var segredo = Base32Encoding.ToBytes(System.Web.HttpUtility.ParseQueryString(uri.Query)["secret"]!);
        return (dto.CredencialId, segredo, dto.OtpauthUri);
    }

    private static string Codigo(byte[] segredo, DateTimeOffset quando) =>
        new Totp(segredo).ComputeTotp(quando.UtcDateTime);

    private static Task<HttpResponseMessage> ConfirmarAsync(HttpClient c, Guid id, string codigo) =>
        c.PostAsJsonAsync("/api/v1/auth/totp/confirmacao", new ConfirmarTotpDto(id, codigo));

    private static Task<HttpResponseMessage> EntrarAsync(HttpClient c, string cpf, string codigo) =>
        c.PostAsJsonAsync("/api/v1/auth/totp", new EntrarComTotpDto(cpf, codigo));

    /// <summary>Registra e confirma um TOTP para a Ana; devolve o segredo.</summary>
    private static async Task<byte[]> ConfigurarAsync(ApiDeTeste api, RelogioFixo relogio)
    {
        var (cliente, _) = await ComRegistroAsync(api);
        var (id, segredo, _) = await RegistrarAsync(cliente);
        Assert.Equal(HttpStatusCode.NoContent, (await ConfirmarAsync(cliente, id, Codigo(segredo, relogio.Agora))).StatusCode);
        return segredo;
    }

    private static async Task<T> EscalarAsync<T>(BancoDeTeste banco, string sql)
    {
        await using var db = new NpgsqlConnection(banco.ConexaoSuperusuario);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, db);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<string> TituloAsync(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("title").GetString()!;

    private const string SqlFalhas = "SELECT falhas_seguidas FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'";

    [Fact]
    public async Task Registro_sem_autorizacao_responde_401()
    {
        await using var api = new ApiDeTeste(await pg.NovoBancoAsync());
        var resposta = await ClienteSemCookies(api).PostAsJsonAsync("/api/v1/auth/totp/registro", new NovoTotpDto("Meu celular"));
        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    [Fact]
    public async Task Registro_confirmado_cria_sessao_e_consome_a_autorizacao()
    {
        var relogio = NovoRelogio();
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: relogio);
        await CadastrarAsync(api);
        var (cliente, cookieDeRegistro) = await ComRegistroAsync(api);
        var (id, segredo, _) = await RegistrarAsync(cliente);

        var resposta = await ConfirmarAsync(cliente, id, Codigo(segredo, relogio.Agora));

        Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);
        var setCookie = resposta.Headers.GetValues("Set-Cookie").Single();
        Assert.StartsWith("__Host-gf_sessao=", setCookie);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain", setCookie, StringComparison.OrdinalIgnoreCase);
        var maxAge = int.Parse(System.Text.RegularExpressions.Regex.Match(setCookie, "max-age=(\\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(maxAge, 30 * 86400 - 60, 30 * 86400);

        // A sessão do Set-Cookie real funciona.
        var logado = ClienteSemCookies(api, setCookie.Split(';')[0]);
        Assert.Equal(HttpStatusCode.OK, (await logado.GetAsync("/api/v1/auth/eu")).StatusCode);

        // Segunda confirmação com o mesmo cookie de registro.
        var outra = ClienteSemCookies(api, cookieDeRegistro);
        var segunda = await ConfirmarAsync(outra, id, Codigo(segredo, relogio.Agora.AddSeconds(30)));
        Assert.Equal(HttpStatusCode.Unauthorized, segunda.StatusCode);
        var registroDeNovo = await outra.PostAsJsonAsync("/api/v1/auth/totp/registro", new NovoTotpDto("Outro"));
        Assert.Equal(HttpStatusCode.Unauthorized, registroDeNovo.StatusCode);
    }

    [Fact]
    public async Task Confirmar_o_totp_encerra_o_primeiro_acesso()
    {
        var relogio = NovoRelogio();
        await using var api = new ApiDeTeste(await pg.NovoBancoAsync(), relogio: relogio);
        await CadastrarAsync(api);
        await ConfigurarAsync(api, relogio);

        var resposta = await api.CreateClient().PostAsJsonAsync("/api/v1/auth/primeiro-acesso", new PrimeiroAcessoDto(CpfDaAna, SenhaDaAna));

        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
    }

    [Fact]
    public async Task Confirmacao_com_codigo_errado_responde_401_e_conta_falha()
    {
        var relogio = NovoRelogio();
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: relogio);
        await CadastrarAsync(api);
        var (cliente, _) = await ComRegistroAsync(api);
        var (id, segredo, _) = await RegistrarAsync(cliente);
        var errado = Codigo(segredo, relogio.Agora) == "000000" ? "111111" : "000000";

        var resposta = await ConfirmarAsync(cliente, id, errado);

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        Assert.False(resposta.Headers.Contains("Set-Cookie"));
        Assert.Equal(1, await EscalarAsync<int>(banco, SqlFalhas));
        Assert.True(await EscalarAsync<bool>(banco, "SELECT primeiro_acesso_encerrado_em IS NULL FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'"));
    }

    [Fact]
    public async Task Segredo_totp_so_existe_cifrado()
    {
        var relogio = NovoRelogio();
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: relogio);
        await CadastrarAsync(api);
        var (cliente, _) = await ComRegistroAsync(api);
        var (id, segredo, _) = await RegistrarAsync(cliente);
        var base32 = Base32Encoding.ToString(segredo);

        await using var conexao = new NpgsqlConnection(banco.ConexaoSuperusuario);
        await conexao.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT segredo_cifrado FROM identidade.credenciais WHERE id = @id", conexao);
        cmd.Parameters.AddWithValue("id", id);
        var cifrado = (byte[])(await cmd.ExecuteScalarAsync())!;

        var cofre = api.Services.GetRequiredService<ICofre>();
        Assert.Equal(base32, cofre.Decifrar(cifrado, Cofre.Aad(Credencial.Tabela, "segredo_cifrado", id)));

        var linha = await EscalarAsync<string>(banco, $"SELECT row_to_json(c)::text FROM identidade.credenciais c WHERE id = '{id}'");
        Assert.DoesNotContain(base32, linha);
        Assert.DoesNotContain(Convert.ToHexString(segredo), linha, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(segredo), linha);
        Assert.DoesNotContain(base32, Convert.ToHexString(cifrado));
    }

    [Fact]
    public async Task Totp_nao_confirmado_nao_serve_para_entrar()
    {
        var relogio = NovoRelogio();
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: relogio);
        await CadastrarAsync(api);
        var (cliente, _) = await ComRegistroAsync(api);
        var (_, segredo, _) = await RegistrarAsync(cliente);

        var resposta = await EntrarAsync(ClienteSemCookies(api), CpfDaAna, Codigo(segredo, relogio.Agora));

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        Assert.Equal(MensagemFalha, await TituloAsync(resposta));
        Assert.False(resposta.Headers.Contains("Set-Cookie"));
        Assert.Equal(0, await EscalarAsync<int>(banco, SqlFalhas));
    }

    [Fact]
    public async Task Cpf_desconhecido_responde_igual_a_codigo_errado()
    {
        var relogio = NovoRelogio();
        await using var api = new ApiDeTeste(await pg.NovoBancoAsync(), relogio: relogio);
        await CadastrarAsync(api);
        await ConfigurarAsync(api, relogio);
        var cliente = ClienteSemCookies(api);

        var errado = await EntrarAsync(cliente, CpfDaAna, "000000");
        var desconhecido = await EntrarAsync(cliente, CpfSintetico.Gerar(9), "000000");

        Assert.Equal(errado.StatusCode, desconhecido.StatusCode);
        Assert.Equal(await TituloAsync(errado), await TituloAsync(desconhecido));
    }

    [Fact]
    public async Task Entrar_com_codigo_certo_cria_sessao()
    {
        var relogio = NovoRelogio();
        await using var api = new ApiDeTeste(await pg.NovoBancoAsync(), relogio: relogio);
        await CadastrarAsync(api);
        var segredo = await ConfigurarAsync(api, relogio);
        relogio.Agora = relogio.Agora.AddSeconds(60);

        var resposta = await EntrarAsync(ClienteSemCookies(api), CpfDaAna, Codigo(segredo, relogio.Agora));

        Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);
        var cookie = resposta.Headers.GetValues("Set-Cookie").Single();
        Assert.StartsWith("__Host-gf_sessao=", cookie);
        var logado = ClienteSemCookies(api, cookie.Split(';')[0]);
        Assert.Equal(HttpStatusCode.OK, (await logado.GetAsync("/api/v1/auth/eu")).StatusCode);
    }

    [Fact]
    public async Task Codigo_totp_reusado_e_rejeitado()
    {
        var relogio = NovoRelogio();
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: relogio);
        await CadastrarAsync(api);
        var segredo = await ConfigurarAsync(api, relogio);
        relogio.Agora = relogio.Agora.AddSeconds(60);
        var codigo = Codigo(segredo, relogio.Agora);
        var cliente = ClienteSemCookies(api);

        Assert.Equal(HttpStatusCode.NoContent, (await EntrarAsync(cliente, CpfDaAna, codigo)).StatusCode);
        relogio.Agora = relogio.Agora.AddSeconds(5);
        var segundo = await EntrarAsync(cliente, CpfDaAna, codigo);

        Assert.Equal(HttpStatusCode.Unauthorized, segundo.StatusCode);
        Assert.Equal(1, await EscalarAsync<int>(banco, SqlFalhas));
    }

    [Fact]
    public async Task Codigo_do_passo_anterior_ainda_vale_uma_vez()
    {
        var relogio = NovoRelogio();
        await using var api = new ApiDeTeste(await pg.NovoBancoAsync(), relogio: relogio);
        await CadastrarAsync(api);
        var segredo = await ConfigurarAsync(api, relogio);
        relogio.Agora = relogio.Agora.AddSeconds(90);
        var anterior = Codigo(segredo, relogio.Agora.AddSeconds(-30));
        var cliente = ClienteSemCookies(api);

        Assert.Equal(HttpStatusCode.NoContent, (await EntrarAsync(cliente, CpfDaAna, anterior)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await EntrarAsync(cliente, CpfDaAna, anterior)).StatusCode);
    }

    [Fact]
    public async Task Codigo_de_dois_passos_atras_e_rejeitado()
    {
        var relogio = NovoRelogio();
        await using var api = new ApiDeTeste(await pg.NovoBancoAsync(), relogio: relogio);
        await CadastrarAsync(api);
        var segredo = await ConfigurarAsync(api, relogio);
        relogio.Agora = relogio.Agora.AddSeconds(120);
        var velho = Codigo(segredo, relogio.Agora.AddSeconds(-60));

        Assert.Equal(HttpStatusCode.Unauthorized, (await EntrarAsync(ClienteSemCookies(api), CpfDaAna, velho)).StatusCode);
    }

    [Fact]
    public async Task Mesmo_codigo_em_paralelo_so_entra_uma_vez()
    {
        var relogio = NovoRelogio();
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: relogio);
        await CadastrarAsync(api);
        var segredo = await ConfigurarAsync(api, relogio);
        relogio.Agora = relogio.Agora.AddSeconds(60);
        var codigo = Codigo(segredo, relogio.Agora);
        var cliente = ClienteSemCookies(api);

        var respostas = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => EntrarAsync(cliente, CpfDaAna, codigo))));

        Assert.Equal(1, respostas.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(1, respostas.Count(r => r.StatusCode == HttpStatusCode.Unauthorized));
    }

    [Fact]
    public async Task Cinco_codigos_errados_bloqueiam_e_a_sexta_responde_423()
    {
        var relogio = NovoRelogio();
        await using var api = new ApiDeTeste(await pg.NovoBancoAsync(), relogio: relogio);
        await CadastrarAsync(api);
        var segredo = await ConfigurarAsync(api, relogio);
        relogio.Agora = relogio.Agora.AddSeconds(60);
        var cliente = ClienteSemCookies(api);

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await EntrarAsync(cliente, CpfDaAna, "abcdef")).StatusCode);
        var sexta = await EntrarAsync(cliente, CpfDaAna, Codigo(segredo, relogio.Agora));

        Assert.Equal(HttpStatusCode.Locked, sexta.StatusCode);
        Assert.False(sexta.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Segredo_totp_nao_aparece_na_auditoria_nem_em_claro_no_banco()
    {
        var relogio = NovoRelogio();
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: relogio);
        await CadastrarAsync(api);
        var (cliente, _) = await ComRegistroAsync(api);
        var (id, segredo, _) = await RegistrarAsync(cliente);
        Assert.Equal(HttpStatusCode.NoContent, (await ConfirmarAsync(cliente, id, Codigo(segredo, relogio.Agora))).StatusCode);
        var base32 = Base32Encoding.ToString(segredo);

        await using var conexao = new NpgsqlConnection(banco.ConexaoSuperusuario);
        await conexao.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT alteracoes::text FROM audit.eventos WHERE tabela = 'identidade.credenciais' UNION ALL SELECT row_to_json(t)::text FROM identidade.credenciais t", conexao);
        var textos = new List<string>();
        await using (var leitor = await cmd.ExecuteReaderAsync())
            while (await leitor.ReadAsync()) textos.Add(leitor.GetString(0));

        Assert.True(textos.Count >= 2);
        Assert.All(textos, t =>
        {
            Assert.DoesNotContain(base32, t);
            Assert.DoesNotContain(Convert.ToHexString(segredo), t, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Convert.ToBase64String(segredo), t);
        });
    }

    [Fact]
    public async Task Otpauth_uri_nao_contem_cpf()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco);
        await CadastrarAsync(api);
        var (cliente, _) = await ComRegistroAsync(api);

        var (_, _, uri) = await RegistrarAsync(cliente);

        Assert.DoesNotContain(CpfDaAna, uri);
        Assert.StartsWith("otpauth://totp/Gestor%20Financeiro:Ana?secret=", uri);
        Assert.EndsWith("&issuer=Gestor%20Financeiro&digits=6&period=30", uri);
        Assert.DoesNotContain("Meu", uri);
    }
}
