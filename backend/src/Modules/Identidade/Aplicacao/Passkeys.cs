using System.Buffers.Text;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.Modules.Identidade.Dominio;
using GestorFinanceiro.Modules.Identidade.Infra;
using GestorFinanceiro.SharedKernel.Persistencia;
using GestorFinanceiro.SharedKernel.Seguranca;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace GestorFinanceiro.Modules.Identidade.Aplicacao;

/// <summary>Configuração <c>Seguranca:WebAuthn</c>: RpId (o domínio) e as origens aceitas no clientDataJSON.</summary>
internal sealed class OpcoesDoWebAuthn
{
    public const string Secao = "Seguranca:WebAuthn";
    public const string NomeDoServidor = "Gestor Financeiro";

    public string RpId { get; set; } = string.Empty;
    public string[] Origens { get; set; } = [];

    public bool EhValida() =>
        !string.IsNullOrWhiteSpace(RpId) && Origens.Length > 0 && Origens.All(o => Uri.TryCreate(o, UriKind.Absolute, out _));

    public Fido2Configuration ParaFido2() => new()
    {
        RPID = RpId,
        RPName = NomeDoServidor,
        Origins = new HashSet<string>(Origens, StringComparer.Ordinal),
        Timeout = (uint)TemposDoLogin.DesafioWebAuthn.TotalMilliseconds,
        ChallengeSize = 32,
    };
}

/// <summary>
/// Desafios WebAuthn pendentes, em memória por 5 min e de uso único: o primeiro a retirar leva, com sucesso ou não.
/// No registro, o desafio fica ligado à autorização (pelo HMAC do token); no login, ao próprio desafio.
/// </summary>
internal sealed class DesafiosWebAuthn(IMemoryCache cache, ICofre cofre, TimeProvider relogio)
{
    private const int TamanhoMaximoDoToken = 128;
    private const int TamanhoMaximoDoDesafio = 128;

    /// <summary>Caixa trocada atomicamente: duas requisições com o mesmo desafio não levam as duas.</summary>
    private sealed class Pendente(object valor, DateTimeOffset expiraEm)
    {
        private object? _valor = valor;
        public DateTimeOffset ExpiraEm { get; } = expiraEm;
        public object? Retirar() => Interlocked.Exchange(ref _valor, null);
    }

    public sealed record DeRegistro(Guid UsuarioId, CredentialCreateOptions Opcoes);

    public void GuardarRegistro(string token, Guid usuarioId, CredentialCreateOptions opcoes) =>
        Guardar(ChaveDeRegistro(token), new DeRegistro(usuarioId, opcoes));

    public DeRegistro? RetirarRegistro(string? token) =>
        string.IsNullOrEmpty(token) || token.Length > TamanhoMaximoDoToken ? null : Retirar(ChaveDeRegistro(token)) as DeRegistro;

    public void GuardarLogin(AssertionOptions opcoes) =>
        Guardar(ChaveDeLogin(Base64Url.EncodeToString(opcoes.Challenge)), opcoes);

    public AssertionOptions? RetirarLogin(string? desafio) =>
        string.IsNullOrEmpty(desafio) || desafio.Length > TamanhoMaximoDoDesafio ? null : Retirar(ChaveDeLogin(desafio)) as AssertionOptions;

    /// <summary>Na confirmação, o desafio fica ligado à sessão: um por sessão, e o novo substitui o anterior.</summary>
    public void GuardarConfirmacao(Guid sessaoId, AssertionOptions opcoes) => Guardar(ChaveDeConfirmacao(sessaoId), opcoes);

    public AssertionOptions? RetirarConfirmacao(Guid sessaoId) => Retirar(ChaveDeConfirmacao(sessaoId)) as AssertionOptions;

    private void Guardar(string chave, object valor) =>
        cache.Set(chave, new Pendente(valor, relogio.GetUtcNow() + TemposDoLogin.DesafioWebAuthn), TemposDoLogin.DesafioWebAuthn);

    private object? Retirar(string chave)
    {
        if (!cache.TryGetValue(chave, out Pendente? pendente) || pendente is null) return null;
        cache.Remove(chave);
        var valor = pendente.Retirar();
        return relogio.GetUtcNow() < pendente.ExpiraEm ? valor : null;
    }

    private string ChaveDeRegistro(string token) =>
        "webauthn:registro:" + Convert.ToBase64String(cofre.IndiceCego(token, AutorizacoesDeRegistro.PropositoDoToken));

    private static string ChaveDeLogin(string desafio) => "webauthn:login:" + desafio;

    private static string ChaveDeConfirmacao(Guid sessaoId) => "webauthn:confirmacao:" + sessaoId.ToString("N");
}

internal abstract record ResultadoDaPasskey
{
    private ResultadoDaPasskey() { }

    public sealed record OpcoesDeRegistro(OpcoesDeRegistroDePasskeyDto Dto) : ResultadoDaPasskey
    {
        public override string ToString() => nameof(OpcoesDeRegistro);
    }

    public sealed record Entrou(string Token, DateTimeOffset ExpiraEm) : ResultadoDaPasskey
    {
        public override string ToString() => nameof(Entrou);
    }

    /// <summary>Sem autorização de registro válida.</summary>
    public sealed record NaoAutorizado : ResultadoDaPasskey;

    public sealed record ApelidoInvalido : ResultadoDaPasskey;

    public sealed record Falhou : ResultadoDaPasskey;

    public sealed record Bloqueado : ResultadoDaPasskey;
}

/// <summary>
/// Registro e login por passkey (credencial descobrível, verificação do usuário obrigatória, attestation none).
/// Toda conferência de uma credencial conhecida passa por <see cref="TentativaDeLogin"/>.
/// </summary>
internal sealed class ServicoDePasskeys(
    IdentidadeDbContext db,
    IFido2 fido2,
    OpcoesDoWebAuthn webAuthn,
    DesafiosWebAuthn desafios,
    TentativaDeLogin tentativa,
    AutorizacoesDeRegistro autorizacoes,
    ServicoDeSessoes sessoes,
    TimeProvider relogio)
{
    private const int TamanhoMaximoDoApelido = 60;
    private const string TipoChavePublica = "public-key";
    private const string Obrigatorio = "required";

    private static readonly PubKeyCredParam[] Algoritmos = [PubKeyCredParam.ES256, PubKeyCredParam.Ed25519, PubKeyCredParam.RS256];

    public async Task<ResultadoDaPasskey> OpcoesDeRegistroAsync(string? token, CancellationToken ct)
    {
        if (autorizacoes.Consultar(token) is not { } autorizacao) return new ResultadoDaPasskey.NaoAutorizado();

        // O casal ainda não é conhecido na requisição: só o filtro de soft delete continua ativo.
        var usuario = await db.Usuarios
            .IgnoreQueryFilters([Filtros.Casal])
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == autorizacao.UsuarioId, ct);
        if (usuario is null) return new ResultadoDaPasskey.NaoAutorizado();

        var ativas = await db.Credenciais
            .IgnoreQueryFilters([Filtros.Casal])
            .AsNoTracking()
            .Where(c => c.UsuarioId == usuario.Id && c.Tipo == TipoCredencial.Passkey)
            .Select(c => c.CredentialId!)
            .ToListAsync(ct);

        var apelido = usuario.NomeCompleto.Split(' ', 2)[0];
        var opcoes = fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = usuario.Id.ToByteArray(), Name = apelido, DisplayName = apelido },
            ExcludeCredentials = ativas.Select(id => new PublicKeyCredentialDescriptor(id)).ToList(),
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Required,
            },
            AttestationPreference = AttestationConveyancePreference.None,
            PubKeyCredParams = Algoritmos,
        });
        desafios.GuardarRegistro(token!, usuario.Id, opcoes);

        return new ResultadoDaPasskey.OpcoesDeRegistro(new OpcoesDeRegistroDePasskeyDto(
            new RpDePasskeyDto(webAuthn.RpId, OpcoesDoWebAuthn.NomeDoServidor),
            new UsuarioDePasskeyDto(Base64Url.EncodeToString(opcoes.User.Id), apelido, apelido),
            Base64Url.EncodeToString(opcoes.Challenge),
            Algoritmos.Select(p => new ParametroDeChaveDto(TipoChavePublica, (int)p.Alg)).ToList(),
            (long)TemposDoLogin.DesafioWebAuthn.TotalMilliseconds,
            ativas.Select(id => new DescritorDeCredencialDto(Base64Url.EncodeToString(id), TipoChavePublica)).ToList(),
            new SelecaoDeAutenticadorDto(Obrigatorio, true, Obrigatorio),
            "none"));
    }

    public async Task<ResultadoDaPasskey> RegistrarAsync(string? token, NovaPasskeyDto pedido, CancellationToken ct)
    {
        if (autorizacoes.Consultar(token) is not { } autorizacao) return new ResultadoDaPasskey.NaoAutorizado();
        var apelidoDoDispositivo = pedido.Apelido?.Trim();
        if (string.IsNullOrEmpty(apelidoDoDispositivo) || apelidoDoDispositivo.Length > TamanhoMaximoDoApelido)
            return new ResultadoDaPasskey.ApelidoInvalido();

        // O desafio sai do cache já aqui: vale uma vez, dê certo ou não.
        if (desafios.RetirarRegistro(token) is not { } pendente || pendente.UsuarioId != autorizacao.UsuarioId)
            return new ResultadoDaPasskey.Falhou();
        if (ParaAtestacao(pedido.Resposta) is not { } resposta) return new ResultadoDaPasskey.Falhou();

        var usuarioId = autorizacao.UsuarioId;
        var agora = relogio.GetUtcNow();
        var resultado = await tentativa.ExecutarAsync(
            usuarioId,
            async (usuario, semCancelar) =>
            {
                // Sob o lock do cônjuge, a autorização só serve uma vez, mesmo com requisições paralelas.
                if (autorizacoes.Consultar(token) is null) return false;
                RegisteredPublicKeyCredential credencial;
                try
                {
                    credencial = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
                    {
                        AttestationResponse = resposta,
                        OriginalOptions = pendente.Opcoes,
                        IsCredentialIdUniqueToUserCallback = async (p, c) => !await db.Credenciais
                            .IgnoreQueryFilters([Filtros.Casal])
                            .AnyAsync(x => x.CredentialId == p.CredentialId, c),
                    }, semCancelar);
                }
                catch (Fido2VerificationException)
                {
                    return false;
                }

                db.Credenciais.Add(Credencial.CriarPasskey(
                    usuario.CasalId, usuario.Id, apelidoDoDispositivo, credencial.Id, credencial.PublicKey, credencial.SignCount));
                usuario.EncerrarPrimeiroAcesso(agora);
                autorizacoes.Consumir(token!);
                return true;
            },
            ct: ct);

        return resultado switch
        {
            ResultadoDaTentativa.Sucesso => await CriarSessaoAsync(usuarioId, ct),
            ResultadoDaTentativa.Bloqueado => new ResultadoDaPasskey.Bloqueado(),
            _ => new ResultadoDaPasskey.Falhou(),
        };
    }

    public OpcoesDeLoginComPasskeyDto OpcoesDeLogin()
    {
        var opcoes = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [],
            UserVerification = UserVerificationRequirement.Required,
        });
        desafios.GuardarLogin(opcoes);
        return new OpcoesDeLoginComPasskeyDto(
            Base64Url.EncodeToString(opcoes.Challenge),
            (long)TemposDoLogin.DesafioWebAuthn.TotalMilliseconds,
            webAuthn.RpId,
            Obrigatorio);
    }

    public async Task<ResultadoDaPasskey> EntrarAsync(RespostaDeLoginComPasskeyDto pedido, CancellationToken ct)
    {
        // Resposta malformada, desafio desconhecido, vencido ou já usado: 401 sem contar.
        if (ParaAssercao(pedido) is not { } resposta) return new ResultadoDaPasskey.Falhou();
        if (desafios.RetirarLogin(LerDesafio(resposta.Response.ClientDataJson)) is not { } opcoes) return new ResultadoDaPasskey.Falhou();

        // O casal ainda não é conhecido: só o filtro de soft delete continua ativo (credencial removida não entra).
        var rawId = resposta.RawId;
        var conhecida = await db.Credenciais
            .IgnoreQueryFilters([Filtros.Casal])
            .AsNoTracking()
            .Where(c => c.CredentialId == rawId && c.Tipo == TipoCredencial.Passkey && c.Confirmada)
            .Select(c => new { c.Id, c.UsuarioId })
            .FirstOrDefaultAsync(ct);
        if (conhecida is null) return new ResultadoDaPasskey.Falhou();

        var agora = relogio.GetUtcNow();
        var resultado = await tentativa.ExecutarAsync(
            conhecida.UsuarioId,
            async (_, semCancelar) =>
            {
                var credencial = await db.Credenciais
                    .IgnoreQueryFilters([Filtros.Casal])
                    .AsTracking()
                    .FirstOrDefaultAsync(c => c.Id == conhecida.Id && c.Tipo == TipoCredencial.Passkey && c.Confirmada, semCancelar);
                return credencial is not null && await VerificarAssercaoAsync(credencial, resposta, opcoes, agora, semCancelar);
            },
            ct: ct);

        return resultado switch
        {
            ResultadoDaTentativa.Sucesso => await CriarSessaoAsync(conhecida.UsuarioId, ct),
            ResultadoDaTentativa.Bloqueado => new ResultadoDaPasskey.Bloqueado(),
            _ => new ResultadoDaPasskey.Falhou(),
        };
    }

    /// <summary>Opções de asserção para confirmar a identidade do usuário da sessão: só as passkeys dele.</summary>
    public async Task<OpcoesDeConfirmacaoDto> OpcoesDeConfirmacaoAsync(Guid usuarioId, Guid sessaoId, CancellationToken ct)
    {
        // Usuário da sessão: o filtro Casal continua ligado.
        var ativas = await db.Credenciais
            .AsNoTracking()
            .Where(c => c.UsuarioId == usuarioId && c.Tipo == TipoCredencial.Passkey && c.Confirmada)
            .Select(c => c.CredentialId!)
            .ToListAsync(ct);
        var opcoes = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = ativas.Select(id => new PublicKeyCredentialDescriptor(id)).ToList(),
            UserVerification = UserVerificationRequirement.Required,
        });
        desafios.GuardarConfirmacao(sessaoId, opcoes);
        return new OpcoesDeConfirmacaoDto(
            Base64Url.EncodeToString(opcoes.Challenge),
            (long)TemposDoLogin.DesafioWebAuthn.TotalMilliseconds,
            webAuthn.RpId,
            ativas.Select(id => new DescritorDeCredencialDto(Base64Url.EncodeToString(id), TipoChavePublica)).ToList(),
            Obrigatorio);
    }

    /// <summary>
    /// Confere, dentro de <see cref="TentativaDeLogin"/>, a asserção de uma passkey do próprio usuário da sessão
    /// contra o desafio ligado à sessão. Credencial de outra pessoa ou desconhecida é falha (contada pela tentativa).
    /// </summary>
    internal async Task<bool> ConferirConfirmacaoAsync(
        Guid usuarioId, AuthenticatorAssertionRawResponse resposta, AssertionOptions opcoes, DateTimeOffset agora)
    {
        var semCancelar = CancellationToken.None;
        var rawId = resposta.RawId;
        var credencial = await db.Credenciais
            .AsTracking()
            .FirstOrDefaultAsync(c => c.CredentialId == rawId && c.UsuarioId == usuarioId
                && c.Tipo == TipoCredencial.Passkey && c.Confirmada, semCancelar);
        return credencial is not null && await VerificarAssercaoAsync(credencial, resposta, opcoes, agora, semCancelar);
    }

    /// <summary>User handle do dono, assinatura, desafio, origem, UV e contador crescente.</summary>
    private async Task<bool> VerificarAssercaoAsync(
        Credencial credencial, AuthenticatorAssertionRawResponse resposta, AssertionOptions opcoes, DateTimeOffset agora, CancellationToken ct)
    {
        var dono = credencial.UsuarioId.ToByteArray();
        if (resposta.Response.UserHandle is not { } handle || !handle.AsSpan().SequenceEqual(dono)) return false;

        var anterior = credencial.Contador ?? 0;
        VerifyAssertionResult verificado;
        try
        {
            verificado = await fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = resposta,
                OriginalOptions = opcoes,
                StoredPublicKey = credencial.ChavePublica!,
                StoredSignatureCounter = (uint)anterior,
                IsUserHandleOwnerOfCredentialIdCallback = (p, _) => Task.FromResult(
                    p.UserHandle.AsSpan().SequenceEqual(dono) && p.CredentialId.AsSpan().SequenceEqual(credencial.CredentialId)),
            }, ct);
        }
        catch (Fido2VerificationException)
        {
            return false;
        }

        // O contador tem que crescer; só um autenticador sem contador (0 e 0) passa sem crescer.
        var contador = (long)verificado.SignCount;
        if (contador <= anterior && !(contador == 0 && anterior == 0)) return false;
        credencial.RegistrarUsoPasskey(contador, agora);
        return true;
    }

    private static AuthenticatorAttestationRawResponse? ParaAtestacao(RespostaDeRegistroDePasskeyDto? dto)
    {
        if (dto?.Response is null) return null;
        if (Decodificar(dto.RawId) is not { } rawId
            || Decodificar(dto.Response.ClientDataJson) is not { } clientData
            || Decodificar(dto.Response.AttestationObject) is not { } atestacao)
            return null;
        return new AuthenticatorAttestationRawResponse
        {
            Id = dto.Id,
            RawId = rawId,
            Type = TipoDaCredencial(dto.Type),
            Response = new AuthenticatorAttestationRawResponse.AttestationResponse
            {
                ClientDataJson = clientData,
                AttestationObject = atestacao,
                Transports = [],
            },
        };
    }

    internal static AuthenticatorAssertionRawResponse? ParaAssercao(RespostaDeLoginComPasskeyDto? dto)
    {
        if (dto?.Response is null) return null;
        if (Decodificar(dto.RawId) is not { } rawId
            || Decodificar(dto.Response.ClientDataJson) is not { } clientData
            || Decodificar(dto.Response.AuthenticatorData) is not { } authData
            || Decodificar(dto.Response.Signature) is not { } assinatura)
            return null;
        byte[]? userHandle = null;
        if (dto.Response.UserHandle is { Length: > 0 } handle && (userHandle = Decodificar(handle)) is null) return null;
        return new AuthenticatorAssertionRawResponse
        {
            Id = dto.Id,
            RawId = rawId,
            Type = TipoDaCredencial(dto.Type),
            Response = new AuthenticatorAssertionRawResponse.AssertionResponse
            {
                ClientDataJson = clientData,
                AuthenticatorData = authData,
                Signature = assinatura,
                UserHandle = userHandle,
            },
        };
    }

    private static PublicKeyCredentialType TipoDaCredencial(string? tipo) =>
        tipo == TipoChavePublica ? PublicKeyCredentialType.PublicKey : PublicKeyCredentialType.Invalid;

    private static byte[]? Decodificar(string? base64Url)
    {
        if (string.IsNullOrEmpty(base64Url)) return null;
        try
        {
            return Base64Url.DecodeFromChars(base64Url);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Lê só o desafio do clientDataJSON, para achar as opções pendentes; o resto quem confere é o Fido2.</summary>
    private static string? LerDesafio(byte[] clientDataJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(clientDataJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("challenge", out var desafio)
                && desafio.ValueKind == JsonValueKind.String
                ? desafio.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<ResultadoDaPasskey> CriarSessaoAsync(Guid usuarioId, CancellationToken ct)
    {
        var usuario = await db.Usuarios
            .IgnoreQueryFilters([Filtros.Casal])
            .AsNoTracking()
            .FirstAsync(u => u.Id == usuarioId, ct);
        var (token, expiraEm) = await sessoes.CriarAsync(usuario, ct);
        return new ResultadoDaPasskey.Entrou(token, expiraEm);
    }
}
