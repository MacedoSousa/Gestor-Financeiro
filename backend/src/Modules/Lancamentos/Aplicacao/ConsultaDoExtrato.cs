using System.Globalization;
using System.Text;
using GestorFinanceiro.Modules.Contas.Contracts;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Dominio;
using GestorFinanceiro.Modules.Lancamentos.Infra;
using GestorFinanceiro.SharedKernel.Aplicacao;
using GestorFinanceiro.SharedKernel.Seguranca;
using Microsoft.EntityFrameworkCore;

namespace GestorFinanceiro.Modules.Lancamentos.Aplicacao;

/// <summary>Filtros de GET /api/v1/lancamentos, como chegam na query string.</summary>
internal sealed record FiltroDoExtrato(string? Mes, Guid? ContaId, string? Titular, string? Origem, string? Q);

/// <summary>
/// Extrato do mês. A busca por texto decifra só os lançamentos do mês filtrado, no servidor, e compara sem acento e sem
/// caixa; o texto da busca nunca vai para o SQL nem para o log.
/// </summary>
internal sealed class ConsultaDoExtrato(LancamentosDbContext db, IUsuarioAtual atual, IConsultaDeContas contas,
    IConsultaDeUsuarios usuarios, ICofre cofre, TimeProvider relogio)
{
    internal const string Conjunta = "Conjunta";
    private const int TamanhoMaximoDaBusca = 140;

    /// <summary>Extrato, ou os erros de validação dos filtros. Nulo nos dois quando não há casal na sessão.</summary>
    public async Task<(ExtratoDto? Extrato, Dictionary<string, string[]>? Erros)> ObterAsync(FiltroDoExtrato filtro, CancellationToken ct)
    {
        if (atual.CasalId is not { } casalId) return (null, null);

        var erros = new Dictionary<string, string[]>();
        var hoje = Brasilia.Hoje(relogio);
        var inicio = new DateOnly(hoje.Year, hoje.Month, 1);
        if (filtro.Mes is not null
            && !DateOnly.TryParseExact(filtro.Mes + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out inicio))
            erros["mes"] = ["Mês inválido: use AAAA-MM."];
        if (filtro.Titular is not (null or "conjunta" or "eu" or "conjuge"))
            erros["titular"] = ["Titular inválido: use conjunta, eu ou conjuge."];
        OrigemDaTransacao? origem = filtro.Origem switch
        {
            null => null,
            "MANUAL" => OrigemDaTransacao.Manual,
            "OPEN_FINANCE" => OrigemDaTransacao.OpenFinance,
            "WHATSAPP" => OrigemDaTransacao.Whatsapp,
            _ => null,
        };
        if (filtro.Origem is not null && origem is null)
            erros["origem"] = ["Origem inválida: use MANUAL, OPEN_FINANCE ou WHATSAPP."];
        if (filtro.Q is { Length: > TamanhoMaximoDaBusca })
            erros["q"] = [$"A busca deve ter até {TamanhoMaximoDaBusca} caracteres."];
        if (erros.Count > 0) return (null, erros);

        var listaDeContas = await contas.ListarAsync(ct);
        var fim = inicio.AddMonths(1);
        var consulta = db.Transacoes.AsNoTracking().Where(t => t.Data >= inicio && t.Data < fim);
        if (filtro.ContaId is { } contaId) consulta = consulta.Where(t => t.ContaId == contaId);
        if (origem is { } o) consulta = consulta.Where(t => t.Origem == o);
        if (filtro.Titular is { } titular)
        {
            var eu = atual.UsuarioId;
            var ids = listaDeContas
                .Where(c => titular switch
                {
                    "conjunta" => c.TitularUsuarioId is null,
                    "eu" => c.TitularUsuarioId is not null && c.TitularUsuarioId == eu,
                    _ => c.TitularUsuarioId is not null && c.TitularUsuarioId != eu,
                })
                .Select(c => c.Id)
                .ToArray();
            consulta = consulta.Where(t => ids.Contains(t.ContaId));
        }

        var transacoes = await consulta.OrderByDescending(t => t.Data).ThenByDescending(t => t.Id).ToListAsync(ct);
        var itens = await MontarAsync(transacoes, casalId, listaDeContas, ct);

        if (!string.IsNullOrWhiteSpace(filtro.Q))
        {
            var busca = Normalizar(filtro.Q.Trim());
            itens = itens
                .Where(l => Normalizar(l.Descricao).Contains(busca, StringComparison.Ordinal)
                            || (l.Contraparte is { } cp && Normalizar(cp).Contains(busca, StringComparison.Ordinal)))
                .ToList();
        }

        return (new ExtratoDto(itens.Count, itens), null);
    }

    /// <summary>Um lançamento do casal pelo id; nulo se não existe, foi apagado ou é de outro casal.</summary>
    public async Task<LancamentoDto?> ObterPorIdAsync(Guid id, CancellationToken ct)
    {
        if (atual.CasalId is not { } casalId) return null;
        var t = await db.Transacoes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return t is null ? null : (await MontarAsync([t], casalId, null, ct)).Single();
    }

    /// <summary>Decifra e junta conta, categoria e "quem". Categorias vêm com os filtros ligados (regra 9).</summary>
    internal async Task<List<LancamentoDto>> MontarAsync(IReadOnlyList<Transacao> transacoes, Guid casalId,
        IReadOnlyList<ContaResumo>? listaDeContas, CancellationToken ct)
    {
        if (transacoes.Count == 0) return [];
        var contasPorId = (listaDeContas ?? await contas.ListarAsync(ct)).ToDictionary(c => c.Id);
        var idsDeCategoria = transacoes.Where(t => t.CategoriaId is not null).Select(t => t.CategoriaId!.Value).Distinct().ToArray();
        var categorias = idsDeCategoria.Length == 0
            ? []
            : await db.Categorias.AsNoTracking().Where(c => idsDeCategoria.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var apelidos = (await usuarios.ListarDoCasalAsync(casalId, ct)).ToDictionary(u => u.UsuarioId, u => u.Apelido);

        return transacoes.Select(t =>
        {
            contasPorId.TryGetValue(t.ContaId, out var conta);
            var categoria = t.CategoriaId is { } cid && categorias.TryGetValue(cid, out var k)
                ? new CategoriaDoLancamentoDto(k.Id, k.Nome, k.Arquivada)
                : null;
            var quem = conta?.TitularUsuarioId is { } titular && apelidos.TryGetValue(titular, out var apelido) ? apelido : Conjunta;
            return new LancamentoDto(
                t.Id, t.Data, t.LerDescricao(cofre), t.LerContraparte(cofre), categoria,
                new ContaDoLancamentoDto(t.ContaId, conta?.Apelido ?? "Conta removida", conta?.Tipo ?? string.Empty),
                quem, TextoDaOrigem(t.Origem), t.Natureza.ToString().ToUpperInvariant(), t.ValorCentavos,
                t.Parcela, t.Parcelas, t.GrupoId, t.FaturaVencimento);
        }).ToList();
    }

    private static string TextoDaOrigem(OrigemDaTransacao origem) =>
        origem == OrigemDaTransacao.OpenFinance ? "OPEN_FINANCE" : origem.ToString().ToUpperInvariant();

    /// <summary>Sem acento (decompõe e tira as marcas) e em minúsculas, de forma independente de cultura.</summary>
    internal static string Normalizar(string texto)
    {
        var decomposto = texto.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposto.Length);
        foreach (var c in decomposto)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC).ToLower(CultureInfo.InvariantCulture);
    }
}
