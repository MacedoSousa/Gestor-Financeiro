using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.Modules.Identidade.Dominio;
using GestorFinanceiro.Modules.Identidade.Infra;
using GestorFinanceiro.SharedKernel.Aplicacao;
using GestorFinanceiro.SharedKernel.Persistencia;
using GestorFinanceiro.SharedKernel.Seguranca;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GestorFinanceiro.Modules.Identidade.Aplicacao;

internal static class EstadosDoPedido
{
    public const string Pendente = "PENDENTE";
    public const string Aprovado = "APROVADO";
    public const string Expirado = "EXPIRADO";
}

internal enum ResultadoDaAprovacao
{
    Aprovado,
    /// <summary>O pedido é do próprio usuário logado.</summary>
    ProprioDono,
    /// <summary>Desconhecido, de outro casal, expirado ou que já não está pendente.</summary>
    NaoEncontrado,
}

/// <summary>
/// Pedido de celular novo: o celular informa o CPF e recebe um código de 6 dígitos; o outro cônjuge, logado e com
/// confirmação recente, confere o código e aprova; o celular resgata a autorização de registro.
/// </summary>
/// <remarks>
/// O cookie do pedido é <c>{token}.{expira em, segundos Unix}.{HMAC de token e validade}</c>. O HMAC (índice cego com
/// propósito próprio) impede adulterar a validade. Só o HMAC do token vai ao banco. Para um CPF desconhecido nada é
/// gravado, mas o cookie tem o mesmo formato e tamanho: a consulta responde PENDENTE até a validade assinada e
/// EXPIRADO depois, exatamente como um pedido real que ninguém aprovou.
/// </remarks>
internal sealed class PedidosDeDispositivo(
    IdentidadeDbContext db,
    ICofre cofre,
    TentativaDeLogin tentativa,
    AutorizacoesDeRegistro autorizacoes,
    IUsuarioAtual atual,
    TimeProvider relogio)
{
    internal const string PropositoDoToken = "pedido";
    private const string PropositoDoCookie = "pedido-cookie";
    private const int TamanhoMaximoDoCookie = 128;

    /// <summary>Cria o pedido (se o CPF existir) e devolve o código e o valor do cookie. A resposta não depende do CPF.</summary>
    public async Task<(string Codigo, string Cookie)> CriarAsync(string cpf, CancellationToken ct)
    {
        var token = ServicoDeSessoes.GerarToken();
        var codigo = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        // Em segundos inteiros: a validade do cookie (CPF desconhecido) e a do banco (pedido real) vencem no mesmo instante.
        var agora = DateTimeOffset.FromUnixTimeSeconds(relogio.GetUtcNow().ToUnixTimeSeconds());
        var expiraEm = agora + TemposDoLogin.Pedido;

        if (await BuscarUsuarioAsync(cpf, ct) is { } usuario)
        {
            db.PedidosDeDispositivo.Add(PedidoDeDispositivo.Criar(
                usuario.CasalId, usuario.Id, cofre.IndiceCego(token, PropositoDoToken), codigo, agora));
            await db.SaveChangesAsync(ct);
        }

        return (codigo, MontarCookie(token, expiraEm));
    }

    /// <summary>Só lê: nenhuma escrita no banco, nenhum cookie. Cookie ausente ou inválido: PENDENTE.</summary>
    public async Task<string> ConsultarAsync(string? cookie, CancellationToken ct)
    {
        if (LerCookie(cookie) is not { } lido) return EstadosDoPedido.Pendente;
        var agora = relogio.GetUtcNow();
        var hmac = cofre.IndiceCego(lido.Token, PropositoDoToken);

        // O celular ainda não tem sessão: só o filtro de soft delete continua ativo.
        var pedido = await db.PedidosDeDispositivo
            .IgnoreQueryFilters([Filtros.Casal])
            .AsNoTracking()
            .Where(p => p.TokenHmac == hmac)
            .Select(p => new { p.Estado, p.ExpiraEm })
            .FirstOrDefaultAsync(ct);

        if (pedido is null) return agora >= lido.ExpiraEm ? EstadosDoPedido.Expirado : EstadosDoPedido.Pendente;
        if (pedido.Estado == EstadoDoPedido.Usado || agora >= pedido.ExpiraEm) return EstadosDoPedido.Expirado;
        return pedido.Estado == EstadoDoPedido.Aprovado ? EstadosDoPedido.Aprovado : EstadosDoPedido.Pendente;
    }

    /// <summary>Pedido aprovado e no prazo vira autorização de registro (uso único); devolve o token, ou null.</summary>
    public async Task<string?> ResgatarAsync(string? cookie)
    {
        if (LerCookie(cookie) is not { } lido) return null;
        var semCancelar = CancellationToken.None;
        var hmac = cofre.IndiceCego(lido.Token, PropositoDoToken);

        var dono = await db.PedidosDeDispositivo
            .IgnoreQueryFilters([Filtros.Casal])
            .AsNoTracking()
            .Where(p => p.TokenHmac == hmac)
            .Select(p => new { p.UsuarioId, p.CasalId })
            .FirstOrDefaultAsync(semCancelar);
        if (dono is null) return null;

        // Com o lock do cônjuge, dois resgates paralelos do mesmo pedido não levam os dois: a leitura abaixo,
        // feita já com o lock, vê o USADO gravado pelo primeiro.
        await using var transacao = await db.Database.BeginTransactionAsync(semCancelar);
        await tentativa.TravarAsync(dono.UsuarioId);
        var pedido = await db.PedidosDeDispositivo
            .IgnoreQueryFilters([Filtros.Casal])
            .AsTracking()
            .FirstOrDefaultAsync(p => p.TokenHmac == hmac, semCancelar);
        if (pedido is null || pedido.Estado != EstadoDoPedido.Aprovado || pedido.Expirou(relogio.GetUtcNow()))
        {
            await transacao.RollbackAsync(semCancelar);
            return null;
        }
        pedido.MarcarUsado();
        await db.SaveChangesAsync(semCancelar);
        await transacao.CommitAsync(semCancelar);
        return autorizacoes.Emitir(dono.UsuarioId, dono.CasalId, OrigemDoRegistro.AprovacaoDoConjuge);
    }

    /// <summary>Pedidos pendentes e no prazo do outro cônjuge (o filtro Casal limita ao casal da sessão).</summary>
    public async Task<IReadOnlyList<PedidoPendenteDto>> PendentesDoConjugeAsync(CancellationToken ct)
    {
        if (atual.UsuarioId is not { } eu) return [];
        var agora = relogio.GetUtcNow();
        return await db.PedidosDeDispositivo
            .AsNoTracking()
            .Where(p => p.UsuarioId != eu && p.Estado == EstadoDoPedido.Pendente && p.ExpiraEm > agora)
            .OrderBy(p => p.ExpiraEm)
            .Select(p => new PedidoPendenteDto(p.Id, p.Codigo, p.ExpiraEm))
            .ToListAsync(ct);
    }

    public async Task<ResultadoDaAprovacao> AprovarAsync(Guid pedidoId, CancellationToken ct)
    {
        if (atual.UsuarioId is not { } eu) return ResultadoDaAprovacao.NaoEncontrado;
        // O filtro Casal continua ligado: pedido de outro casal não é encontrado.
        var pedido = await db.PedidosDeDispositivo.AsTracking().FirstOrDefaultAsync(p => p.Id == pedidoId, ct);
        if (pedido is null) return ResultadoDaAprovacao.NaoEncontrado;
        if (pedido.UsuarioId == eu) return ResultadoDaAprovacao.ProprioDono;
        var agora = relogio.GetUtcNow();
        if (pedido.Estado != EstadoDoPedido.Pendente || pedido.Expirou(agora)) return ResultadoDaAprovacao.NaoEncontrado;

        pedido.Aprovar(eu, agora);
        await db.SaveChangesAsync(ct);
        return ResultadoDaAprovacao.Aprovado;
    }

    public static void EscreverCookie(HttpResponse resposta, string valor) =>
        resposta.Cookies.Append(NomesDoLogin.CookieDePedido, valor, ServicoDeSessoes.OpcoesDoCookie(TemposDoLogin.Pedido));

    public static void ApagarCookie(HttpResponse resposta) =>
        resposta.Cookies.Delete(NomesDoLogin.CookieDePedido, ServicoDeSessoes.OpcoesDoCookie(null));

    private string MontarCookie(string token, DateTimeOffset expiraEm)
    {
        var validade = expiraEm.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"{token}.{validade}.{Base64Url.EncodeToString(Assinar(token, validade))}";
    }

    private (string Token, DateTimeOffset ExpiraEm)? LerCookie(string? cookie)
    {
        if (string.IsNullOrEmpty(cookie) || cookie.Length > TamanhoMaximoDoCookie) return null;
        var partes = cookie.Split('.');
        if (partes.Length != 3 || partes[0].Length == 0) return null;
        if (!long.TryParse(partes[1], NumberStyles.None, CultureInfo.InvariantCulture, out var segundos)) return null;

        byte[] informada;
        try
        {
            informada = Base64Url.DecodeFromChars(partes[2]);
        }
        catch (FormatException)
        {
            return null;
        }
        if (!CryptographicOperations.FixedTimeEquals(informada, Assinar(partes[0], partes[1]))) return null;

        try
        {
            return (partes[0], DateTimeOffset.FromUnixTimeSeconds(segundos));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private byte[] Assinar(string token, string validade) => cofre.IndiceCego($"{token}.{validade}", PropositoDoCookie);

    private async Task<Usuario?> BuscarUsuarioAsync(string cpf, CancellationToken ct)
    {
        if (!Cpf.TentarCriar(cpf, out var valido)) return null;
        var hmac = cofre.IndiceCego(valido.Digitos, Usuario.PropositoCpf);

        // O celular ainda não tem sessão: só o filtro de soft delete continua ativo.
        return await db.Usuarios
            .IgnoreQueryFilters([Filtros.Casal])
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.CpfHmac == hmac, ct);
    }
}
