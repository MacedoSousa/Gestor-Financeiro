using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GestorFinanceiro.IntegrationTests.Infra;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.Modules.Identidade.Dominio;
using GestorFinanceiro.Modules.Identidade.Infra;
using GestorFinanceiro.SharedKernel.Persistencia;
using GestorFinanceiro.SharedKernel.Seguranca;
using GestorFinanceiro.UnitTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GestorFinanceiro.IntegrationTests;

/// <summary>Credenciais do usuário (listar, remover, autorizar nova) e o novo código de recuperação.</summary>
[Collection("postgres")]
public class CredenciaisTests(PostgresFixture pg)
{
    private const string SenhaDaAna = "senha-da-ana-123";
    private static readonly string CpfDaAna = CpfSintetico.Gerar(1);
    private static readonly string CpfDoBruno = CpfSintetico.Gerar(2);
    private const string RotaCredenciais = "/api/v1/dispositivos/credenciais";
    private const string RotaCodigo = "/api/v1/casal/codigo-recuperacao";
    private const string MensagemUltima = "Você precisa manter pelo menos uma forma de entrar.";
    private const string MensagemConfirme = "Confirme sua identidade para continuar.";
    private const string SqlAna = "SELECT id FROM identidade.usuarios WHERE nome_completo = 'Ana Teste'";
    private const string SqlBruno = "SELECT id FROM identidade.usuarios WHERE nome_completo = 'Bruno Teste'";

    private static RelogioFixo NovoRelogio() => new() { Agora = new DateTimeOffset(2026, 11, 5, 12, 0, 10, TimeSpan.Zero) };

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

    /// <summary>Grava direto uma credencial confirmada (passkey ou TOTP) e encerra o primeiro acesso do usuário.</summary>
    private static async Task<Guid> CriarAsync(ApiDeTeste api, Guid usuarioId, TipoCredencial tipo, string apelido, bool confirmada = true)
    {
        await using var escopo = api.Services.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<IdentidadeDbContext>();
        var usuario = await db.Usuarios.IgnoreQueryFilters([Filtros.Casal]).AsTracking().FirstAsync(u => u.Id == usuarioId);
        var credencial = tipo == TipoCredencial.Passkey
            ? Credencial.CriarPasskey(usuario.CasalId, usuarioId, apelido, Guid.NewGuid().ToByteArray(), [1, 2, 3], 0)
            : Credencial.CriarTotp(usuario.CasalId, usuarioId, apelido, "JBSWY3DPEHPK3PXP", escopo.ServiceProvider.GetRequiredService<ICofre>());
        if (confirmada) credencial.Confirmar();
        db.Credenciais.Add(credencial);
        usuario.EncerrarPrimeiroAcesso(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        return credencial.Id;
    }

    [Fact]
    public async Task Lista_so_credenciais_proprias_ativas()
    {
        var relogio = NovoRelogio();
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: relogio);
        await CadastrarAsync(api);
        var ana = await EscalarAsync<Guid>(banco, SqlAna);
        var bruno = await EscalarAsync<Guid>(banco, SqlBruno);
        var passkey = await CriarAsync(api, ana, TipoCredencial.Passkey, "Notebook");
        var totp = await CriarAsync(api, ana, TipoCredencial.Totp, "Celular");
        await CriarAsync(api, ana, TipoCredencial.Totp, "Pendente", confirmada: false);
        var removida = await CriarAsync(api, ana, TipoCredencial.Totp, "Antigo");
        await CriarAsync(api, bruno, TipoCredencial.Passkey, "Do Bruno");
        await ExecutarAsync(banco, $"UPDATE identidade.credenciais SET is_deleted = true WHERE id = '{removida}'");

        var cliente = await api.ClienteLogadoAsync(ana, relogio.Agora);
        var resposta = await cliente.GetAsync(RotaCredenciais);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var itens = (await JsonAsync(resposta)).EnumerateArray().ToArray();
        Assert.Equal(new[] { passkey, totp }.Order(), itens.Select(i => i.GetProperty("id").GetGuid()).Order());
        Assert.All(itens, i => Assert.Equal(JsonValueKind.String, i.GetProperty("criadaEm").ValueKind));
        Assert.Contains(itens, i => i.GetProperty("tipo").GetString() == "PASSKEY" && i.GetProperty("apelido").GetString() == "Notebook");
        Assert.Contains(itens, i => i.GetProperty("tipo").GetString() == "TOTP");
    }

    [Fact]
    public async Task Primeiro_acesso_continua_fechado_depois_de_remover_credenciais()
    {
        var relogio = NovoRelogio();
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: relogio);
        await CadastrarAsync(api);
        var ana = await EscalarAsync<Guid>(banco, SqlAna);
        var passkey = await CriarAsync(api, ana, TipoCredencial.Passkey, "Notebook");
        var totp = await CriarAsync(api, ana, TipoCredencial.Totp, "Celular");
        var cliente = await api.ClienteLogadoAsync(ana, relogio.Agora);

        Assert.Equal(HttpStatusCode.NoContent, (await cliente.DeleteAsync($"{RotaCredenciais}/{totp}")).StatusCode);
        await ExecutarAsync(banco, $"UPDATE identidade.credenciais SET is_deleted = true WHERE id = '{passkey}'");

        var resposta = await api.CreateClient().PostAsJsonAsync("/api/v1/auth/primeiro-acesso", new PrimeiroAcessoDto(CpfDaAna, SenhaDaAna));
        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
    }

    [Fact]
    public async Task Nao_remove_a_ultima_credencial()
    {
        var relogio = NovoRelogio();
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: relogio);
        await CadastrarAsync(api);
        var ana = await EscalarAsync<Guid>(banco, SqlAna);
        var unica = await CriarAsync(api, ana, TipoCredencial.Passkey, "Notebook");
        await CriarAsync(api, ana, TipoCredencial.Totp, "Pendente", confirmada: false); // não conta
        var cliente = await api.ClienteLogadoAsync(ana, relogio.Agora);

        var resposta = await cliente.DeleteAsync($"{RotaCredenciais}/{unica}");

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Equal(MensagemUltima, (await JsonAsync(resposta)).GetProperty("title").GetString());
        Assert.False(await EscalarAsync<bool>(banco, $"SELECT is_deleted FROM identidade.credenciais WHERE id = '{unica}'"));
    }

    [Fact]
    public async Task Remove_credencial_com_outra_ativa()
    {
        var relogio = NovoRelogio();
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: relogio);
        await CadastrarAsync(api);
        var ana = await EscalarAsync<Guid>(banco, SqlAna);
        var bruno = await EscalarAsync<Guid>(banco, SqlBruno);
        var passkey = await CriarAsync(api, ana, TipoCredencial.Passkey, "Notebook");
        var totp = await CriarAsync(api, ana, TipoCredencial.Totp, "Celular");
        var doBruno = await CriarAsync(api, bruno, TipoCredencial.Passkey, "Do Bruno");
        var cliente = await api.ClienteLogadoAsync(ana, relogio.Agora);

        // Credencial do outro usuário: 404, e nada muda.
        Assert.Equal(HttpStatusCode.NotFound, (await cliente.DeleteAsync($"{RotaCredenciais}/{doBruno}")).StatusCode);
        Assert.False(await EscalarAsync<bool>(banco, $"SELECT is_deleted FROM identidade.credenciais WHERE id = '{doBruno}'"));

        Assert.Equal(HttpStatusCode.NoContent, (await cliente.DeleteAsync($"{RotaCredenciais}/{totp}")).StatusCode);
        Assert.True(await EscalarAsync<bool>(banco, $"SELECT is_deleted FROM identidade.credenciais WHERE id = '{totp}'"));
        Assert.Equal(1L, await EscalarAsync<long>(banco, $"SELECT count(*) FROM identidade.credenciais WHERE id = '{totp}'"));

        // A removida não aparece mais e o restante continua sendo a última.
        var itens = (await JsonAsync(await cliente.GetAsync(RotaCredenciais))).EnumerateArray().ToArray();
        Assert.Equal(passkey, Assert.Single(itens).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Conflict, (await cliente.DeleteAsync($"{RotaCredenciais}/{passkey}")).StatusCode);
    }

    [Fact]
    public async Task Remover_sem_confirmacao_recente_responde_403()
    {
        var relogio = NovoRelogio();
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: relogio);
        await CadastrarAsync(api);
        var ana = await EscalarAsync<Guid>(banco, SqlAna);
        await CriarAsync(api, ana, TipoCredencial.Passkey, "Notebook");
        var totp = await CriarAsync(api, ana, TipoCredencial.Totp, "Celular");
        var cliente = await api.ClienteLogadoAsync(ana, relogio.Agora.AddMinutes(-6));

        var resposta = await cliente.DeleteAsync($"{RotaCredenciais}/{totp}");

        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
        Assert.Equal(MensagemConfirme, (await JsonAsync(resposta)).GetProperty("title").GetString());
        Assert.False(await EscalarAsync<bool>(banco, $"SELECT is_deleted FROM identidade.credenciais WHERE id = '{totp}'"));
    }

    [Fact]
    public async Task Remocoes_paralelas_nao_deixam_sem_credencial()
    {
        var relogio = NovoRelogio();
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: relogio);
        await CadastrarAsync(api);
        var ana = await EscalarAsync<Guid>(banco, SqlAna);
        var a = await CriarAsync(api, ana, TipoCredencial.Passkey, "Notebook");
        var b = await CriarAsync(api, ana, TipoCredencial.Totp, "Celular");
        var cliente = await api.ClienteLogadoAsync(ana, relogio.Agora);

        var respostas = await Task.WhenAll(cliente.DeleteAsync($"{RotaCredenciais}/{a}"), cliente.DeleteAsync($"{RotaCredenciais}/{b}"));

        Assert.Equal(1, respostas.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(1, respostas.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1L, await EscalarAsync<long>(banco, $"SELECT count(*) FROM identidade.credenciais WHERE usuario_id = '{ana}' AND NOT is_deleted AND confirmada"));
    }

    [Fact]
    public async Task Autorizacao_para_adicionar_credencial_emite_cookie_de_registro()
    {
        var relogio = NovoRelogio();
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: relogio);
        await CadastrarAsync(api);
        var ana = await EscalarAsync<Guid>(banco, SqlAna);
        var cliente = await api.ClienteLogadoAsync(ana, relogio.Agora);

        var resposta = await cliente.PostAsync($"{RotaCredenciais}/autorizacao", null);

        Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);
        Assert.StartsWith("__Host-gf_registro=", resposta.Headers.GetValues("Set-Cookie").Single());

        var semConfirmacao = await api.ClienteLogadoAsync(ana, relogio.Agora.AddMinutes(-6));
        Assert.Equal(HttpStatusCode.Forbidden, (await semConfirmacao.PostAsync($"{RotaCredenciais}/autorizacao", null)).StatusCode);
    }

    [Fact]
    public async Task Novo_codigo_de_recuperacao_invalida_o_anterior()
    {
        var relogio = NovoRelogio();
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: relogio);
        var antigo = await CadastrarAsync(api);
        var ana = await EscalarAsync<Guid>(banco, SqlAna);
        var cliente = await api.ClienteLogadoAsync(ana, relogio.Agora);

        var resposta = await cliente.PostAsync(RotaCodigo, null);

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        var novo = (await resposta.Content.ReadFromJsonAsync<CodigoRecuperacaoDto>())!.CodigoRecuperacao;
        Assert.NotEqual(antigo, novo);
        Assert.Equal(1L, await EscalarAsync<long>(banco, "SELECT count(*) FROM identidade.chaves WHERE tipo = 'RECUPERACAO' AND NOT is_deleted"));
        Assert.Equal(1L, await EscalarAsync<long>(banco, "SELECT count(*) FROM identidade.chaves WHERE tipo = 'RECUPERACAO' AND is_deleted"));

        var comAntigo = await api.CreateClient().PostAsJsonAsync("/api/v1/auth/recuperacao", new { cpf = CpfDoBruno, codigo = antigo });
        Assert.Equal(HttpStatusCode.Unauthorized, comAntigo.StatusCode);
        var comNovo = await api.CreateClient().PostAsJsonAsync("/api/v1/auth/recuperacao", new { cpf = CpfDoBruno, codigo = novo });
        Assert.Equal(HttpStatusCode.OK, comNovo.StatusCode);
    }

    [Fact]
    public async Task Novo_codigo_sem_confirmacao_recente_responde_403()
    {
        var relogio = NovoRelogio();
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: relogio);
        await CadastrarAsync(api);
        var ana = await EscalarAsync<Guid>(banco, SqlAna);
        var cliente = await api.ClienteLogadoAsync(ana, relogio.Agora.AddMinutes(-6));

        var resposta = await cliente.PostAsync(RotaCodigo, null);

        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
        Assert.Equal(0L, await EscalarAsync<long>(banco, "SELECT count(*) FROM identidade.chaves WHERE tipo = 'RECUPERACAO' AND is_deleted"));
    }

    [Fact]
    public async Task Resposta_do_codigo_tem_no_store()
    {
        var relogio = NovoRelogio();
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, relogio: relogio);
        await CadastrarAsync(api);
        var ana = await EscalarAsync<Guid>(banco, SqlAna);
        var cliente = await api.ClienteLogadoAsync(ana, relogio.Agora);

        var resposta = await cliente.PostAsync(RotaCodigo, null);

        Assert.Equal("no-store", resposta.Headers.CacheControl?.ToString());
    }
}
