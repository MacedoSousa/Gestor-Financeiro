namespace GestorFinanceiro.Modules.Identidade.Contracts;

/// <summary>Corpo de POST /api/v1/auth/totp/registro. Apelido é o nome do dispositivo (1 a 60 caracteres).</summary>
public sealed record NovoTotpDto(string Apelido);

/// <summary>Resposta do registro de TOTP. A URI carrega o segredo: ToString não mostra nada.</summary>
public sealed record TotpGeradoDto(Guid CredencialId, string OtpauthUri)
{
    public override string ToString() => nameof(TotpGeradoDto);
}

/// <summary>Corpo de POST /api/v1/auth/totp/confirmacao. Traz um código: ToString não mostra nada.</summary>
public sealed record ConfirmarTotpDto(Guid CredencialId, string Codigo)
{
    public override string ToString() => nameof(ConfirmarTotpDto);
}

/// <summary>Corpo de POST /api/v1/auth/totp. Traz CPF e código: ToString não mostra nada.</summary>
public sealed record EntrarComTotpDto(string Cpf, string Codigo)
{
    public override string ToString() => nameof(EntrarComTotpDto);
}
