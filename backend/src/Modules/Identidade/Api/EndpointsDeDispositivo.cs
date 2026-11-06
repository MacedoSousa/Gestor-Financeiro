using GestorFinanceiro.Modules.Identidade.Aplicacao;
using GestorFinanceiro.Modules.Identidade.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace GestorFinanceiro.Modules.Identidade.Api;

/// <summary>
/// Celular novo: pedido (anônimo) aprovado pelo cônjuge logado, ou código de recuperação. E o destravar do cônjuge.
/// </summary>
internal static class EndpointsDeDispositivo
{
    public const string MensagemPedidosDemais =
        "Há pedidos demais aguardando aprovação. Aguarde alguns minutos ou peça ao outro cônjuge para aprovar.";

    public static IEndpointRouteBuilder MapDispositivo(this IEndpointRouteBuilder api)
    {
        var anonimo = api.MapGroup("/auth").WithTags("Autenticacao").AllowAnonymous();

        anonimo.MapPost("/dispositivo/pedido", async Task<Results<Ok<PedidoCriadoDto>, ProblemHttpResult>> (
            NovoPedidoDeDispositivoDto pedido, PedidosDeDispositivo pedidos, HttpResponse resposta, CancellationToken ct) =>
        {
            resposta.Headers.CacheControl = "no-store";
            if (await pedidos.CriarAsync(pedido.Cpf, ct) is not var (codigo, cookie))
                return TypedResults.Problem(title: MensagemPedidosDemais, statusCode: StatusCodes.Status429TooManyRequests);
            PedidosDeDispositivo.EscreverCookie(resposta, cookie);
            return TypedResults.Ok(new PedidoCriadoDto(codigo));
        })
        .WithName("PedirDispositivo")
        .WithSummary("Pede a entrada de um celular novo; devolve o código de 6 dígitos (no-store) e o cookie do pedido, exista o CPF ou não. "
            + "429 quando o cônjuge já tem pedidos pendentes demais (nada é gravado).")
        .ProducesProblem(StatusCodes.Status429TooManyRequests);

        anonimo.MapGet("/dispositivo/pedido", async Task<Ok<EstadoDoPedidoDto>> (
            PedidosDeDispositivo pedidos, HttpRequest requisicao, HttpResponse resposta, CancellationToken ct) =>
        {
            resposta.Headers.CacheControl = "no-store";
            return TypedResults.Ok(new EstadoDoPedidoDto(await pedidos.ConsultarAsync(requisicao.Cookies[NomesDoLogin.CookieDePedido], ct)));
        })
        .WithName("ConsultarPedidoDeDispositivo")
        .WithSummary("Só o estado do pedido (PENDENTE, APROVADO ou EXPIRADO), sem efeito colateral. Sem cookie válido: PENDENTE.");

        anonimo.MapPost("/dispositivo/pedido/resgate", async Task<Results<NoContent, ProblemHttpResult>> (
            PedidosDeDispositivo pedidos, HttpRequest requisicao, HttpResponse resposta) =>
        {
            resposta.Headers.CacheControl = "no-store";
            if (await pedidos.ResgatarAsync(requisicao.Cookies[NomesDoLogin.CookieDePedido]) is not { } token)
                return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict);
            AutorizacoesDeRegistro.EscreverCookie(resposta, token);
            PedidosDeDispositivo.ApagarCookie(resposta);
            return TypedResults.NoContent();
        })
        .WithName("ResgatarPedidoDeDispositivo")
        .WithSummary("Pedido aprovado e no prazo vira o cookie de autorização de registro (uso único); senão 409.")
        .ProducesProblem(StatusCodes.Status409Conflict);

        anonimo.MapPost("/recuperacao", async Task<Results<Ok<CodigoRecuperacaoDto>, ProblemHttpResult>> (
            RecuperacaoDto pedido, RecuperacaoDeAcesso recuperacao, HttpResponse resposta, CancellationToken ct) =>
        {
            resposta.Headers.CacheControl = "no-store";
            switch (await recuperacao.ExecutarAsync(pedido.Cpf, pedido.Codigo, ct))
            {
                case ResultadoDaRecuperacao.Autorizado autorizado:
                    AutorizacoesDeRegistro.EscreverCookie(resposta, autorizado.Token);
                    return TypedResults.Ok(new CodigoRecuperacaoDto(autorizado.NovoCodigo));
                case ResultadoDaRecuperacao.Bloqueado:
                    return TypedResults.Problem(title: EndpointsDeSessao.MensagemContaBloqueada, statusCode: StatusCodes.Status423Locked);
                default:
                    return TypedResults.Problem(title: EndpointsDeSessao.MensagemFalhaDeLogin, statusCode: StatusCodes.Status401Unauthorized);
            }
        })
        .WithName("RecuperarAcesso")
        .WithSummary("CPF + código de recuperação: gira o código (o novo vem uma única vez, no-store) e emite o cookie de autorização de registro.")
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status423Locked);

        var logado = api.MapGroup("/dispositivos").WithTags("Dispositivos");

        logado.MapGet("/pedidos", async Task<Ok<IReadOnlyList<PedidoPendenteDto>>> (
            PedidosDeDispositivo pedidos, HttpResponse resposta, CancellationToken ct) =>
        {
            resposta.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await pedidos.PendentesDoConjugeAsync(ct));
        })
        .WithName("ListarPedidosDoConjuge")
        .WithSummary("Pedidos de celular novo do outro cônjuge, pendentes e no prazo, com o código para conferir.");

        logado.MapPost("/pedidos/{id:guid}/aprovacao", async Task<Results<NoContent, ProblemHttpResult, NotFound>> (
            Guid id, PedidosDeDispositivo pedidos, CancellationToken ct) =>
            await pedidos.AprovarAsync(id, ct) switch
            {
                ResultadoDaAprovacao.Aprovado => TypedResults.NoContent(),
                ResultadoDaAprovacao.ProprioDono => TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden),
                _ => TypedResults.NotFound(),
            })
        .AddEndpointFilter<ExigirConfirmacaoRecente>()
        .WithName("AprovarPedidoDeDispositivo")
        .WithSummary("Aprova o pedido do outro cônjuge (exige confirmação recente). O próprio dono não aprova (403).")
        .ProducesProblem(StatusCodes.Status403Forbidden);

        logado.MapPost("/conjuge/destravar", async Task<Results<NoContent, NotFound>> (DestravarConjuge destravar, CancellationToken ct) =>
            await destravar.ExecutarAsync(ct) ? TypedResults.NoContent() : TypedResults.NotFound())
        .AddEndpointFilter<ExigirConfirmacaoRecente>()
        .WithName("DestravarConjuge")
        .WithSummary("Destrava o outro cônjuge na hora (exige confirmação recente).")
        .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }
}
