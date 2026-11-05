using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GestorFinanceiro.IntegrationTests.Infra;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.SharedKernel.Seguranca;
using GestorFinanceiro.UnitTests;
using Npgsql;

namespace GestorFinanceiro.IntegrationTests;

[Collection("postgres")]
public class CadastroDoCasalTests(PostgresFixture pg)
{
    private const string RotaCadastro = "/api/v1/casal";
    private const string RotaEstado = "/api/v1/casal/cadastro";

    private static CadastroDoCasalDto Pedido(string codigo) => new(
        codigo, "Família Teste",
        new ConjugeNovoDto("Ana Teste", CpfSintetico.Gerar(1), "senha-da-ana-123"),
        new ConjugeNovoDto("Bruno Teste", CpfSintetico.Gerar(2), "senha-do-bruno-1"));

    [Fact]
    public async Task Instalacao_nova_esta_disponivel_para_cadastro()
    {
        await using var api = new ApiDeTeste(await pg.NovoBancoAsync());
        var resposta = await api.CreateClient().GetAsync(RotaEstado);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var estado = await resposta.Content.ReadFromJsonAsync<EstadoDoCadastroDto>();
        Assert.True(estado!.Disponivel);
    }

    [Fact]
    public async Task Cadastro_cria_casal_conjuges_vinculados_e_tres_envelopes()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco);
        var cliente = api.CreateClient();
        var pedido = Pedido(api.CodigoInstalacao);

        var resposta = await cliente.PostAsJsonAsync(RotaCadastro, pedido);

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        Assert.True(resposta.Headers.CacheControl?.NoStore, "O 201 precisa de Cache-Control: no-store.");
        Assert.Equal("/api/v1/casal", resposta.Headers.Location?.OriginalString);
        var criado = (await resposta.Content.ReadFromJsonAsync<CasalCadastradoDto>())!;
        Assert.Matches("^[A-Z0-9]{4}(-[A-Z0-9]{4}){7}$", criado.CodigoRecuperacao);

        await using var db = await AbrirSuperusuarioAsync(banco);
        Assert.Equal(1L, await EscalarAsync<long>(db, "SELECT count(*) FROM identidade.casais"));
        Assert.Equal(criado.CasalId, await EscalarAsync<Guid>(db, "SELECT id FROM identidade.casais"));

        var usuarios = await UsuariosAsync(db);
        Assert.Equal(2, usuarios.Count);
        Assert.All(usuarios, u => Assert.Equal(criado.CasalId, u.CasalId));
        Assert.Equal(usuarios[1].Id, usuarios[0].ConjugeId);
        Assert.Equal(usuarios[0].Id, usuarios[1].ConjugeId);

        var digitos = new[] { pedido.Conjuge1.Cpf, pedido.Conjuge2.Cpf };
        foreach (var u in usuarios)
            foreach (var d in digitos)
                Assert.DoesNotContain(d, Encoding.ASCII.GetString(u.CpfCifrado));

        var chaves = await ChavesAsync(db);
        Assert.Equal(3, chaves.Count);
        Assert.All(chaves, c =>
        {
            Assert.Equal(criado.CasalId, c.CasalId);
            Assert.Equal(16, c.Salt.Length);
            Assert.Equal(1, c.Versao);
        });
        var deUsuario = chaves.Where(c => c.Tipo == "USUARIO").Select(c => c.UsuarioId).OrderBy(x => x).ToList();
        Assert.Equal(usuarios.Select(u => (Guid?)u.Id).OrderBy(x => x).ToList(), deUsuario);
        var recuperacao = Assert.Single(chaves, c => c.Tipo == "RECUPERACAO");
        Assert.Null(recuperacao.UsuarioId);

        var estado = await cliente.GetFromJsonAsync<EstadoDoCadastroDto>(RotaEstado);
        Assert.False(estado!.Disponivel);
    }

    [Fact]
    public async Task Senha_de_cada_conjuge_e_codigo_de_recuperacao_abrem_a_mesma_dek()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco);
        var pedido = Pedido(api.CodigoInstalacao);
        var resposta = await api.CreateClient().PostAsJsonAsync(RotaCadastro, pedido);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        var criado = (await resposta.Content.ReadFromJsonAsync<CasalCadastradoDto>())!;

        await using var db = await AbrirSuperusuarioAsync(banco);
        var usuarios = await UsuariosAsync(db);
        var chaves = await ChavesAsync(db);

        var segredoPorUsuario = new Dictionary<Guid, (string Senha, string Cpf)>();
        foreach (var u in usuarios)
        {
            var conjuge = u.NomeCompleto == pedido.Conjuge1.NomeCompleto ? pedido.Conjuge1 : pedido.Conjuge2;
            segredoPorUsuario[u.Id] = (conjuge.Senha, conjuge.Cpf);
        }

        var deks = chaves.Select(c =>
        {
            var segredo = c.Tipo == "RECUPERACAO" ? criado.CodigoRecuperacao : segredoPorUsuario[c.UsuarioId!.Value].Senha;
            var kek = EnvelopeDeChaves.DerivarKek(segredo, c.Salt, new ParametrosArgon2());
            return EnvelopeDeChaves.Desembrulhar(c.DekCifrada, kek);
        }).ToList();

        Assert.Equal(3, deks.Count);
        Assert.Equal(deks[0], deks[1]);
        Assert.Equal(deks[0], deks[2]);

        using var cofre = new CofreAesGcm(deks[0]);
        foreach (var u in usuarios)
        {
            var cpf = cofre.Decifrar(u.CpfCifrado, Cofre.Aad("identidade.usuarios", "cpf_cifrado", u.Id));
            Assert.Equal(segredoPorUsuario[u.Id].Cpf, cpf);
        }
    }

    [Fact]
    public async Task Cadeia_de_auditoria_fica_integra_apos_o_cadastro()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco);
        var pedido = Pedido(api.CodigoInstalacao);
        var resposta = await api.CreateClient().PostAsJsonAsync(RotaCadastro, pedido);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        var casal = (await resposta.Content.ReadFromJsonAsync<CasalCadastradoDto>())!.CasalId;

        await using var db = await AbrirSuperusuarioAsync(banco);
        Assert.Equal(0L, await EscalarAsync<long>(db, $"SELECT count(*) FROM audit.verificar_cadeia('{casal}')"));
        Assert.Equal(8L, await EscalarAsync<long>(db, $"SELECT count(*) FROM audit.eventos WHERE casal_id = '{casal}'"));

        var contagem = new Dictionary<string, long>();
        await using (var cmd = new NpgsqlCommand(
            $"SELECT tabela || ' ' || acao, count(*) FROM audit.eventos WHERE casal_id = '{casal}' GROUP BY 1", db))
        await using (var leitor = await cmd.ExecuteReaderAsync())
            while (await leitor.ReadAsync()) contagem[leitor.GetString(0)] = leitor.GetInt64(1);

        Assert.Equal(new Dictionary<string, long>
        {
            ["identidade.casais INSERT"] = 1,
            ["identidade.usuarios INSERT"] = 2,
            ["identidade.usuarios UPDATE"] = 2,
            ["identidade.chaves INSERT"] = 3,
        }, contagem);

        var alteracoes = await TextosAsync(db, $"SELECT alteracoes::text FROM audit.eventos WHERE casal_id = '{casal}'");

        foreach (var texto in alteracoes)
        {
            Assert.DoesNotContain(pedido.Conjuge1.Cpf, texto);
            Assert.DoesNotContain(pedido.Conjuge2.Cpf, texto);
        }

        var insercoesDeUsuario = await TextosAsync(db,
            $"SELECT alteracoes::text FROM audit.eventos WHERE casal_id = '{casal}' AND tabela = 'identidade.usuarios' AND acao = 'INSERT'");
        Assert.All(insercoesDeUsuario, t =>
        {
            Assert.Contains("\"cpf_cifrado\": \"alterado\"", t);
            Assert.Contains("\"cpf_hmac\": \"alterado\"", t);
        });
        var insercoesDeChave = await TextosAsync(db,
            $"SELECT alteracoes::text FROM audit.eventos WHERE casal_id = '{casal}' AND tabela = 'identidade.chaves'");
        Assert.All(insercoesDeChave, t => Assert.Contains("\"dek_cifrada\": \"alterado\"", t));
    }

    [Fact]
    public async Task Segundo_cadastro_responde_404_mesmo_com_o_codigo_certo()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco);
        var cliente = api.CreateClient();

        var primeira = await cliente.PostAsJsonAsync(RotaCadastro, Pedido(api.CodigoInstalacao));
        Assert.Equal(HttpStatusCode.Created, primeira.StatusCode);

        var segunda = await cliente.PostAsJsonAsync(RotaCadastro, Pedido(api.CodigoInstalacao));
        Assert.Equal(HttpStatusCode.NotFound, segunda.StatusCode);

        var conteudo = await File.ReadAllBytesAsync(api.CaminhoCodigoInstalacao);
        Assert.Equal(0L, BinaryPrimitives.ReadInt64BigEndian(conteudo));
    }

    [Fact]
    public async Task Codigo_com_mais_de_24h_responde_403()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco, idadeDoCodigo: TimeSpan.FromHours(25));

        var resposta = await api.CreateClient().PostAsJsonAsync(RotaCadastro, Pedido(api.CodigoInstalacao));

        await AssertProblema403Async(resposta);
        await AssertNadaGravadoAsync(banco);
    }

    [Fact]
    public async Task Dois_cadastros_simultaneos_criam_um_so_casal()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco);
        var cliente = api.CreateClient();

        var respostas = await Task.WhenAll(
            Task.Run(() => cliente.PostAsJsonAsync(RotaCadastro, Pedido(api.CodigoInstalacao))),
            Task.Run(() => cliente.PostAsJsonAsync(RotaCadastro, Pedido(api.CodigoInstalacao))));

        var status = respostas.Select(r => (int)r.StatusCode).Order().ToArray();
        Assert.Equal([201, 404], status);

        await using var db = await AbrirSuperusuarioAsync(banco);
        Assert.Equal(1L, await EscalarAsync<long>(db, "SELECT count(*) FROM identidade.casais"));
    }

    [Fact]
    public async Task Codigo_de_instalacao_errado_responde_403_e_nao_grava_nada()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco);

        var resposta = await api.CreateClient().PostAsJsonAsync(RotaCadastro, Pedido(EnvelopeDeChaves.GerarCodigoRecuperacao()));

        await AssertProblema403Async(resposta);
        await AssertNadaGravadoAsync(banco);
    }

    [Fact]
    public async Task Codigo_de_instalacao_gigante_responde_403()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco);
        var gigante = api.CodigoInstalacao + new string('A', 100_000);

        var resposta = await api.CreateClient().PostAsJsonAsync(RotaCadastro, Pedido(gigante));

        await AssertProblema403Async(resposta);
        await AssertNadaGravadoAsync(banco);
    }

    [Fact]
    public async Task Erro_de_validacao_nao_ecoa_cpf_nem_senha()
    {
        var banco = await pg.NovoBancoAsync();
        await using var api = new ApiDeTeste(banco);
        const string cpfInvalido = "52998224700";
        const string senhaCurta = "curta-77";
        var pedido = Pedido(api.CodigoInstalacao) with
        {
            Conjuge1 = new ConjugeNovoDto("Ana Teste", cpfInvalido, "senha-da-ana-123"),
            Conjuge2 = new ConjugeNovoDto("Bruno Teste", CpfSintetico.Gerar(2), senhaCurta),
        };

        var resposta = await api.CreateClient().PostAsJsonAsync(RotaCadastro, pedido);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        var corpo = await resposta.Content.ReadAsStringAsync();
        Assert.DoesNotContain(cpfInvalido, corpo);
        Assert.DoesNotContain(senhaCurta, corpo);

        using var json = JsonDocument.Parse(corpo);
        var erros = json.RootElement.GetProperty("errors");
        var cpf = erros.GetProperty("conjuge1.cpf").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["CPF inválido."], cpf);
        Assert.True(erros.TryGetProperty("conjuge2.senha", out _));

        await AssertNadaGravadoAsync(banco);
    }

    private static async Task AssertProblema403Async(HttpResponseMessage resposta)
    {
        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
        using var json = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync());
        Assert.Equal("Código de instalação incorreto ou expirado.", json.RootElement.GetProperty("title").GetString());
    }

    private static async Task AssertNadaGravadoAsync(BancoDeTeste banco)
    {
        await using var db = await AbrirSuperusuarioAsync(banco);
        Assert.Equal(0L, await EscalarAsync<long>(db, "SELECT count(*) FROM audit.eventos"));
        Assert.Equal(0L, await EscalarAsync<long>(db, "SELECT count(*) FROM identidade.casais"));
    }

    private sealed record UsuarioGravado(Guid Id, Guid CasalId, string NomeCompleto, byte[] CpfCifrado, Guid? ConjugeId);

    private sealed record ChaveGravada(Guid CasalId, string Tipo, Guid? UsuarioId, byte[] DekCifrada, byte[] Salt, int Versao);

    private static async Task<List<UsuarioGravado>> UsuariosAsync(NpgsqlConnection db)
    {
        var lista = new List<UsuarioGravado>();
        await using var cmd = new NpgsqlCommand(
            "SELECT id, casal_id, nome_completo, cpf_cifrado, conjuge_id FROM identidade.usuarios ORDER BY nome_completo", db);
        await using var leitor = await cmd.ExecuteReaderAsync();
        while (await leitor.ReadAsync())
            lista.Add(new UsuarioGravado(
                leitor.GetGuid(0), leitor.GetGuid(1), leitor.GetString(2),
                (byte[])leitor[3], leitor.IsDBNull(4) ? null : leitor.GetGuid(4)));
        return lista;
    }

    private static async Task<List<ChaveGravada>> ChavesAsync(NpgsqlConnection db)
    {
        var lista = new List<ChaveGravada>();
        await using var cmd = new NpgsqlCommand(
            "SELECT casal_id, tipo, usuario_id, dek_cifrada, salt, versao FROM identidade.chaves", db);
        await using var leitor = await cmd.ExecuteReaderAsync();
        while (await leitor.ReadAsync())
            lista.Add(new ChaveGravada(
                leitor.GetGuid(0), leitor.GetString(1), leitor.IsDBNull(2) ? null : leitor.GetGuid(2),
                (byte[])leitor[3], (byte[])leitor[4], leitor.GetInt32(5)));
        return lista;
    }

    private static async Task<List<string>> TextosAsync(NpgsqlConnection db, string sql)
    {
        var lista = new List<string>();
        await using var cmd = new NpgsqlCommand(sql, db);
        await using var leitor = await cmd.ExecuteReaderAsync();
        while (await leitor.ReadAsync()) lista.Add(leitor.GetString(0));
        return lista;
    }

    private static async Task<NpgsqlConnection> AbrirSuperusuarioAsync(BancoDeTeste banco)
    {
        var db = new NpgsqlConnection(banco.ConexaoSuperusuario);
        await db.OpenAsync();
        return db;
    }

    private static async Task<T> EscalarAsync<T>(NpgsqlConnection db, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, db);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}
