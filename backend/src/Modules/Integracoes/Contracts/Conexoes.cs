using System.ComponentModel.DataAnnotations;

namespace GestorFinanceiro.Modules.Integracoes.Contracts;

/// <summary>Resposta de GET /api/v1/integracoes/conexoes. Nunca traz o itemId da Pluggy nem as chaves.</summary>
/// <param name="Slots">Os dois slots de chaves do servidor: se os arquivos estão preenchidos e se uma conexão já os usa.</param>
public sealed record ConexoesDto(IReadOnlyList<ConexaoDto> Conexoes, IReadOnlyList<SlotDto> Slots);

/// <param name="Configurado">Os arquivos <c>pluggy_&lt;slot&gt;_client_id</c> e <c>_client_secret</c> não estão vazios.</param>
public sealed record SlotDto(int Slot, bool Configurado, bool EmUso);

/// <param name="Apelido">Primeiro nome do titular.</param>
/// <param name="EhSua">A conexão é de quem está na sessão: só o titular cadastra, troca e apaga (D21).</param>
public sealed record ConexaoDto(Guid Id, Guid UsuarioId, string Apelido, int Slot, bool SlotConfigurado, bool EhSua,
    IReadOnlyList<ItemDto> Items);

/// <param name="Id">Id do item no sistema (não é o itemId da Pluggy).</param>
/// <param name="Estado">ATIVO, CONSENTIMENTO_VENCENDO, CONSENTIMENTO_VENCIDO ou ERRO.</param>
/// <param name="Motivo">Só no ERRO: o código do status da Pluggy (ex.: LOGIN_ERROR).</param>
/// <param name="ConsentimentoVenceEm">Dia, em São Paulo, em que o consentimento vence.</param>
public sealed record ItemDto(Guid Id, string Banco, string Estado, string? Motivo, DateOnly? ConsentimentoVenceEm,
    DateTimeOffset? UltimaSincronizacaoEm);

/// <summary>Corpo de POST /api/v1/integracoes/conexoes: a conexão de quem está na sessão, já com o primeiro banco.</summary>
/// <param name="Slot">1 ou 2: em quais arquivos (<c>pluggy_1_*</c> ou <c>pluggy_2_*</c>) estão as chaves deste cônjuge.</param>
/// <param name="ItemId">Id do item copiado do painel do Meu Pluggy; até 100 caracteres. Validado no banco antes de gravar.</param>
/// <param name="Banco">Nome do banco, para exibição; 1 a 60 caracteres.</param>
public sealed record NovaConexaoDto([property: Required] int? Slot, [property: Required] string? ItemId, [property: Required] string? Banco);

/// <summary>Corpo de POST /api/v1/integracoes/conexoes/{id}/items: mais um banco na própria conexão.</summary>
public sealed record NovoItemDto([property: Required] string? ItemId, [property: Required] string? Banco);
