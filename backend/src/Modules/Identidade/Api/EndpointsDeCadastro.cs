using System.Diagnostics;
using GestorFinanceiro.Modules.Identidade.Aplicacao;
using GestorFinanceiro.Modules.Identidade.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace GestorFinanceiro.Modules.Identidade.Api;

/// <summary>Cadastro do casal (sem sessão; protegido pelo código de instalação). Só funciona uma vez.</summary>
internal static class EndpointsDeCadastro
{
    internal const string TituloCodigoIncorreto = "Código de instalação incorreto ou expirado.";

    public static RouteGroupBuilder MapCadastro(this RouteGroupBuilder grupo)
    {
        grupo.MapGet("/cadastro", async (CadastroDoCasal cadastro, CancellationToken ct) =>
            TypedResults.Ok(new EstadoDoCadastroDto(await cadastro.EstaDisponivelAsync(ct))))
        .AllowAnonymous()
        .WithName("ObterEstadoDoCadastro")
        .WithSummary("Informa se o cadastro do casal ainda está aberto nesta instalação.");

        grupo.MapPost("/", async Task<Results<Created<CasalCadastradoDto>, ValidationProblem, ProblemHttpResult, NotFound>> (
            CadastroDoCasalDto pedido, CadastroDoCasal cadastro, HttpContext http, CancellationToken ct) =>
        {
            switch (await cadastro.CadastrarAsync(pedido, ct))
            {
                case ResultadoDoCadastro.Criado criado:
                    http.Response.Headers.CacheControl = "no-store";
                    return TypedResults.Created("/api/v1/casal", new CasalCadastradoDto(criado.CasalId, criado.CodigoRecuperacao));
                case ResultadoDoCadastro.Invalido invalido:
                    return TypedResults.ValidationProblem(invalido.Erros.ToDictionary());
                case ResultadoDoCadastro.CodigoInstalacaoIncorreto:
                    return TypedResults.Problem(title: TituloCodigoIncorreto, statusCode: StatusCodes.Status403Forbidden);
                case ResultadoDoCadastro.JaCadastrado:
                    return TypedResults.NotFound();
                case var outro:
                    throw new UnreachableException(outro.GetType().Name);
            }
        })
        .AllowAnonymous()
        .WithName("CadastrarCasal")
        .WithSummary("Cadastra o casal, os dois cônjuges e os envelopes da DEK. Devolve o código de recuperação uma única vez.")
        .ProducesProblem(StatusCodes.Status403Forbidden);

        return grupo;
    }
}
