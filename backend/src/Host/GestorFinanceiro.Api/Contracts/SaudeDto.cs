namespace GestorFinanceiro.Api.Contracts;

/// <summary>Resposta de GET /api/v1/saude.</summary>
public sealed record SaudeDto(string Status, DateTimeOffset Horario);
