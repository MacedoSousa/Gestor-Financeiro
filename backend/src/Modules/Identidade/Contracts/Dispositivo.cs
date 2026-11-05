using System.Text.Json.Serialization;

namespace GestorFinanceiro.Modules.Identidade.Contracts;

/// <summary>Corpo de POST /api/v1/auth/dispositivo/pedido. Traz CPF: ToString não mostra nada.</summary>
public sealed record NovoPedidoDeDispositivoDto(string Cpf)
{
    public override string ToString() => nameof(NovoPedidoDeDispositivoDto);
}

/// <summary>Resposta do pedido: o código de 6 dígitos que o cônjuge confere antes de aprovar.</summary>
public sealed record PedidoCriadoDto(string Codigo)
{
    public override string ToString() => nameof(PedidoCriadoDto);
}

/// <summary>Resposta de GET /api/v1/auth/dispositivo/pedido: só o estado (PENDENTE, APROVADO ou EXPIRADO).</summary>
public sealed record EstadoDoPedidoDto(string Estado);

/// <summary>Pedido de celular novo do outro cônjuge, aguardando aprovação.</summary>
public sealed record PedidoPendenteDto(Guid Id, string Codigo, DateTimeOffset ExpiraEm)
{
    public override string ToString() => nameof(PedidoPendenteDto);
}

/// <summary>Corpo de POST /api/v1/auth/recuperacao. Traz CPF e código: ToString não mostra nada.</summary>
public sealed record RecuperacaoDto(string Cpf, string Codigo)
{
    public override string ToString() => nameof(RecuperacaoDto);
}

/// <summary>Código de recuperação novo, mostrado uma única vez (no-store).</summary>
public sealed record CodigoRecuperacaoDto(string CodigoRecuperacao)
{
    public override string ToString() => nameof(CodigoRecuperacaoDto);
}

/// <summary>
/// Corpo de POST /api/v1/auth/confirmar: ou o código TOTP, ou a asserção da passkey
/// (AuthenticationResponseJSON) do próprio usuário da sessão. ToString não mostra nada.
/// </summary>
public sealed record ConfirmacaoDeIdentidadeDto(
    [property: JsonPropertyName("codigoTotp")] string? CodigoTotp,
    [property: JsonPropertyName("passkey")] RespostaDeLoginComPasskeyDto? Passkey)
{
    public override string ToString() => nameof(ConfirmacaoDeIdentidadeDto);
}

/// <summary>Resposta de POST /api/v1/auth/confirmar/opcoes (PublicKeyCredentialRequestOptionsJSON) com as passkeys do usuário.</summary>
public sealed record OpcoesDeConfirmacaoDto(
    [property: JsonPropertyName("challenge")] string Challenge,
    [property: JsonPropertyName("timeout")] long Timeout,
    [property: JsonPropertyName("rpId")] string RpId,
    [property: JsonPropertyName("allowCredentials")] IReadOnlyList<DescritorDeCredencialDto> AllowCredentials,
    [property: JsonPropertyName("userVerification")] string UserVerification)
{
    public override string ToString() => nameof(OpcoesDeConfirmacaoDto);
}

/// <summary>Credencial ativa e confirmada do usuário. Tipo: PASSKEY ou TOTP.</summary>
public sealed record CredencialDto(Guid Id, string Tipo, string Apelido, DateTimeOffset CriadaEm, DateTimeOffset? UltimoUsoEm);
