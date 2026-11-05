namespace GestorFinanceiro.Api.Infra;

/// <summary>
/// Defesa de CSRF junto com SameSite=Strict: todo método que muda estado precisa do cabeçalho
/// X-Requested-With: gestor-web, que um formulário de outro site não consegue enviar.
/// </summary>
internal sealed class ExigirCabecalhoAntiCsrf(RequestDelegate proximo)
{
    internal const string Cabecalho = "X-Requested-With";
    internal const string Valor = "gestor-web";

    public Task InvokeAsync(HttpContext http)
    {
        var metodo = http.Request.Method;
        if (HttpMethods.IsGet(metodo) || HttpMethods.IsHead(metodo)) return proximo(http);
        if (http.Request.Headers[Cabecalho] == Valor) return proximo(http);

        // Corpo vazio: o UseStatusCodePages monta o Problem Details.
        http.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
