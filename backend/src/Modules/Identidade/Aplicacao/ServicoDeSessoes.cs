using Microsoft.Extensions.DependencyInjection;
using System.Buffers.Text;
using System.Security.Cryptography;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.Modules.Identidade.Dominio;
using GestorFinanceiro.Modules.Identidade.Infra;
using GestorFinanceiro.SharedKernel.Persistencia;
using GestorFinanceiro.SharedKernel.Seguranca;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GestorFinanceiro.Modules.Identidade.Aplicacao;

/// <summary>Cria, valida, confirma e encerra sessões. Só o HMAC do token vai ao banco.</summary>
internal sealed class ServicoDeSessoes(IdentidadeDbContext db, ICofre cofre, TimeProvider relogio) : ISessoes
{
    internal const string PropositoDoToken = "sessao";

    /// <summary>O token tem 43 caracteres; acima disso nem é conferido.</summary>
    private const int TamanhoMaximoDoToken = 128;

    public async Task<(string Token, DateTimeOffset ExpiraEm)> CriarAsync(Usuario usuario, CancellationToken ct)
    {
        var token = GerarToken();
        var sessao = Sessao.Criar(usuario.CasalId, usuario.Id, cofre.IndiceCego(token, PropositoDoToken), relogio.GetUtcNow());
        db.Sessoes.Add(sessao);
        await db.SaveChangesAsync(ct);
        return (token, sessao.ExpiraEm);
    }

    public async Task<SessaoValida?> ValidarAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || token.Length > TamanhoMaximoDoToken) return null;
        var hmac = cofre.IndiceCego(token, PropositoDoToken);

        // O casal ainda não é conhecido: só o filtro de soft delete continua ativo.
        var sessao = await db.Sessoes
            .IgnoreQueryFilters([Filtros.Casal])
            .FirstOrDefaultAsync(s => s.TokenHmac == hmac, ct);

        var agora = relogio.GetUtcNow();
        if (sessao is null || !sessao.Valida(agora)) return null;

        // A sessão só vale enquanto o usuário estiver ativo (filtro SoftDelete mantido).
        var usuarioAtivo = await db.Usuarios
            .IgnoreQueryFilters([Filtros.Casal])
            .AnyAsync(u => u.Id == sessao.UsuarioId, ct);
        if (!usuarioAtivo) return null;

        if (sessao.RegistrarUso(agora)) await db.SaveChangesAsync(ct);
        return new SessaoValida(sessao.Id, sessao.UsuarioId, sessao.CasalId, sessao.ConfirmadaEm);
    }

    public async Task EncerrarAsync(Guid sessaoId, CancellationToken ct)
    {
        var sessao = await db.Sessoes.FirstOrDefaultAsync(s => s.Id == sessaoId, ct);
        if (sessao is null) return;
        db.Sessoes.Remove(sessao); // o SoftDeleteInterceptor transforma em is_deleted = true
        await db.SaveChangesAsync(ct);
    }

    public async Task ConfirmarAsync(Guid sessaoId, CancellationToken ct)
    {
        var sessao = await db.Sessoes.FirstOrDefaultAsync(s => s.Id == sessaoId, ct);
        if (sessao is null) return;
        sessao.Confirmar(relogio.GetUtcNow());
        await db.SaveChangesAsync(ct);
    }

    public static void EscreverCookie(HttpResponse resposta, string token, DateTimeOffset expiraEm)
    {
        var agora = resposta.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        var restante = expiraEm - agora;
        resposta.Cookies.Append(NomesDoLogin.CookieDeSessao, token, OpcoesDoCookie(restante > TimeSpan.Zero ? restante : TimeSpan.Zero));
    }

    public static void ApagarCookie(HttpResponse resposta) =>
        resposta.Cookies.Delete(NomesDoLogin.CookieDeSessao, OpcoesDoCookie(null));

    internal static CookieOptions OpcoesDoCookie(TimeSpan? duracao) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        MaxAge = duracao,
        IsEssential = true,
    };

    /// <summary>32 bytes aleatórios em base64url.</summary>
    internal static string GerarToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
}
