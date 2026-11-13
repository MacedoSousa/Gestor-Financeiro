using System.Diagnostics;
using GestorFinanceiro.Modules.Integracoes.Aplicacao;
using GestorFinanceiro.Modules.Integracoes.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace GestorFinanceiro.Modules.Integracoes.Api;

/// <summary>
/// Conexões do Meu Pluggy. Exigem sessão (política padrão do grupo /api/v1); o casal e o titular vêm da sessão. Nenhuma
/// resposta traz o itemId da Pluggy nem as chaves.
/// </summary>
internal static class EndpointsDeConexoes
{
    public static IEndpointRouteBuilder MapEndpointsDeConexoes(this IEndpointRouteBuilder api)
    {
        var grupo = api.MapGroup("/integracoes/conexoes").WithTags("Integrações");
        grupo.AddEndpointFilter(async (contexto, proximo) =>
        {
            contexto.HttpContext.Response.Headers.CacheControl = "no-store";
            return await proximo(contexto);
        });

        grupo.MapGet("/", async Task<Results<Ok<ConexoesDto>, NotFound>> (ServicoDeConexoes servico, CancellationToken ct) =>
            await servico.ListarAsync(ct) switch
            {
                ResultadoDeConexoes.Lista l => TypedResults.Ok(l.Dto),
                ResultadoDeConexoes.NaoEncontrado => TypedResults.NotFound(),
                var outro => throw new UnreachableException(outro.GetType().Name),
            })
        .WithName("ListarConexoes")
        .WithSummary("Conexões dos dois cônjuges, com o estado de cada banco, e os dois slots de chaves do servidor.");

        api.MapGet("/integracoes/grupos-de-investimento", async (ServicoDeConexoes servico, HttpContext http, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(await servico.GruposDeInvestimentoAsync(ct));
            })
        .WithTags("Integrações")
        .WithName("ListarGruposDeInvestimento")
        .WithSummary("Grupos de investimento do casal com o valor no banco e a diferença para o sistema na última sincronização (D21). " +
                     "Nada é lançado: o rendimento entra no sistema na próxima etapa.");

        grupo.MapPost("/", async Task<Results<Created<ConexaoDto>, ValidationProblem, ProblemHttpResult, NotFound>> (
            NovaConexaoDto pedido, ServicoDeConexoes servico, CancellationToken ct) =>
            await servico.CriarAsync(pedido, ct) switch
            {
                ResultadoDeConexoes.Criada c => TypedResults.Created($"/api/v1/integracoes/conexoes/{c.Conexao.Id}", c.Conexao),
                var outro => Erro<Created<ConexaoDto>>(outro),
            })
        .WithName("CriarConexao")
        .WithSummary("Cria a conexão de quem está na sessão com o primeiro banco. Autentica com as chaves do slot escolhido e " +
                     "consulta o item na Pluggy antes de gravar: recusado dá 400, Pluggy fora do ar dá 503. Slot sem chaves " +
                     "dá 400; slot em uso, segunda conexão do mesmo cônjuge ou item já cadastrado dão 409.")
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        grupo.MapPost("/{id:guid}/items", async Task<Results<Created<ItemDto>, ValidationProblem, ProblemHttpResult, NotFound>> (
            Guid id, NovoItemDto pedido, ServicoDeConexoes servico, CancellationToken ct) =>
            await servico.AcrescentarItemAsync(id, pedido, ct) switch
            {
                ResultadoDeConexoes.ItemCriado i => TypedResults.Created($"/api/v1/integracoes/conexoes/{i.ConexaoId}/items/{i.Item.Id}", i.Item),
                var outro => Erro<Created<ItemDto>>(outro),
            })
        .WithName("AcrescentarItem")
        .WithSummary("Mais um banco na própria conexão, validado com as chaves do slot dela. Só o titular (senão 403).")
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        grupo.MapDelete("/{id:guid}/items/{item:guid}", async Task<Results<NoContent, ValidationProblem, ProblemHttpResult, NotFound>> (
            Guid id, Guid item, ServicoDeConexoes servico, CancellationToken ct) =>
            Feito(await servico.ApagarItemAsync(id, item, ct)))
        .WithName("ApagarItem")
        .WithSummary("Tira um banco da própria conexão (soft delete). Só o titular (senão 403). Com conta não arquivada vinda " +
                     "deste banco dá 409: arquive antes.")
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status409Conflict);

        grupo.MapDelete("/{id:guid}", async Task<Results<NoContent, ValidationProblem, ProblemHttpResult, NotFound>> (
            Guid id, ServicoDeConexoes servico, CancellationToken ct) =>
            Feito(await servico.ApagarAsync(id, ct)))
        .WithName("ApagarConexao")
        .WithSummary("Apaga a própria conexão e os bancos dela (soft delete); libera o slot. Só o titular (senão 403). Com " +
                     "conta não arquivada vinda destes bancos dá 409: arquive antes.")
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status409Conflict);

        var items = api.MapGroup("/integracoes/items").WithTags("Integrações");
        items.AddEndpointFilter(async (contexto, proximo) =>
        {
            contexto.HttpContext.Response.Headers.CacheControl = "no-store";
            return await proximo(contexto);
        });
        items.MapPut("/{id:guid}/banco", async Task<Results<NoContent, ValidationProblem, ProblemHttpResult, NotFound>> (
            Guid id, BancoDoItemDto pedido, ServicoDeConexoes servico, CancellationToken ct) =>
            Feito(await servico.EscolherBancoAsync(id, pedido, ct)))
        .WithName("EscolherBancoDoItem")
        .WithSummary("Escolhe o banco (código COMPE) de um item cadastrado antes do código (D18). Só o titular (senão 403); " +
                     "uma vez só (já escolhido dá 409). Nada muda na Pluggy.")
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    private static Results<NoContent, ValidationProblem, ProblemHttpResult, NotFound> Feito(ResultadoDeConexoes resultado) =>
        resultado switch
        {
            ResultadoDeConexoes.Feito => TypedResults.NoContent(),
            ResultadoDeConexoes.Invalido i => TypedResults.ValidationProblem(i.Erros.ToDictionary()),
            ResultadoDeConexoes.Recusado r => TypedResults.Problem(title: r.Titulo, statusCode: r.Status),
            ResultadoDeConexoes.NaoEncontrado => TypedResults.NotFound(),
            _ => throw new UnreachableException(resultado.GetType().Name),
        };

    private static Results<T, ValidationProblem, ProblemHttpResult, NotFound> Erro<T>(ResultadoDeConexoes resultado) where T : IResult =>
        resultado switch
        {
            ResultadoDeConexoes.Invalido i => TypedResults.ValidationProblem(i.Erros.ToDictionary()),
            ResultadoDeConexoes.Recusado r => TypedResults.Problem(title: r.Titulo, statusCode: r.Status),
            ResultadoDeConexoes.NaoEncontrado => TypedResults.NotFound(),
            _ => throw new UnreachableException(resultado.GetType().Name),
        };
}
