using GestorFinanceiro.Modules.Contas.Contracts;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.Modules.Integracoes.Contracts;
using GestorFinanceiro.Modules.Integracoes.Dominio;
using GestorFinanceiro.Modules.Integracoes.Infra;
using GestorFinanceiro.Modules.Integracoes.Pluggy;
using GestorFinanceiro.SharedKernel.Aplicacao;
using GestorFinanceiro.SharedKernel.Seguranca;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GestorFinanceiro.Modules.Integracoes.Aplicacao;

internal abstract record ResultadoDeNascimento
{
    public sealed record Descoberta(DescobertaDto Dto) : ResultadoDeNascimento;
    public sealed record Feito : ResultadoDeNascimento;
    public sealed record Invalido(IReadOnlyDictionary<string, string[]> Erros) : ResultadoDeNascimento;

    /// <param name="Extras">Campos a mais no Problem Details (ex.: a sugestão de apelido).</param>
    public sealed record Recusado(string Titulo, int Status, IReadOnlyDictionary<string, object?>? Extras = null) : ResultadoDeNascimento;

    public sealed record NaoEncontrado : ResultadoDeNascimento;
}

/// <summary>
/// O item, a conexão dona, as chaves do slot, o itemId decifrado (só em memória, nunca em log ou resposta) e o código do
/// banco já escolhido (D18).
/// </summary>
internal sealed record AcessoAoItem(Item Item, Conexao Conexao, CredenciaisPluggy Chaves, string ItemPluggy, string CodigoBanco);

/// <summary>O que o banco entregou para o item, numa leitura só.</summary>
internal sealed record LeituraDoBanco(IReadOnlyList<ContaPluggy> Contas, IReadOnlyList<InvestimentoPluggy> Aplicacoes,
    IReadOnlyDictionary<string, IReadOnlyList<FaturaPluggy>> FaturasPorCartao);

/// <summary>
/// Lista o que o banco tem e o sistema ainda não (Tarefa 3.2). Só lê: nada é gravado sem confirmação (spec, seção 1).
/// Só o titular da conexão (D21). Conta "nascida" = vínculo cuja conta existe no Contas; o resto é perguntado de novo.
/// </summary>
internal sealed partial class Descoberta(
    IntegracoesDbContext db,
    IUsuarioAtual atual,
    IConsultaDeUsuarios usuarios,
    IConsultaDeContas contas,
    SegredosDaPluggy segredos,
    IClientePluggy pluggy,
    ICofre cofre,
    ILogger<Descoberta> log)
{
    internal const string TituloBancoRecusou = "O banco não entregou as contas deste item agora. Tente de novo mais tarde.";
    internal const string TituloEscolhaOBanco = "Escolha o banco desta conexão antes de ver as contas.";

    public async Task<ResultadoDeNascimento> ListarAsync(Guid itemId, CancellationToken ct)
    {
        var (acesso, recusa) = await AcessoAsync(itemId, ct);
        if (recusa is not null) return recusa;
        var (leitura, falha) = await LerAsync(acesso!, ct);
        if (falha is not null) return falha;
        return new ResultadoDeNascimento.Descoberta(await MontarAsync(acesso!, leitura!, ct));
    }

    /// <summary>
    /// O item do casal, se a conexão dele for de quem está na sessão (senão 404 ou 403), se o banco já foi escolhido (senão
    /// 409: a identidade das contas depende do código, D18) e se o slot tiver chaves.
    /// </summary>
    internal async Task<(AcessoAoItem? Acesso, ResultadoDeNascimento? Recusa)> AcessoAsync(Guid itemId, CancellationToken ct)
    {
        if (atual.CasalId is null) return (null, new ResultadoDeNascimento.NaoEncontrado());
        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null) return (null, new ResultadoDeNascimento.NaoEncontrado());
        var conexao = await db.Conexoes.AsNoTracking().FirstOrDefaultAsync(c => c.Id == item.ConexaoId, ct);
        if (conexao is null) return (null, new ResultadoDeNascimento.NaoEncontrado());
        if (conexao.UsuarioId != atual.UsuarioId) return (null, new ResultadoDeNascimento.Recusado(ServicoDeConexoes.TituloSoOTitular, 403));
        if (item.CodigoBanco is not { } codigoBanco) return (null, new ResultadoDeNascimento.Recusado(TituloEscolhaOBanco, 409));
        if (segredos.Ler(conexao.Slot) is not { } chaves)
            return (null, new ResultadoDeNascimento.Recusado(ServicoDeConexoes.TituloSlotSemChaves, 400));
        return (new AcessoAoItem(item, conexao, chaves, item.LerItemId(cofre), codigoBanco), null);
    }

    /// <summary>
    /// Contas, aplicações e faturas dos cartões. Resposta de erro da Pluggy = 502; rede ou tempo esgotado = 503. O log leva
    /// só o slot, o status e o código (D26).
    /// </summary>
    internal async Task<(LeituraDoBanco? Leitura, ResultadoDeNascimento? Falha)> LerAsync(AcessoAoItem a, CancellationToken ct)
    {
        try
        {
            var contasDoBanco = await pluggy.ListarContasAsync(a.Chaves, a.ItemPluggy, ct);
            var aplicacoes = await pluggy.ListarInvestimentosAsync(a.Chaves, a.ItemPluggy, ct);
            var faturas = new Dictionary<string, IReadOnlyList<FaturaPluggy>>(StringComparer.Ordinal);
            foreach (var cartao in contasDoBanco.Where(c => Vinculo.TipoDe(c) == TipoDeVinculo.Cartao))
                faturas[cartao.Id] = await pluggy.ListarFaturasAsync(a.Chaves, cartao.Id, ct);
            return (new LeituraDoBanco(contasDoBanco, aplicacoes, faturas), null);
        }
        catch (ErroDaPluggy e)
        {
            BancoRecusou(log, a.Chaves.Slot, e.Status, e.Codigo ?? "-");
            return (null, new ResultadoDeNascimento.Recusado(TituloBancoRecusou, 502));
        }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            PluggyFora(log, a.Chaves.Slot, e.GetType().Name);
            return (null, new ResultadoDeNascimento.Recusado(ServicoDeConexoes.TituloPluggyFora, 503));
        }
    }

    private async Task<DescobertaDto> MontarAsync(AcessoAoItem a, LeituraDoBanco leitura, CancellationToken ct)
    {
        var vinculos = await db.Vinculos.AsNoTracking().Where(v => v.ItemId == a.Item.Id).ToListAsync(ct);
        // "Nascida" = a conta existe no casal e já é ligada; um LIGAR pela metade (conta ainda manual) volta a ser perguntado.
        var existentes = (await contas.ListarAsync(ct)).Where(c => c.Ligada).Select(c => c.Id).ToHashSet();
        var apelidos = (await usuarios.ListarDoCasalAsync(a.Item.CasalId, ct)).ToDictionary(u => u.UsuarioId, u => u.Apelido);
        var deOutrosItems = await IdentidadesDeOutrosItemsAsync(a.Item.Id, ct);
        var dono = apelidos.GetValueOrDefault(a.Conexao.UsuarioId);

        var encontradas = new List<ContaEncontradaDto>();
        var naoImportadas = new List<ContaNaoImportadaDto>();
        foreach (var conta in leitura.Contas.OrderBy(c => Vinculo.TipoDaConta(c), StringComparer.Ordinal).ThenBy(c => c.Nome, StringComparer.Ordinal))
        {
            var tipo = Vinculo.TipoDaConta(conta);
            var sugestao = SugestaoDeApelido.Para(a.Item.Banco, tipo, dono);
            var vinculo = vinculos.FirstOrDefault(v => v.Tipo == Vinculo.TipoDe(conta) && v.IdExterno == conta.Id);
            if (vinculo is { NaoImportada: true })
            {
                naoImportadas.Add(new ContaNaoImportadaDto(vinculo.Id, conta.Id, tipo, conta.Nome, sugestao));
                continue;
            }
            if (vinculo?.ContaId is { } nascida && existentes.Contains(nascida)) continue;

            var identidade = Vinculo.IdentidadeDe(conta, a.CodigoBanco, cofre);
            var jaVemDe = identidade is not null && deOutrosItems.TryGetValue(Convert.ToBase64String(identidade), out var usuario)
                ? apelidos.GetValueOrDefault(usuario, "?")
                : null;
            var (fechamento, vencimento) = leitura.FaturasPorCartao.TryGetValue(conta.Id, out var faturas)
                ? SugestaoDeApelido.DiasDoCartao([.. faturas])
                : (null, null);
            encontradas.Add(new ContaEncontradaDto(conta.Id, tipo, conta.Nome, sugestao, a.Conexao.UsuarioId, fechamento, vencimento,
                conta.LimiteCentavos, jaVemDe));
        }

        var aplicacoes = leitura.Aplicacoes
            .Where(ap => vinculos.FirstOrDefault(v => v.Tipo == TipoDeVinculo.Investimento && v.IdExterno == ap.Id)?.ContaId is not { } c
                         || !existentes.Contains(c))
            .OrderBy(ap => ap.Nome, StringComparer.Ordinal)
            .Select(ap => new AplicacaoEncontradaDto(ap.Id, ap.Nome, ap.Subtipo ?? ap.Tipo, ap.SaldoCentavos))
            .ToList();

        return new DescobertaDto(a.Item.Banco, encontradas, aplicacoes, naoImportadas);
    }

    /// <summary>Identidade (em base64) → dono da conexão, para os vínculos de outros items do casal.</summary>
    internal async Task<Dictionary<string, Guid>> IdentidadesDeOutrosItemsAsync(Guid itemId, CancellationToken ct)
    {
        var linhas = await (
            from v in db.Vinculos
            where v.ItemId != itemId && v.IdentidadeHmac != null
            join i in db.Items on v.ItemId equals i.Id
            join c in db.Conexoes on i.ConexaoId equals c.Id
            select new { v.IdentidadeHmac, c.UsuarioId }).AsNoTracking().ToListAsync(ct);
        return linhas.GroupBy(l => Convert.ToBase64String(l.IdentidadeHmac!)).ToDictionary(g => g.Key, g => g.First().UsuarioId);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pluggy recusou a leitura das contas do item (slot {Slot}, status {Status}, código {Codigo}).")]
    private static partial void BancoRecusou(ILogger logger, int slot, int status, string codigo);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pluggy indisponível na leitura das contas do item (slot {Slot}, {Tipo}).")]
    private static partial void PluggyFora(ILogger logger, int slot, string tipo);
}
