using GestorFinanceiro.Modules.Investimentos.Contracts;

namespace GestorFinanceiro.Modules.Investimentos.Aplicacao;

/// <summary>Entrega a outros módulos o que o GET /metas monta, sem repetir regra: delega ao <see cref="ServicoDeMetas"/>.</summary>
internal sealed class ConsultaDeMetas(ServicoDeMetas servico) : IConsultaDeMetas
{
    public async Task<PainelDeMetasDto> PainelAsync(CancellationToken ct = default) =>
        await servico.PainelAsync(ct) is ResultadoDeMetas.Painel p
            ? p.Dto
            : throw new InvalidOperationException("Metas indisponíveis para a sessão atual.");
}
