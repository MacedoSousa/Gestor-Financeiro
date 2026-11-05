using GestorFinanceiro.Modules.Identidade.Dominio;
using GestorFinanceiro.Modules.Identidade.Infra;
using GestorFinanceiro.SharedKernel.Persistencia;
using GestorFinanceiro.SharedKernel.Seguranca;
using Microsoft.EntityFrameworkCore;

namespace GestorFinanceiro.Modules.Identidade.Aplicacao;

internal abstract record ResultadoDoTotp
{
    private ResultadoDoTotp() { }

    public sealed record Gerado(Guid CredencialId, string OtpauthUri) : ResultadoDoTotp
    {
        public override string ToString() => nameof(Gerado);
    }

    public sealed record Entrou(string Token, DateTimeOffset ExpiraEm) : ResultadoDoTotp
    {
        public override string ToString() => nameof(Entrou);
    }

    /// <summary>Sem autorização de registro válida.</summary>
    public sealed record NaoAutorizado : ResultadoDoTotp;

    public sealed record ApelidoInvalido : ResultadoDoTotp;

    public sealed record Falhou : ResultadoDoTotp;

    public sealed record Bloqueado : ResultadoDoTotp;
}

/// <summary>Registro, confirmação e login por TOTP. Toda conferência que conta falha passa por <see cref="TentativaDeLogin"/>.</summary>
internal sealed class ServicoDeTotp(
    IdentidadeDbContext db,
    ICofre cofre,
    TentativaDeLogin tentativa,
    AutorizacoesDeRegistro autorizacoes,
    ServicoDeSessoes sessoes,
    TimeProvider relogio)
{
    private const int TamanhoMaximoDoApelido = 60;
    private static readonly byte[] SegredoFalso = new byte[Totp.TamanhoDoSegredo];

    public async Task<ResultadoDoTotp> RegistrarAsync(string? token, string apelido, CancellationToken ct)
    {
        if (autorizacoes.Consultar(token) is not { } autorizacao) return new ResultadoDoTotp.NaoAutorizado();
        var apelidoDoDispositivo = apelido?.Trim();
        if (string.IsNullOrEmpty(apelidoDoDispositivo) || apelidoDoDispositivo.Length > TamanhoMaximoDoApelido)
            return new ResultadoDoTotp.ApelidoInvalido();

        // O casal ainda não é conhecido na requisição: só o filtro de soft delete continua ativo.
        var usuario = await db.Usuarios
            .IgnoreQueryFilters([Filtros.Casal])
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == autorizacao.UsuarioId, ct);
        if (usuario is null) return new ResultadoDoTotp.NaoAutorizado();

        var segredo = Totp.GerarSegredo();
        var base32 = Totp.ParaBase32(segredo);
        var credencial = Credencial.CriarTotp(usuario.CasalId, usuario.Id, apelidoDoDispositivo, base32, cofre);
        db.Credenciais.Add(credencial);
        await db.SaveChangesAsync(ct);
        return new ResultadoDoTotp.Gerado(credencial.Id, Totp.MontarUri(usuario.NomeCompleto.Split(' ', 2)[0], base32));
    }

    public async Task<ResultadoDoTotp> ConfirmarAsync(string? token, Guid credencialId, string codigo, CancellationToken ct)
    {
        if (autorizacoes.Consultar(token) is not { } autorizacao) return new ResultadoDoTotp.NaoAutorizado();
        var usuarioId = autorizacao.UsuarioId;
        var agora = relogio.GetUtcNow();

        var resultado = await tentativa.ExecutarAsync(
            usuarioId,
            async (usuario, _) =>
            {
                // Sob o lock do cônjuge, a autorização só serve uma vez, mesmo com requisições paralelas.
                if (autorizacoes.Consultar(token) is null) return false;
                var credencial = await db.Credenciais
                    .IgnoreQueryFilters([Filtros.Casal])
                    .AsTracking()
                    .FirstOrDefaultAsync(c => c.Id == credencialId && c.UsuarioId == usuarioId
                        && c.Tipo == TipoCredencial.Totp && !c.Confirmada, CancellationToken.None);
                if (credencial is null) return false;
                if (!Totp.Conferir(Totp.DeBase32(credencial.LerSegredo(cofre)), codigo, credencial.UltimoPasso ?? long.MinValue, agora, out var passo))
                    return false;
                credencial.Confirmar();
                credencial.RegistrarUsoTotp(passo, agora);
                usuario.EncerrarPrimeiroAcesso(agora);
                autorizacoes.Consumir(token!);
                return true;
            },
            ct: ct);

        return resultado switch
        {
            ResultadoDaTentativa.Sucesso => await CriarSessaoAsync(usuarioId, ct),
            ResultadoDaTentativa.Bloqueado => new ResultadoDoTotp.Bloqueado(),
            _ => new ResultadoDoTotp.Falhou(),
        };
    }

    public async Task<ResultadoDoTotp> EntrarAsync(string cpf, string codigo, CancellationToken ct)
    {
        var usuarioId = await BuscarUsuarioComTotpAsync(cpf, ct);

        // CPF desconhecido, malformado ou sem TOTP confirmado: mesma conferência (barata) contra um segredo falso
        // e nada é contado, para a resposta e o bloqueio não revelarem se o CPF existe.
        if (usuarioId is null)
        {
            Totp.Conferir(SegredoFalso, codigo, long.MinValue, relogio.GetUtcNow(), out _);
            return new ResultadoDoTotp.Falhou();
        }

        var agora = relogio.GetUtcNow();
        var id = usuarioId.Value;
        var resultado = await tentativa.ExecutarAsync(
            id,
            async (_, _) =>
            {
                var credenciais = await db.Credenciais
                    .IgnoreQueryFilters([Filtros.Casal])
                    .AsTracking()
                    .Where(c => c.UsuarioId == id && c.Tipo == TipoCredencial.Totp && c.Confirmada)
                    .ToListAsync(CancellationToken.None);
                if (credenciais.Count == 0)
                {
                    Totp.Conferir(SegredoFalso, codigo, long.MinValue, agora, out _);
                    return false;
                }
                foreach (var credencial in credenciais)
                {
                    if (!Totp.Conferir(Totp.DeBase32(credencial.LerSegredo(cofre)), codigo, credencial.UltimoPasso ?? long.MinValue, agora, out var passo))
                        continue;
                    credencial.RegistrarUsoTotp(passo, agora);
                    return true;
                }
                return false;
            },
            ct: ct);

        return resultado switch
        {
            ResultadoDaTentativa.Sucesso => await CriarSessaoAsync(id, ct),
            ResultadoDaTentativa.Bloqueado => new ResultadoDoTotp.Bloqueado(),
            _ => new ResultadoDoTotp.Falhou(),
        };
    }

    private async Task<Guid?> BuscarUsuarioComTotpAsync(string cpf, CancellationToken ct)
    {
        if (!Cpf.TentarCriar(cpf, out var valido)) return null;
        var hmac = cofre.IndiceCego(valido.Digitos, Usuario.PropositoCpf);

        // O casal ainda não é conhecido: só o filtro de soft delete continua ativo.
        var usuarioId = await db.Usuarios
            .IgnoreQueryFilters([Filtros.Casal])
            .AsNoTracking()
            .Where(u => u.CpfHmac == hmac)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(ct);
        if (usuarioId is null) return null;

        var temTotp = await db.Credenciais
            .IgnoreQueryFilters([Filtros.Casal])
            .AsNoTracking()
            .AnyAsync(c => c.UsuarioId == usuarioId && c.Tipo == TipoCredencial.Totp && c.Confirmada, ct);
        return temTotp ? usuarioId : null;
    }

    private async Task<ResultadoDoTotp> CriarSessaoAsync(Guid usuarioId, CancellationToken ct)
    {
        var usuario = await db.Usuarios
            .IgnoreQueryFilters([Filtros.Casal])
            .AsNoTracking()
            .FirstAsync(u => u.Id == usuarioId, ct);
        var (token, expiraEm) = await sessoes.CriarAsync(usuario, ct);
        return new ResultadoDoTotp.Entrou(token, expiraEm);
    }
}
