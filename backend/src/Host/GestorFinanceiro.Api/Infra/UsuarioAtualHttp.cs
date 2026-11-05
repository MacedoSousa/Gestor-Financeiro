using System.Security.Claims;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.SharedKernel.Aplicacao;

namespace GestorFinanceiro.Api.Infra;

/// <summary>Lê o usuário da sessão (cookie). Sem sessão, todos os campos ficam nulos.</summary>
internal sealed class UsuarioAtualHttp(IHttpContextAccessor acessor) : IUsuarioAtual
{
    private ClaimsPrincipal? Principal => acessor.HttpContext?.User;

    public Guid? UsuarioId => Ler(ClaimTypes.NameIdentifier);

    public Guid? CasalId => Ler(NomesDoLogin.ClaimCasal);

    public string? Ip => acessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    private Guid? Ler(string tipo) =>
        Guid.TryParse(Principal?.FindFirstValue(tipo), out var id) ? id : null;
}
