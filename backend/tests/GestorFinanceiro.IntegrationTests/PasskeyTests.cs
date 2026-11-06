using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using GestorFinanceiro.IntegrationTests.Infra;
using GestorFinanceiro.Modules.Identidade.Aplicacao;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.UnitTests;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GestorFinanceiro.IntegrationTests;

/// <summary>
/// Passkey ponta a ponta com um autenticador de software. Os corpos vão e voltam no JSON padrão do WebAuthn,
/// sem depender dos tipos C# do contrato, para testar o formato que o navegador realmente usa.
/// </summary>
[Collection("postgres")]
public class PasskeyTests(PostgresFixture pg)
{
    private const string SenhaDaAna = "senha-da-ana-123";
    private static readonly string CpfDaAna = CpfSintetico.Gerar(1);
    private const string MensagemFalha = "Não foi possível entrar. Confira os dados e tente de novo.";
    private const string SqlFalhas = "SELECT falhas_seguidas FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'";

    private static RelogioFixo NovoRelogio() => new() { Agora = new DateTimeOffset(2026, 11, 5, 12, 0, 10, TimeSpan.Zero) };

    private static async Task CadastrarAsync(ApiDeTeste api)
    {
        var pedido = new CadastroDoCasalDto(
            api.CodigoInstalacao, "Família Teste",
            new ConjugeNovoDto("Ana Teste", CpfDaAna, SenhaDaAna),
            new ConjugeNovoDto("Bruno Teste", CpfSintetico.Gerar(2), "senha-do-bruno-1"));
        Assert.Equal(HttpStatusCode.Created, (await api.CreateClient().PostAsJsonAsync("/api/v1/casal", pedido)).StatusCode);
    }

    private static HttpClient ClienteSemCookies(ApiDeTeste api, string? cookie = null)
    {
        var c = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = api.ClientOptions.BaseAddress });
        if (cookie is not null) c.DefaultRequestHeaders.Add("Cookie", cookie);
        return c;
    }

    /// <summary>Cliente com o cookie de registro obtido pelo primeiro acesso da Ana.</summary>
    private static async Task<HttpClient> ComRegistroAsync(ApiDeTeste api)
    {
        var resposta = await api.CreateClient().PostAsJsonAsync("/api/v1/auth/primeiro-acesso", new PrimeiroAcessoDto(CpfDaAna, SenhaDaAna));
        Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);
        return ClienteSemCookies(api, resposta.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
    }

    /// <summary>Autorização de registro emitida direto (o primeiro acesso já fechou), como faria a aprovação pelo cônjuge.</summary>
    private static async Task<HttpClient> ComNovaAutorizacaoAsync(ApiDeTeste api, BancoDeTeste banco)
    {
        var usuarioId = await EscalarAsync<Guid>(banco, "SELECT id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'");
        var casalId = await EscalarAsync<Guid>(banco, "SELECT casal_id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'");
        await using var escopo = api.Services.CreateAsyncScope();
        var token = escopo.ServiceProvider.GetRequiredService<AutorizacoesDeRegistro>()
            .Emitir(usuarioId, casalId, OrigemDoRegistro.AprovacaoDoConjuge);
        return ClienteSemCookies(api, $"{NomesDoLogin.CookieDeRegistro}={token}");
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<JsonElement> OpcoesDeRegistroAsync(HttpClient cliente)
    {
        var r = await cliente.PostAsync("/api/v1/auth/passkey/registro/opcoes", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        return await JsonAsync(r);
    }

    private static Task<HttpResponseMessage> RegistrarAsync(HttpClient cliente, JsonObject resposta, string apelido = "Meu celular") =>
        cliente.PostAsJsonAsync("/api/v1/auth/passkey/registro", new JsonObject { ["apelido"] = apelido, ["resposta"] = resposta });

    private static async Task<JsonElement> OpcoesDeLoginAsync(HttpClient cliente)
    {
        var r = await cliente.PostAsync("/api/v1/auth/passkey/opcoes", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        return await JsonAsync(r);
    }

    private static Task<HttpResponseMessage> EntrarAsync(HttpClient cliente, JsonObject assercao) =>
        cliente.PostAsJsonAsync("/api/v1/auth/passkey", assercao);

    /// <summary>Faz o primeiro acesso da Ana e registra a passkey do autenticador.</summary>
    private static async Task ConfigurarAsync(ApiDeTeste api, AutenticadorDeTeste autenticador)
    {
        var cliente = await ComRegistroAsync(api);
        var resposta = await RegistrarAsync(cliente, autenticador.Registrar(await OpcoesDeRegistroAsync(cliente)));
        Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);
    }

    private static async Task<T> EscalarAsync<T>(BancoDeTeste banco, string sql)
    {
        await using var db = new NpgsqlConnection(banco.ConexaoSuperusuario);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, db);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<string> TituloAsync(HttpResponseMessage r) =>
        (await JsonAsync(r)).GetProperty("title").GetString()!;

    [Fact]
    public async Task Registro_de_passkey_cria_credencial_e_sessao()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: NovoRelogio());
        await CadastrarAsync(api);
        using var autenticador = new AutenticadorDeTeste();
        var cliente = await ComRegistroAsync(api);

        var opcoes = await OpcoesDeRegistroAsync(cliente);
        Assert.Equal("localhost", opcoes.GetProperty("rp").GetProperty("id").GetString());
        Assert.Equal("Gestor Financeiro", opcoes.GetProperty("rp").GetProperty("name").GetString());
        Assert.Equal("required", opcoes.GetProperty("authenticatorSelection").GetProperty("residentKey").GetString());
        Assert.Equal("required", opcoes.GetProperty("authenticatorSelection").GetProperty("userVerification").GetString());
        Assert.Equal("none", opcoes.GetProperty("attestation").GetString());
        Assert.Empty(opcoes.GetProperty("excludeCredentials").EnumerateArray());
        Assert.Contains(opcoes.GetProperty("pubKeyCredParams").EnumerateArray(), p => p.GetProperty("alg").GetInt32() == -7);

        var resposta = await RegistrarAsync(cliente, autenticador.Registrar(opcoes));

        Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);
        Assert.Equal("no-store", resposta.Headers.CacheControl?.ToString());
        var setCookie = resposta.Headers.GetValues("Set-Cookie").Single();
        Assert.StartsWith("__Host-gf_sessao=", setCookie);
        var logado = ClienteSemCookies(api, setCookie.Split(';')[0]);
        Assert.Equal(HttpStatusCode.OK, (await logado.GetAsync("/api/v1/auth/eu")).StatusCode);

        var idHex = Convert.ToHexString(autenticador.CredentialId);
        Assert.Equal(1L, await EscalarAsync<long>(banco,
            $"SELECT count(*) FROM identidade.credenciais WHERE tipo = 'PASSKEY' AND confirmada AND contador = 0 AND apelido = 'Meu celular' AND credential_id = '\\x{idHex}'::bytea"));
        Assert.True(await EscalarAsync<bool>(banco,
            "SELECT primeiro_acesso_encerrado_em IS NOT NULL FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'"));

        // A autorização de registro foi consumida: não registra uma segunda credencial.
        Assert.Equal(HttpStatusCode.Unauthorized, (await cliente.PostAsync("/api/v1/auth/passkey/registro/opcoes", null)).StatusCode);
        using var outro = new AutenticadorDeTeste();
        Assert.Equal(HttpStatusCode.Unauthorized, (await RegistrarAsync(cliente, outro.Registrar(opcoes))).StatusCode);
        Assert.Equal(1L, await EscalarAsync<long>(banco, "SELECT count(*) FROM identidade.credenciais"));
    }

    [Fact]
    public async Task Registro_sem_autorizacao_responde_401()
    {
        await using var api = new ApiDeTeste(await pg.NovoBancoAsync());
        var resposta = await ClienteSemCookies(api).PostAsync("/api/v1/auth/passkey/registro/opcoes", null);
        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    [Fact]
    public async Task Login_com_passkey_cria_sessao_e_atualiza_contador()
    {
        var relogio = NovoRelogio();
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: relogio);
        await CadastrarAsync(api);
        using var autenticador = new AutenticadorDeTeste { Contador = 5 };
        await ConfigurarAsync(api, autenticador);
        var anonimo = ClienteSemCookies(api);

        var opcoes = await OpcoesDeLoginAsync(anonimo);
        Assert.Equal("required", opcoes.GetProperty("userVerification").GetString());
        Assert.Equal("localhost", opcoes.GetProperty("rpId").GetString());
        Assert.False(opcoes.TryGetProperty("allowCredentials", out var permitidas) && permitidas.GetArrayLength() > 0);

        var resposta = await EntrarAsync(anonimo, autenticador.Autenticar(opcoes));

        Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);
        Assert.Equal("no-store", resposta.Headers.CacheControl?.ToString());
        var setCookie = resposta.Headers.GetValues("Set-Cookie").Single();
        Assert.StartsWith("__Host-gf_sessao=", setCookie);
        Assert.Equal(HttpStatusCode.OK, (await ClienteSemCookies(api, setCookie.Split(';')[0]).GetAsync("/api/v1/auth/eu")).StatusCode);
        Assert.Equal(6L, await EscalarAsync<long>(banco, "SELECT contador FROM identidade.credenciais"));
        Assert.True(await EscalarAsync<bool>(banco, "SELECT ultimo_uso_em IS NOT NULL FROM identidade.credenciais"));

        // Contador que não cresce (clone do autenticador): rejeitado, sem contar falha (passkey fica fora do bloqueio).
        autenticador.Contador = 5;
        var clonado = await EntrarAsync(anonimo, autenticador.Autenticar(await OpcoesDeLoginAsync(anonimo)));
        Assert.Equal(HttpStatusCode.Unauthorized, clonado.StatusCode);
        Assert.Equal(0, await EscalarAsync<int>(banco, SqlFalhas));
        Assert.Equal(6L, await EscalarAsync<long>(banco, "SELECT contador FROM identidade.credenciais"));
    }

    [Fact]
    public async Task Falha_de_passkey_nao_conta_para_o_bloqueio()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: NovoRelogio());
        await CadastrarAsync(api);
        using var autenticador = new AutenticadorDeTeste();
        await ConfigurarAsync(api, autenticador);
        var anonimo = ClienteSemCookies(api);

        // Mais asserções inválidas do que o limite de falhas: nenhuma conta, nenhuma bloqueia.
        for (var i = 0; i < 6; i++)
        {
            var resposta = await EntrarAsync(anonimo, autenticador.Autenticar(await OpcoesDeLoginAsync(anonimo), assinaturaValida: false));
            Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
            Assert.Equal(MensagemFalha, await TituloAsync(resposta));
        }

        Assert.Equal(0, await EscalarAsync<int>(banco, SqlFalhas));
        Assert.True(await EscalarAsync<bool>(banco, "SELECT bloqueado_ate IS NULL FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'"));

        // Credencial desconhecida: mesma resposta, nada contado.
        using var estranho = new AutenticadorDeTeste { UserHandle = autenticador.UserHandle };
        var desconhecida = await EntrarAsync(anonimo, estranho.Autenticar(await OpcoesDeLoginAsync(anonimo)));
        Assert.Equal(HttpStatusCode.Unauthorized, desconhecida.StatusCode);
        Assert.Equal(MensagemFalha, await TituloAsync(desconhecida));
        Assert.Equal(0, await EscalarAsync<int>(banco, SqlFalhas));
    }

    [Fact]
    public async Task Desafio_reusado_e_rejeitado()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: NovoRelogio());
        await CadastrarAsync(api);
        // Sem contador (sempre 0): só o desafio de uso único impede o replay.
        using var autenticador = new AutenticadorDeTeste { ContadorFixoEmZero = true };
        await ConfigurarAsync(api, autenticador);
        var anonimo = ClienteSemCookies(api);

        var assercao = autenticador.Autenticar(await OpcoesDeLoginAsync(anonimo));
        Assert.Equal(HttpStatusCode.NoContent, (await EntrarAsync(anonimo, assercao)).StatusCode);
        var replay = await EntrarAsync(anonimo, (JsonObject)assercao.DeepClone());
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal(MensagemFalha, await TituloAsync(replay));

        // Também some depois de uma falha: a mesma conferência, agora certa, não reaproveita o desafio.
        var opcoes = await OpcoesDeLoginAsync(anonimo);
        Assert.Equal(HttpStatusCode.Unauthorized, (await EntrarAsync(anonimo, autenticador.Autenticar(opcoes, assinaturaValida: false))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await EntrarAsync(anonimo, autenticador.Autenticar(opcoes))).StatusCode);

        // No registro, o desafio também é de uso único (e um desafio inventado não serve).
        var cliente = await ComNovaAutorizacaoAsync(api, banco);
        using var segundo = new AutenticadorDeTeste();
        var opcoesDeRegistro = await OpcoesDeRegistroAsync(cliente);
        var falsificadas = JsonDocument.Parse(
            opcoesDeRegistro.GetRawText().Replace(opcoesDeRegistro.GetProperty("challenge").GetString()!, Base64Url.EncodeToString(new byte[32]))).RootElement;
        Assert.Equal(HttpStatusCode.Unauthorized, (await RegistrarAsync(cliente, segundo.Registrar(falsificadas))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RegistrarAsync(cliente, segundo.Registrar(opcoesDeRegistro))).StatusCode);
        Assert.Equal(1L, await EscalarAsync<long>(banco, "SELECT count(*) FROM identidade.credenciais"));
    }

    [Fact]
    public async Task Origem_errada_e_rejeitada()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: NovoRelogio());
        await CadastrarAsync(api);

        // No registro: nada é gravado e o primeiro acesso continua aberto.
        using var enganado = new AutenticadorDeTeste { Origem = "https://localhost.evil.example" };
        var cliente = await ComRegistroAsync(api);
        var resposta = await RegistrarAsync(cliente, enganado.Registrar(await OpcoesDeRegistroAsync(cliente)));
        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        Assert.Equal(0L, await EscalarAsync<long>(banco, "SELECT count(*) FROM identidade.credenciais"));
        Assert.True(await EscalarAsync<bool>(banco,
            "SELECT primeiro_acesso_encerrado_em IS NULL FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'"));

        // No login: rejeitada, sem contar falha (passkey fica fora do bloqueio).
        using var autenticador = new AutenticadorDeTeste();
        await ConfigurarAsync(api, autenticador);
        autenticador.Origem = "https://localhost.evil.example";
        var anonimo = ClienteSemCookies(api);
        var login = await EntrarAsync(anonimo, autenticador.Autenticar(await OpcoesDeLoginAsync(anonimo)));
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal(0, await EscalarAsync<int>(banco, SqlFalhas));
    }

    [Fact]
    public async Task Passkey_de_credencial_removida_nao_entra()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: NovoRelogio());
        await CadastrarAsync(api);
        using var autenticador = new AutenticadorDeTeste();
        await ConfigurarAsync(api, autenticador);
        await EscalarAsync<int>(banco,
            "WITH c AS (UPDATE identidade.credenciais SET is_deleted = true, deleted_at = now() RETURNING 1) SELECT count(*)::int FROM c");
        var anonimo = ClienteSemCookies(api);

        var resposta = await EntrarAsync(anonimo, autenticador.Autenticar(await OpcoesDeLoginAsync(anonimo)));

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        Assert.Equal(MensagemFalha, await TituloAsync(resposta));
        Assert.Equal(0, await EscalarAsync<int>(banco, SqlFalhas));
    }

    [Fact]
    public async Task Nome_da_passkey_e_o_apelido_nunca_o_cpf()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: NovoRelogio());
        await CadastrarAsync(api);
        var cliente = await ComRegistroAsync(api);

        var r = await cliente.PostAsync("/api/v1/auth/passkey/registro/opcoes", null);
        var corpo = await r.Content.ReadAsStringAsync();
        var usuario = JsonDocument.Parse(corpo).RootElement.GetProperty("user");

        Assert.Equal("Ana", usuario.GetProperty("name").GetString());
        Assert.Equal("Ana", usuario.GetProperty("displayName").GetString());
        var usuarioId = await EscalarAsync<Guid>(banco, "SELECT id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'");
        Assert.Equal(usuarioId.ToByteArray(), Base64Url.DecodeFromChars(usuario.GetProperty("id").GetString()));
        Assert.DoesNotContain(CpfDaAna, corpo);
        Assert.DoesNotContain(CpfDaAna[..9], corpo);
        Assert.DoesNotContain("Teste", corpo);
    }

    [Fact]
    public async Task Exclui_as_passkeys_ativas_do_usuario()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: NovoRelogio());
        await CadastrarAsync(api);
        using var autenticador = new AutenticadorDeTeste();
        await ConfigurarAsync(api, autenticador);

        // Outra autorização de registro para a Ana (como faria a aprovação pelo cônjuge).
        var cliente = await ComNovaAutorizacaoAsync(api, banco);

        var opcoes = await OpcoesDeRegistroAsync(cliente);
        var excluidas = opcoes.GetProperty("excludeCredentials").EnumerateArray().ToList();
        Assert.Single(excluidas);
        Assert.Equal(autenticador.IdBase64Url, excluidas[0].GetProperty("id").GetString());
        Assert.Equal("public-key", excluidas[0].GetProperty("type").GetString());

        // A mesma credencial não pode ser registrada de novo.
        Assert.Equal(HttpStatusCode.Unauthorized, (await RegistrarAsync(cliente, autenticador.Registrar(opcoes))).StatusCode);
        Assert.Equal(1L, await EscalarAsync<long>(banco, "SELECT count(*) FROM identidade.credenciais"));
    }

    [Fact]
    public async Task Resposta_malformada_responde_401_sem_erro_interno()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: NovoRelogio());
        await CadastrarAsync(api);

        // Registro com attestationObject que não é CBOR válido.
        using var autenticador = new AutenticadorDeTeste();
        var cliente = await ComRegistroAsync(api);
        var registro = autenticador.Registrar(await OpcoesDeRegistroAsync(cliente));
        registro["response"]!["attestationObject"] = Base64Url.EncodeToString([0xA3, 0x01, 0x02]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RegistrarAsync(cliente, registro)).StatusCode);

        // Login com authenticatorData truncado e com base64url inválido.
        await ConfigurarAsync(api, autenticador);
        var anonimo = ClienteSemCookies(api);
        var truncada = autenticador.Autenticar(await OpcoesDeLoginAsync(anonimo));
        truncada["response"]!["authenticatorData"] = Base64Url.EncodeToString([1, 2, 3]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await EntrarAsync(anonimo, truncada)).StatusCode);
        var invalida = autenticador.Autenticar(await OpcoesDeLoginAsync(anonimo));
        invalida["response"]!["signature"] = "@@@";
        Assert.Equal(HttpStatusCode.Unauthorized, (await EntrarAsync(anonimo, invalida)).StatusCode);
    }
}
