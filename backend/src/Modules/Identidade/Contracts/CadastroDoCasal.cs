namespace GestorFinanceiro.Modules.Identidade.Contracts;

/// <summary>Corpo de POST /api/v1/casal. Traz segredos: ToString não mostra nada.</summary>
public sealed record CadastroDoCasalDto(string CodigoInstalacao, string NomeFamilia, ConjugeNovoDto Conjuge1, ConjugeNovoDto Conjuge2)
{
    public override string ToString() => nameof(CadastroDoCasalDto);
}

/// <summary>Um dos cônjuges no cadastro. A senha só embrulha a DEK; não é senha de login.</summary>
public sealed record ConjugeNovoDto(string NomeCompleto, string Cpf, string Senha)
{
    public override string ToString() => nameof(ConjugeNovoDto);
}

/// <summary>Resposta do cadastro. O código de recuperação é mostrado uma única vez.</summary>
public sealed record CasalCadastradoDto(Guid CasalId, string CodigoRecuperacao)
{
    public override string ToString() => nameof(CasalCadastradoDto);
}

/// <summary>Resposta de GET /api/v1/casal/cadastro: se o cadastro ainda está aberto.</summary>
public sealed record EstadoDoCadastroDto(bool Disponivel);
