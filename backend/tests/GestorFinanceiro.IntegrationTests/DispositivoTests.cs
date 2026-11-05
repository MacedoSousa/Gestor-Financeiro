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
using OtpNet;
using Totp = OtpNet.Totp;

namespace GestorFinanceiro.IntegrationTests;

/// <summary>
/// Celular novo (pedido aprovado pelo cônjuge), código de recuperação, confirmação recente e destravar.
/// Os corpos vão como JSON solto, para testar o formato do fio.
/// </summary>
[Collection("postgres")]
public class DispositivoTests(PostgresFixture pg)
{
    private const string SenhaDaAna = "senha-da-ana-123";
    private static readonly string CpfDaAna = CpfSintetico.Gerar(1);
    private static readonly string CpfDoBruno = CpfSintetico.Gerar(2);
    private const string RotaPedido = "/api/v1/auth/dispositivo/pedido";
    private const string RotaResgate = "/api/v1/auth/dispositivo/pedido/resgate";
    private const string RotaPedidos = "/api/v1/dispositivos/pedidos";
    private const string RotaRecuperacao = "/api/v1/auth/recuperacao";
    private const string RotaDestravar = "/api/v1/dispositivos/conjuge/destravar";
    private const string MensagemFalha = "Não foi possível entrar. Confira os dados e tente de novo.";
    private const string MensagemBloqueio = "Conta bloqueada por excesso de tentativas. Tente mais tarde ou peça ao outro cônjuge para destravar.";
    private const string MensagemConfirme = "Confirme sua identidade para continuar.";
    private const string SqlAna = "SELECT id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'";
    private const string SqlBruno = "SELECT id FROM identidade.usuarios WHERE nome_completo = 'Bruno Teste'";

    private static RelogioFixo NovoRelogio() => new() { Agora = new DateTimeOffset(2026, 11, 5, 12, 0, 10, TimeSpan.Zero) };

    /// <summary>Cadastra o casal e devolve o código de recuperação (formato exibido).</summary>
    private static async Task<string> CadastrarAsync(ApiDeTeste api)
    {
        var pedido = new CadastroDoCasalDto(
            api.CodigoInstalacao, "Família Teste",
            new ConjugeNovoDto("Ana Teste", CpfDaAna, SenhaDaAna),
            new ConjugeNovoDto("Bruno Teste", CpfDoBruno, "senha-do-bruno-1"));
        var resposta = await api.CreateClient().PostAsJsonAsync("/api/v1/casal", pedido);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return (await resposta.Content.ReadFromJsonAsync<CasalCadastradoDto>())!.CodigoRecuperacao;
    }

    private static HttpClient ClienteSemCookies(ApiDeTeste api, string? cookie = null)
    {
        var c = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = api.ClientOptions.BaseAddress });
        if (cookie is not null) c.DefaultRequestHeaders.Add("Cookie", cookie);
        return c;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<string> TituloAsync(HttpResponseMessage r) => (await JsonAsync(r)).GetProperty("title").GetString()!;

    private static async Task<T> EscalarAsync<T>(BancoDeTeste banco, string sql)
    {
        await using var db = new NpgsqlConnection(banco.ConexaoSuperusuario);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, db);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static string Cookie(HttpResponseMessage r, string nome) =>
        r.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(nome + "=", StringComparison.Ordinal));

    /// <summary>Pede um celular novo e devolve o código e um cliente que carrega o cookie do pedido.</summary>
    private static async Task<(string Codigo, HttpClient Celular, HttpResponseMessage Resposta)> PedirAsync(ApiDeTeste api, string cpf)
    {
        var resposta = await ClienteSemCookies(api).PostAsJsonAsync(RotaPedido, new { cpf });
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var codigo = (await JsonAsync(resposta)).GetProperty("codigo").GetString()!;
        var cookie = Cookie(resposta, "__Host-gf_pedido").Split(';')[0];
        return (codigo, ClienteSemCookies(api, cookie), resposta);
    }

    private static async Task<string> EstadoAsync(HttpClient celular)
    {
        var r = await celular.GetAsync(RotaPedido);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await JsonAsync(r)).GetProperty("estado").GetString()!;
    }

    private static async Task<JsonElement[]> PendentesAsync(HttpClient logado)
    {
        var r = await logado.GetAsync(RotaPedidos);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await JsonAsync(r)).EnumerateArray().ToArray();
    }

    private static Task<HttpResponseMessage> AprovarAsync(HttpClient logado, Guid pedidoId) =>
        logado.PostAsync($"{RotaPedidos}/{pedidoId}/aprovacao", null);

    private static Task<HttpResponseMessage> RecuperarAsync(HttpClient c, string cpf, string codigo) =>
        c.PostAsJsonAsync(RotaRecuperacao, new { cpf, codigo });

    private static Task<Guid> PedidoDaAnaAsync(BancoDeTeste banco) =>
        EscalarAsync<Guid>(banco, "SELECT p.id FROM identidade.pedidos_dispositivo p JOIN identidade.usuarios u ON u.id = p.usuario_id WHERE u.nome_completo = 'Ana Teste'");

    [Fact]
    public async Task Pedido_aprovado_pelo_conjuge_vira_autorizacao_de_registro()
    {
        var relogio = NovoRelogio();
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: relogio);
        await CadastrarAsync(api);

        var (codigo, celular, criado) = await PedirAsync(api, CpfDaAna);
        Assert.Matches("^[0-9]{6}$", codigo);
        var cookieDoPedido = Cookie(criado, "__Host-gf_pedido");
        Assert.Contains("httponly", cookieDoPedido, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookieDoPedido, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookieDoPedido, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookieDoPedido, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max-age=600", cookieDoPedido, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain", cookieDoPedido, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("PENDENTE", await EstadoAsync(celular));

        var bruno = await api.ClienteLogadoAsync(await EscalarAsync<Guid>(banco, SqlBruno), relogio.Agora);
        var pendente = Assert.Single(await PendentesAsync(bruno));
        Assert.Equal(codigo, pendente.GetProperty("codigo").GetString());
        Assert.Equal(relogio.Agora.AddMinutes(10), pendente.GetProperty("expiraEm").GetDateTimeOffset());
        Assert.Equal(HttpStatusCode.NoContent, (await AprovarAsync(bruno, pendente.GetProperty("id").GetGuid())).StatusCode);

        Assert.Equal("APROVADO", await EstadoAsync(celular));
        Assert.Empty(await PendentesAsync(bruno));

        var resgate = await celular.PostAsync(RotaResgate, null);
        Assert.Equal(HttpStatusCode.NoContent, resgate.StatusCode);
        var registro = Cookie(resgate, NomesDoLogin.CookieDeRegistro);
        Assert.Contains("max-age=600", registro, StringComparison.OrdinalIgnoreCase);
        var apagado = Cookie(resgate, "__Host-gf_pedido");
        Assert.Contains("expires=Thu, 01 Jan 1970", apagado, StringComparison.OrdinalIgnoreCase);

        // A autorização serve para registrar o TOTP do celular novo.
        var comRegistro = ClienteSemCookies(api, registro.Split(';')[0]);
        Assert.Equal(HttpStatusCode.OK, (await comRegistro.PostAsJsonAsync("/api/v1/auth/totp/registro", new NovoTotpDto("Celular novo"))).StatusCode);
        Assert.Equal("USADO", await EscalarAsync<string>(banco, "SELECT estado FROM identidade.pedidos_dispositivo"));
        Assert.Equal(await EscalarAsync<Guid>(banco, SqlBruno), await EscalarAsync<Guid>(banco, "SELECT aprovado_por FROM identidade.pedidos_dispositivo"));

        // O resgate é de uso único.
        Assert.Equal(HttpStatusCode.Conflict, (await celular.PostAsync(RotaResgate, null)).StatusCode);
    }

    [Fact]
    public async Task Consulta_do_pedido_so_devolve_o_status()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco);
        await CadastrarAsync(api);
        var (codigo, celular, _) = await PedirAsync(api, CpfDaAna);
        const string sqlEventos = "SELECT count(*) FROM audit.eventos";
        const string sqlPedido = "SELECT estado || '|' || atualizado_em::text || '|' || is_deleted::text FROM identidade.pedidos_dispositivo";
        var eventosAntes = await EscalarAsync<long>(banco, sqlEventos);
        var pedidoAntes = await EscalarAsync<string>(banco, sqlPedido);

        var resposta = await celular.GetAsync(RotaPedido);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.False(resposta.Headers.Contains("Set-Cookie"));
        var corpo = await JsonAsync(resposta);
        Assert.Equal(["estado"], corpo.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("PENDENTE", corpo.GetProperty("estado").GetString());
        Assert.DoesNotContain(codigo, await resposta.Content.ReadAsStringAsync());
        Assert.Equal(eventosAntes, await EscalarAsync<long>(banco, sqlEventos));
        Assert.Equal(pedidoAntes, await EscalarAsync<string>(banco, sqlPedido));

        // Sem cookie, ou com cookie adulterado: PENDENTE, sem revelar nada.
        Assert.Equal("PENDENTE", await EstadoAsync(ClienteSemCookies(api)));
        Assert.Equal("PENDENTE", await EstadoAsync(ClienteSemCookies(api, "__Host-gf_pedido=lixo.123.abc")));
    }

    [Fact]
    public async Task Resgate_de_pedido_pendente_responde_409()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco);
        await CadastrarAsync(api);
        var (_, celular, _) = await PedirAsync(api, CpfDaAna);

        var resgate = await celular.PostAsync(RotaResgate, null);

        Assert.Equal(HttpStatusCode.Conflict, resgate.StatusCode);
        Assert.False(resgate.Headers.Contains("Set-Cookie"));
        Assert.Equal("PENDENTE", await EscalarAsync<string>(banco, "SELECT estado FROM identidade.pedidos_dispositivo"));
        // Sem cookie nenhum, também 409.
        Assert.Equal(HttpStatusCode.Conflict, (await ClienteSemCookies(api).PostAsync(RotaResgate, null)).StatusCode);
    }

    [Fact]
    public async Task Dono_nao_aprova_o_proprio_pedido()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco);
        await CadastrarAsync(api);
        await PedirAsync(api, CpfDaAna);
        var ana = await api.ClienteLogadoAsync(await EscalarAsync<Guid>(banco, SqlAna), DateTimeOffset.UtcNow);

        Assert.Empty(await PendentesAsync(ana));
        var resposta = await AprovarAsync(ana, await PedidoDaAnaAsync(banco));

        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
        Assert.Equal("PENDENTE", await EscalarAsync<string>(banco, "SELECT estado FROM identidade.pedidos_dispositivo"));
        // Pedido desconhecido: 404.
        var bruno = await api.ClienteLogadoAsync(await EscalarAsync<Guid>(banco, SqlBruno), DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.NotFound, (await AprovarAsync(bruno, Guid.CreateVersion7())).StatusCode);
    }

    [Fact]
    public async Task Aprovar_sem_confirmacao_recente_responde_403()
    {
        var relogio = NovoRelogio();
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: relogio);
        await CadastrarAsync(api);
        await PedirAsync(api, CpfDaAna);
        var bruno = await api.ClienteLogadoAsync(await EscalarAsync<Guid>(banco, SqlBruno), relogio.Agora.AddMinutes(-6));

        var resposta = await AprovarAsync(bruno, await PedidoDaAnaAsync(banco));

        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
        Assert.Equal(MensagemConfirme, await TituloAsync(resposta));
        Assert.Equal("PENDENTE", await EscalarAsync<string>(banco, "SELECT estado FROM identidade.pedidos_dispositivo"));
    }

    [Fact]
    public async Task Pedido_expira_em_10_min()
    {
        var relogio = NovoRelogio();
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: relogio);
        await CadastrarAsync(api);
        var inicio = relogio.Agora;
        var (_, celular, _) = await PedirAsync(api, CpfDaAna);
        var pedidoId = await PedidoDaAnaAsync(banco);

        relogio.Agora = inicio.AddMinutes(10).AddSeconds(-1);
        Assert.Equal("PENDENTE", await EstadoAsync(celular));

        relogio.Agora = inicio.AddMinutes(10);
        Assert.Equal("EXPIRADO", await EstadoAsync(celular));
        var bruno = await api.ClienteLogadoAsync(await EscalarAsync<Guid>(banco, SqlBruno), relogio.Agora);
        Assert.Empty(await PendentesAsync(bruno));
        Assert.Equal(HttpStatusCode.NotFound, (await AprovarAsync(bruno, pedidoId)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await celular.PostAsync(RotaResgate, null)).StatusCode);
    }

    [Fact]
    public async Task Pedido_aprovado_que_expira_antes_do_resgate_responde_409()
    {
        var relogio = NovoRelogio();
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: relogio);
        await CadastrarAsync(api);
        var inicio = relogio.Agora;
        var (_, celular, _) = await PedirAsync(api, CpfDaAna);
        var bruno = await api.ClienteLogadoAsync(await EscalarAsync<Guid>(banco, SqlBruno), relogio.Agora);
        Assert.Equal(HttpStatusCode.NoContent, (await AprovarAsync(bruno, await PedidoDaAnaAsync(banco))).StatusCode);

        relogio.Agora = inicio.AddMinutes(10);

        Assert.Equal("EXPIRADO", await EstadoAsync(celular));
        Assert.Equal(HttpStatusCode.Conflict, (await celular.PostAsync(RotaResgate, null)).StatusCode);
    }

    [Fact]
    public async Task Pedido_para_cpf_inexistente_parece_igual_e_nunca_e_aprovado()
    {
        var relogio = NovoRelogio();
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: relogio);
        await CadastrarAsync(api);
        var inicio = relogio.Agora;

        var (_, _, real) = await PedirAsync(api, CpfDaAna);
        var (codigo, celular, falso) = await PedirAsync(api, CpfSintetico.Gerar(9));

        Assert.Matches("^[0-9]{6}$", codigo);
        Assert.Equal(["codigo"], (await JsonAsync(falso)).EnumerateObject().Select(p => p.Name).ToArray());
        string Atributos(HttpResponseMessage r) => Cookie(r, "__Host-gf_pedido").Split(';', 2)[1];
        Assert.Equal(Atributos(real), Atributos(falso));
        Assert.Equal(Cookie(real, "__Host-gf_pedido").Split(';')[0].Length, Cookie(falso, "__Host-gf_pedido").Split(';')[0].Length);
        Assert.Equal(1L, await EscalarAsync<long>(banco, "SELECT count(*) FROM identidade.pedidos_dispositivo"));

        Assert.Equal("PENDENTE", await EstadoAsync(celular));
        var bruno = await api.ClienteLogadoAsync(await EscalarAsync<Guid>(banco, SqlBruno), relogio.Agora);
        Assert.DoesNotContain(await PendentesAsync(bruno), p => p.GetProperty("codigo").GetString() == codigo);
        Assert.Equal(HttpStatusCode.Conflict, (await celular.PostAsync(RotaResgate, null)).StatusCode);

        relogio.Agora = inicio.AddMinutes(10);
        Assert.Equal("EXPIRADO", await EstadoAsync(celular));
    }

    [Fact]
    public async Task Codigo_de_recuperacao_da_autorizacao_e_gira_o_codigo()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco);
        var antigo = await CadastrarAsync(api);
        const string sqlAtivos = "SELECT count(*) FROM identidade.chaves WHERE tipo = 'RECUPERACAO' AND NOT is_deleted";

        var resposta = await RecuperarAsync(ClienteSemCookies(api), CpfDaAna, antigo);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("no-store", resposta.Headers.CacheControl?.ToString());
        var corpo = await JsonAsync(resposta);
        Assert.Equal(["codigoRecuperacao"], corpo.EnumerateObject().Select(p => p.Name).ToArray());
        var novo = corpo.GetProperty("codigoRecuperacao").GetString()!;
        Assert.Matches("^([A-Z2-9]{4}-){7}[A-Z2-9]{4}$", novo);
        Assert.NotEqual(antigo, novo);
        var registro = Cookie(resposta, NomesDoLogin.CookieDeRegistro);
        Assert.Contains("max-age=600", registro, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1L, await EscalarAsync<long>(banco, sqlAtivos));
        Assert.Equal(1L, await EscalarAsync<long>(banco, "SELECT count(*) FROM identidade.chaves WHERE tipo = 'RECUPERACAO' AND is_deleted"));
        Assert.Equal(0, await EscalarAsync<int>(banco, "SELECT falhas_seguidas FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'"));

        // A autorização emitida serve para registrar um dispositivo.
        var comRegistro = ClienteSemCookies(api, registro.Split(';')[0]);
        Assert.Equal(HttpStatusCode.OK, (await comRegistro.PostAsJsonAsync("/api/v1/auth/totp/registro", new NovoTotpDto("Celular novo"))).StatusCode);

        // O antigo deixa de abrir; o novo abre (e gira de novo).
        var comAntigo = await RecuperarAsync(ClienteSemCookies(api), CpfDaAna, antigo);
        Assert.Equal(HttpStatusCode.Unauthorized, comAntigo.StatusCode);
        Assert.Equal(MensagemFalha, await TituloAsync(comAntigo));
        Assert.False(comAntigo.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.OK, (await RecuperarAsync(ClienteSemCookies(api), CpfDaAna, novo)).StatusCode);
        Assert.Equal(1L, await EscalarAsync<long>(banco, sqlAtivos));
    }

    [Fact]
    public async Task Codigo_de_recuperacao_digitado_sem_hifens_e_minusculo_funciona()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco);
        var codigo = await CadastrarAsync(api);

        var resposta = await RecuperarAsync(ClienteSemCookies(api), CpfDoBruno, codigo.Replace("-", "").ToLowerInvariant());

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }

    [Fact]
    public async Task Recuperacao_com_cpf_inexistente_responde_igual_a_codigo_errado()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco);
        await CadastrarAsync(api);

        var errado = await RecuperarAsync(ClienteSemCookies(api), CpfDaAna, "AAAA-BBBB");
        var inexistente = await RecuperarAsync(ClienteSemCookies(api), CpfSintetico.Gerar(9), "AAAA-BBBB");

        Assert.Equal(HttpStatusCode.Unauthorized, inexistente.StatusCode);
        Assert.Equal(await TituloAsync(errado), await TituloAsync(inexistente));
        Assert.Equal(1, await EscalarAsync<int>(banco, "SELECT falhas_seguidas FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'"));
    }

    [Fact]
    public async Task Falhas_de_tipos_diferentes_somam_e_bloqueio_vale_ate_para_codigo_certo()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco);
        var codigo = await CadastrarAsync(api);
        var anonimo = ClienteSemCookies(api);

        // 2 senhas erradas no primeiro acesso + 1 TOTP errado na confirmação + 2 códigos de recuperação errados = 5.
        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await anonimo.PostAsJsonAsync("/api/v1/auth/primeiro-acesso", new PrimeiroAcessoDto(CpfDaAna, "senha-errada-xyz"))).StatusCode);
        var ana = await api.ClienteLogadoAsync(await EscalarAsync<Guid>(banco, SqlAna));
        Assert.Equal(HttpStatusCode.Unauthorized, (await ana.PostAsJsonAsync("/api/v1/auth/confirmar", new { codigoTotp = "000000" })).StatusCode);
        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await RecuperarAsync(anonimo, CpfDaAna, "AAAA-BBBB-CCCC")).StatusCode);

        var certo = await RecuperarAsync(anonimo, CpfDaAna, codigo);

        Assert.Equal(HttpStatusCode.Locked, certo.StatusCode);
        Assert.Equal(MensagemBloqueio, await TituloAsync(certo));
        Assert.False(certo.Headers.Contains("Set-Cookie"));
        Assert.Equal(1, await EscalarAsync<int>(banco, "SELECT bloqueios FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'"));
        // Nada girou: o envelope original continua o único ativo e nenhum foi removido.
        Assert.Equal(0L, await EscalarAsync<long>(banco, "SELECT count(*) FROM identidade.chaves WHERE tipo = 'RECUPERACAO' AND is_deleted"));
    }

    [Fact]
    public async Task Conjuge_destrava_na_hora()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco);
        var codigo = await CadastrarAsync(api);
        var anonimo = ClienteSemCookies(api);
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await RecuperarAsync(anonimo, CpfDaAna, "AAAA-BBBB-CCCC")).StatusCode);
        Assert.Equal(HttpStatusCode.Locked, (await RecuperarAsync(anonimo, CpfDaAna, codigo)).StatusCode);
        var brunoId = await EscalarAsync<Guid>(banco, SqlBruno);

        // Sem confirmação recente, não destrava.
        var semConfirmacao = await api.ClienteLogadoAsync(brunoId, DateTimeOffset.UtcNow.AddMinutes(-6));
        var negado = await semConfirmacao.PostAsync(RotaDestravar, null);
        Assert.Equal(HttpStatusCode.Forbidden, negado.StatusCode);
        Assert.Equal(MensagemConfirme, await TituloAsync(negado));

        var bruno = await api.ClienteLogadoAsync(brunoId, DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.NoContent, (await bruno.PostAsync(RotaDestravar, null)).StatusCode);

        Assert.True(await EscalarAsync<bool>(banco, "SELECT bloqueado_ate IS NULL AND falhas_seguidas = 0 FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'"));
        // Destravar mantém a contagem de bloqueios (só um login certo a zera).
        Assert.Equal(1, await EscalarAsync<int>(banco, "SELECT bloqueios FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'"));
        Assert.Equal(HttpStatusCode.OK, (await RecuperarAsync(anonimo, CpfDaAna, codigo)).StatusCode);
    }

    [Fact]
    public async Task Confirmar_com_totp_do_proprio_usuario_renova_a_confirmacao()
    {
        var relogio = NovoRelogio();
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: relogio);
        await CadastrarAsync(api);
        var anaId = await EscalarAsync<Guid>(banco, SqlAna);
        var segredo = await ConfigurarTotpAsync(api, banco, anaId, relogio);
        relogio.Agora = relogio.Agora.AddMinutes(1);
        var ana = await api.ClienteLogadoAsync(anaId, relogio.Agora.AddMinutes(-6));
        Assert.False((await JsonAsync(await ana.GetAsync("/api/v1/auth/eu"))).GetProperty("confirmacaoRecente").GetBoolean());

        var errado = await ana.PostAsJsonAsync("/api/v1/auth/confirmar", new { codigoTotp = "000000" });
        Assert.Equal(HttpStatusCode.Unauthorized, errado.StatusCode);
        Assert.Equal(1, await EscalarAsync<int>(banco, "SELECT falhas_seguidas FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'"));

        var certo = await ana.PostAsJsonAsync("/api/v1/auth/confirmar", new { codigoTotp = new Totp(segredo).ComputeTotp(relogio.Agora.UtcDateTime) });

        Assert.Equal(HttpStatusCode.NoContent, certo.StatusCode);
        Assert.True((await JsonAsync(await ana.GetAsync("/api/v1/auth/eu"))).GetProperty("confirmacaoRecente").GetBoolean());
        Assert.Equal(0, await EscalarAsync<int>(banco, "SELECT falhas_seguidas FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'"));
    }

    [Fact]
    public async Task Confirmar_com_totp_do_conjuge_responde_401()
    {
        var relogio = NovoRelogio();
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: relogio);
        await CadastrarAsync(api);
        var segredoDoBruno = await ConfigurarTotpAsync(api, banco, await EscalarAsync<Guid>(banco, SqlBruno), relogio);
        relogio.Agora = relogio.Agora.AddMinutes(1);
        var ana = await api.ClienteLogadoAsync(await EscalarAsync<Guid>(banco, SqlAna), relogio.Agora.AddMinutes(-6));

        var resposta = await ana.PostAsJsonAsync("/api/v1/auth/confirmar", new { codigoTotp = new Totp(segredoDoBruno).ComputeTotp(relogio.Agora.UtcDateTime) });

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        Assert.False((await JsonAsync(await ana.GetAsync("/api/v1/auth/eu"))).GetProperty("confirmacaoRecente").GetBoolean());
    }

    [Fact]
    public async Task Confirmar_com_passkey_propria_renova_e_de_outra_pessoa_responde_401()
    {
        var relogio = NovoRelogio();
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: relogio);
        await CadastrarAsync(api);
        var anaId = await EscalarAsync<Guid>(banco, SqlAna);
        var brunoId = await EscalarAsync<Guid>(banco, SqlBruno);
        using var daAna = new AutenticadorDeTeste();
        using var doBruno = new AutenticadorDeTeste();
        await ConfigurarPasskeyAsync(api, banco, anaId, daAna);
        await ConfigurarPasskeyAsync(api, banco, brunoId, doBruno);
        var ana = await api.ClienteLogadoAsync(anaId, relogio.Agora.AddMinutes(-6));

        // Passkey do Bruno assinando o desafio da sessão da Ana: 401 e falha contada para a Ana.
        var opcoes = await OpcoesDeConfirmacaoAsync(ana);
        var allow = opcoes.GetProperty("allowCredentials").EnumerateArray().ToArray();
        Assert.Equal(daAna.IdBase64Url, Assert.Single(allow).GetProperty("id").GetString());
        Assert.Equal("required", opcoes.GetProperty("userVerification").GetString());
        var alheia = await ana.PostAsJsonAsync("/api/v1/auth/confirmar", new JsonObject { ["passkey"] = doBruno.Autenticar(opcoes) });
        Assert.Equal(HttpStatusCode.Unauthorized, alheia.StatusCode);
        Assert.Equal(1, await EscalarAsync<int>(banco, "SELECT falhas_seguidas FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'"));

        // O desafio é de uso único: reaproveitá-lo com a passkey certa não passa.
        var reuso = await ana.PostAsJsonAsync("/api/v1/auth/confirmar", new JsonObject { ["passkey"] = daAna.Autenticar(opcoes) });
        Assert.Equal(HttpStatusCode.Unauthorized, reuso.StatusCode);

        // Desafio de outra sessão (do Bruno) não serve para a sessão da Ana.
        var bruno = await api.ClienteLogadoAsync(brunoId);
        var opcoesDoBruno = await OpcoesDeConfirmacaoAsync(bruno);
        var cruzada = await ana.PostAsJsonAsync("/api/v1/auth/confirmar", new JsonObject { ["passkey"] = daAna.Autenticar(opcoesDoBruno) });
        Assert.Equal(HttpStatusCode.Unauthorized, cruzada.StatusCode);

        var propria = await ana.PostAsJsonAsync("/api/v1/auth/confirmar", new JsonObject { ["passkey"] = daAna.Autenticar(await OpcoesDeConfirmacaoAsync(ana)) });
        Assert.Equal(HttpStatusCode.NoContent, propria.StatusCode);
        Assert.True((await JsonAsync(await ana.GetAsync("/api/v1/auth/eu"))).GetProperty("confirmacaoRecente").GetBoolean());
    }

    [Fact]
    public async Task Cadeia_de_auditoria_continua_integra_depois_do_fluxo()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco);
        var codigo = await CadastrarAsync(api);
        var (_, celular, _) = await PedirAsync(api, CpfDaAna);
        var bruno = await api.ClienteLogadoAsync(await EscalarAsync<Guid>(banco, SqlBruno), DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.NoContent, (await AprovarAsync(bruno, await PedidoDaAnaAsync(banco))).StatusCode);
        Assert.Equal("APROVADO", await EstadoAsync(celular));
        Assert.Equal(HttpStatusCode.NoContent, (await celular.PostAsync(RotaResgate, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RecuperarAsync(ClienteSemCookies(api), CpfDaAna, "AAAA-BBBB")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RecuperarAsync(ClienteSemCookies(api), CpfDaAna, codigo)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await bruno.PostAsync(RotaDestravar, null)).StatusCode);

        var casalId = await EscalarAsync<Guid>(banco, "SELECT id FROM identidade.casais");
        Assert.Equal(0L, await EscalarAsync<long>(banco, $"SELECT count(*) FROM audit.verificar_cadeia('{casalId}')"));
        Assert.True(await EscalarAsync<long>(banco,
            "SELECT count(*) FROM audit.eventos WHERE tabela = 'identidade.chaves' AND acao = 'SOFT_DELETE'") >= 1);
        Assert.True(await EscalarAsync<long>(banco,
            "SELECT count(*) FROM audit.eventos WHERE tabela = 'identidade.pedidos_dispositivo'") >= 3);
    }

    private static async Task<JsonElement> OpcoesDeConfirmacaoAsync(HttpClient logado)
    {
        var r = await logado.PostAsync("/api/v1/auth/confirmar/opcoes", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        return await JsonAsync(r);
    }

    private static async Task<HttpClient> ComAutorizacaoAsync(ApiDeTeste api, BancoDeTeste banco, Guid usuarioId)
    {
        var casalId = await EscalarAsync<Guid>(banco, "SELECT id FROM identidade.casais");
        await using var escopo = api.Services.CreateAsyncScope();
        var token = escopo.ServiceProvider.GetRequiredService<AutorizacoesDeRegistro>().Emitir(usuarioId, casalId, OrigemDoRegistro.AprovacaoDoConjuge);
        return ClienteSemCookies(api, $"{NomesDoLogin.CookieDeRegistro}={token}");
    }

    private static async Task<byte[]> ConfigurarTotpAsync(ApiDeTeste api, BancoDeTeste banco, Guid usuarioId, RelogioFixo relogio)
    {
        var cliente = await ComAutorizacaoAsync(api, banco, usuarioId);
        var registro = await cliente.PostAsJsonAsync("/api/v1/auth/totp/registro", new NovoTotpDto("Celular"));
        Assert.Equal(HttpStatusCode.OK, registro.StatusCode);
        var dto = (await registro.Content.ReadFromJsonAsync<TotpGeradoDto>())!;
        var segredo = Base32Encoding.ToBytes(System.Web.HttpUtility.ParseQueryString(new Uri(dto.OtpauthUri).Query)["secret"]!);
        var confirmacao = await cliente.PostAsJsonAsync("/api/v1/auth/totp/confirmacao",
            new ConfirmarTotpDto(dto.CredencialId, new Totp(segredo).ComputeTotp(relogio.Agora.UtcDateTime)));
        Assert.Equal(HttpStatusCode.NoContent, confirmacao.StatusCode);
        return segredo;
    }

    private static async Task ConfigurarPasskeyAsync(ApiDeTeste api, BancoDeTeste banco, Guid usuarioId, AutenticadorDeTeste autenticador)
    {
        var cliente = await ComAutorizacaoAsync(api, banco, usuarioId);
        var opcoes = await JsonAsync(await cliente.PostAsync("/api/v1/auth/passkey/registro/opcoes", null));
        var registro = await cliente.PostAsJsonAsync("/api/v1/auth/passkey/registro",
            new JsonObject { ["apelido"] = "Celular", ["resposta"] = autenticador.Registrar(opcoes) });
        Assert.Equal(HttpStatusCode.NoContent, registro.StatusCode);
    }
}
