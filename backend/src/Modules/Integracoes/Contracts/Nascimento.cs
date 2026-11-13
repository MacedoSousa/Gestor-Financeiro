using System.ComponentModel.DataAnnotations;

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

/// <summary>Corpo de POST /api/v1/integracoes/items/{id}/nascimento: o que a pessoa confirmou. Só o titular (D21).</summary>
/// <param name="Contas">Uma por conta ou cartão encontrado; quem fica de fora continua sendo perguntado.</param>
/// <param name="Aplicacoes">Uma por aplicação; cada grupo distinto (pelo nome, sem caixa) vira uma conta INVESTIMENTO.</param>
public sealed record NascimentoDto(IReadOnlyList<ContaConfirmadaDto>? Contas, IReadOnlyList<AplicacaoConfirmadaDto>? Aplicacoes);

/// <param name="Acao">CRIAR (conta nova ligada), LIGAR (a uma conta que já existe) ou NAO_IMPORTAR (decisão 16).</param>
/// <param name="ContaExistenteId">Só no LIGAR: corrente ou poupança para conta, cartão para cartão.</param>
/// <param name="Apelido">Só no CRIAR; 1 a 60 caracteres. Repetido no casal dá 409 com <c>sugestao</c>.</param>
/// <param name="TitularUsuarioId">Só no CRIAR; nulo = conjunta.</param>
/// <param name="DiaFechamento">Só no CRIAR de cartão (1 a 31), junto com <paramref name="DiaVencimento"/>.</param>
public sealed record ContaConfirmadaDto([property: Required] string? IdExterno, [property: Required] string? Acao, Guid? ContaExistenteId,
    string? Apelido, Guid? TitularUsuarioId, int? DiaFechamento, int? DiaVencimento);

/// <param name="Grupo">Nulo = a aplicação fica aguardando grupo (não vira conta).</param>
public sealed record AplicacaoConfirmadaDto([property: Required] string? IdExterno, GrupoDeInvestimentoDto? Grupo);

/// <param name="Nome">Vira o apelido da conta do grupo; 1 a 60 caracteres.</param>
/// <param name="MetaId">Opcional: a meta que a conta do grupo passa a compor.</param>
public sealed record GrupoDeInvestimentoDto([property: Required] string? Nome, Guid? MetaId);

/// <summary>Corpo de POST /api/v1/integracoes/vinculos/{id}/inclusao: incluir uma conta marcada "não importar".</summary>
/// <param name="Acao">CRIAR ou LIGAR, com os mesmos campos da confirmação.</param>
public sealed record InclusaoDto([property: Required] string? Acao, Guid? ContaExistenteId, string? Apelido, Guid? TitularUsuarioId,
    int? DiaFechamento, int? DiaVencimento);
