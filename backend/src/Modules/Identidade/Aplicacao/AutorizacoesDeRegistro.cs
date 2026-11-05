using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.Modules.Identidade.Dominio;
using GestorFinanceiro.SharedKernel.Seguranca;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace GestorFinanceiro.Modules.Identidade.Aplicacao;

internal enum OrigemDoRegistro
{
    PrimeiroAcesso,
    AprovacaoDoConjuge,
    CodigoDeRecuperacao,
    UsuarioLogado,
}

/// <summary>Permissão curta (10 min) para registrar um dispositivo. Não carrega dado pessoal.</summary>
internal sealed record AutorizacaoDeRegistro(Guid UsuarioId, Guid CasalId, OrigemDoRegistro Origem, DateTimeOffset ExpiraEm);

/// <summary>
/// Guarda as autorizações de registro em memória, pelo HMAC do token (nunca o token em claro).
/// É um processo só; um restart invalida as autorizações pendentes.
/// </summary>
internal sealed class AutorizacoesDeRegistro(IMemoryCache cache, ICofre cofre, TimeProvider relogio)
{
    internal const string PropositoDoToken = "registro";
    private const int TamanhoMaximoDoToken = 128;

    public string Emitir(Guid usuarioId, Guid casalId, OrigemDoRegistro origem)
    {
        var token = ServicoDeSessoes.GerarToken();
        var autorizacao = new AutorizacaoDeRegistro(usuarioId, casalId, origem, relogio.GetUtcNow() + TemposDoLogin.AutorizacaoDeRegistro);
        cache.Set(Chave(token), autorizacao, TemposDoLogin.AutorizacaoDeRegistro);
        return token;
    }

    public AutorizacaoDeRegistro? Consultar(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > TamanhoMaximoDoToken) return null;
        if (!cache.TryGetValue(Chave(token), out AutorizacaoDeRegistro? autorizacao) || autorizacao is null) return null;
        return relogio.GetUtcNow() < autorizacao.ExpiraEm ? autorizacao : null;
    }

    public void Consumir(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > TamanhoMaximoDoToken) return;
        cache.Remove(Chave(token));
    }

    public static void EscreverCookie(HttpResponse resposta, string token) =>
        resposta.Cookies.Append(NomesDoLogin.CookieDeRegistro, token, ServicoDeSessoes.OpcoesDoCookie(TemposDoLogin.AutorizacaoDeRegistro));

    private string Chave(string token) => "registro:" + Convert.ToBase64String(cofre.IndiceCego(token, PropositoDoToken));
}
