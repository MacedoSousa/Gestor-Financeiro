using GestorFinanceiro.Modules.Orcamento.Contracts;

namespace GestorFinanceiro.Modules.Orcamento.Aplicacao;

/// <summary>Entrega a outros módulos o que o GET /orcamento monta, sem repetir regra: delega ao <see cref="ServicoDeOrcamento"/>.</summary>
internal sealed class ConsultaDoOrcamento(ServicoDeOrcamento servico) : IConsultaDoOrcamento
{
    public async Task<OrcamentoDoMesDto> DoMesAsync(DateOnly mes, CancellationToken ct = default) =>
        await servico.ObterAsync(mes.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture), ct) is ResultadoDoOrcamento.DoMes d
            ? d.Orcamento
            : throw new InvalidOperationException("Orçamento indisponível para a sessão atual.");
}
