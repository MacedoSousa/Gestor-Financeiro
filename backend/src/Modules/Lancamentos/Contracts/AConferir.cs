namespace GestorFinanceiro.Modules.Lancamentos.Contracts;

/// <summary>Um item aberto da caixa "A conferir" (Fase 2, D11, Tarefa 6.1): o que o sistema não decidiu sozinho.</summary>
/// <param name="Tipo">TRANSFERENCIA_SEM_PAR, PENDENTE_SUMIU, PENDENTE_SEM_PAR ou APORTE_OU_RESGATE.</param>
/// <param name="ValorCentavos">Com o sinal do sistema (negativo = saída).</param>
/// <param name="Descricao">A descrição do lançamento do banco; nula na pendente que sumiu.</param>
/// <param name="Opcoes">As resoluções aceitas para este item (TRANSFERENCIA, GASTO, RECEITA, APORTE, RESGATE, CIENTE, DEIXAR_PARA_DEPOIS).</param>
public sealed record ItemAConferirDto(Guid Id, string Tipo, Guid ContaId, string ContaApelido, DateOnly Data, long ValorCentavos, string? Descricao,
    IReadOnlyList<string> Opcoes);

/// <summary>Corpo de POST /api/v1/a-conferir/{id}/resolucao.</summary>
/// <param name="Opcao">Uma das opções do item.</param>
/// <param name="ContaId">TRANSFERENCIA, APORTE e RESGATE: a outra conta do par.</param>
/// <param name="CategoriaId">GASTO e RECEITA: a categoria.</param>
public sealed record ResolucaoDto(string? Opcao, Guid? ContaId, Guid? CategoriaId);
