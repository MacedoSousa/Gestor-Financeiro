namespace GestorFinanceiro.Modules.Integracoes.Contracts;

/// <summary>
/// Resposta de GET /api/v1/integracoes/items/{id}/descoberta: o que o banco tem e o sistema ainda não. Nada é gravado.
/// Nunca traz o itemId da Pluggy, agência, número de conta nem final de cartão.
/// </summary>
/// <param name="Banco">Nome do banco digitado no cadastro do item.</param>
/// <param name="NaoImportadas">Contas marcadas "não importar" (decisão 16): não são perguntadas de novo; podem ser incluídas.</param>
public sealed record DescobertaDto(string Banco, IReadOnlyList<ContaEncontradaDto> Contas, IReadOnlyList<AplicacaoEncontradaDto> Aplicacoes,
    IReadOnlyList<ContaNaoImportadaDto> NaoImportadas);

/// <param name="IdExterno">Id opaco da conta na Pluggy (D7); é o que o nascimento devolve.</param>
/// <param name="Tipo">CORRENTE, POUPANCA ou CARTAO.</param>
/// <param name="NomeNoBanco">Como o banco chama a conta (ex.: "Conta Corrente").</param>
/// <param name="TitularSugeridoUsuarioId">O dono da conexão; a pessoa pode trocar, inclusive por conjunta.</param>
/// <param name="JaVemDe">Primeiro nome do cônjuge cuja conexão já traz esta conta: a tela a mostra e a ignora.</param>
public sealed record ContaEncontradaDto(string IdExterno, string Tipo, string NomeNoBanco, string ApelidoSugerido, Guid TitularSugeridoUsuarioId,
    int? DiaFechamentoSugerido, int? DiaVencimentoSugerido, long? LimiteCentavos, string? JaVemDe);

/// <param name="Tipo">Subtipo do banco quando houver (ex.: CDB), senão o tipo.</param>
public sealed record AplicacaoEncontradaDto(string IdExterno, string Nome, string Tipo, long SaldoCentavos);

/// <param name="VinculoId">Para POST /api/v1/integracoes/vinculos/{id}/inclusao.</param>
public sealed record ContaNaoImportadaDto(Guid VinculoId, string IdExterno, string Tipo, string NomeNoBanco, string ApelidoSugerido);
