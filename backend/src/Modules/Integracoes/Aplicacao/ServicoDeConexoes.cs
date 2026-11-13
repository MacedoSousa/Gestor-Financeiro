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
using Npgsql;

namespace GestorFinanceiro.Modules.Integracoes.Aplicacao;

internal abstract record ResultadoDeConexoes
{
    public sealed record Lista(ConexoesDto Dto) : ResultadoDeConexoes;
    public sealed record Criada(ConexaoDto Conexao) : ResultadoDeConexoes;
    public sealed record ItemCriado(Guid ConexaoId, ItemDto Item) : ResultadoDeConexoes;
    public sealed record Feito : ResultadoDeConexoes;
    public sealed record Invalido(IReadOnlyDictionary<string, string[]> Erros) : ResultadoDeConexoes;
    public sealed record Recusado(string Titulo, int Status) : ResultadoDeConexoes;
    public sealed record NaoEncontrado : ResultadoDeConexoes;
}

/// <summary>
/// Conexões do Meu Pluggy do casal (D20, D21, D23). Cada cônjuge cadastra a própria conexão, escolhendo o slot das chaves
/// no servidor; o outro só vê. Todo item é validado no banco antes de gravar: autentica com as chaves do slot e faz
/// <c>GET /items/{id}</c>. O itemId e as chaves nunca vão para resposta, log ou exceção (regra 5, D26).
/// </summary>
internal sealed partial class ServicoDeConexoes(
    IntegracoesDbContext db,
    IUsuarioAtual atual,
    IConsultaDeUsuarios usuarios,
    SegredosDaPluggy segredos,
    IClientePluggy pluggy,
    ICofre cofre,
    TimeProvider relogio,
    IConsultaDeContas contas,
    ILogger<ServicoDeConexoes> log)
{
    internal const string TituloSlotSemChaves = "As chaves deste slot não foram gravadas no servidor.";
    internal const string TituloSlotEmUso = "Este slot já é usado no casal.";
    internal const string TituloJaTemConexao = "Você já tem uma conexão.";
    internal const string TituloItemRepetido = "Este item já está cadastrado.";
    internal const string TituloSoOTitular = "Só o titular pode alterar esta conexão.";
    internal const string TituloPluggyFora = "A Pluggy não respondeu. Tente de novo em alguns minutos.";
    internal const string TituloArquiveAntes = "Arquive antes as contas que vêm deste banco.";
    internal const string TituloBancoJaEscolhido = "O banco desta conexão já foi escolhido.";
    internal static string TituloRecusado(int slot) => $"O banco recusou este item com as chaves do slot {slot}.";

    private const string IndiceDoSlot = "ux_conexoes_slot";
    private const string IndiceDoUsuario = "ux_conexoes_usuario";
    private const string IndiceDoItem = "ux_items_item";

    private sealed record ItemPedido(string ItemId, string CodigoBanco, string Banco);

    public async Task<ResultadoDeConexoes> ListarAsync(CancellationToken ct)
    {
        if (atual.CasalId is not { } casal) return new ResultadoDeConexoes.NaoEncontrado();

        var conexoes = await db.Conexoes.AsNoTracking().OrderBy(c => c.Slot).ToListAsync(ct);
        var items = await db.Items.AsNoTracking().OrderBy(i => i.Id).ToListAsync(ct);
        var apelidos = (await usuarios.ListarDoCasalAsync(casal, ct)).ToDictionary(u => u.UsuarioId, u => u.Apelido);
        var configurados = Enumerable.Range(Conexao.SlotMinimo, Conexao.SlotMaximo).ToDictionary(s => s, s => segredos.Ler(s) is not null);
        var contasPorItem = await ContasPorItemAsync(ct);

        var dto = new ConexoesDto(
            conexoes.Select(c => Dto(c, apelidos, configurados[c.Slot], items.Where(i => i.ConexaoId == c.Id), contasPorItem)).ToList(),
            configurados.Select(s => new SlotDto(s.Key, s.Value, conexoes.Any(c => c.Slot == s.Key))).ToList(),
            BancosConhecidos.Lista);
        return new ResultadoDeConexoes.Lista(dto);
    }

    /// <summary>Cria a conexão de quem está na sessão com o primeiro banco, só se o banco aceitar o item com as chaves do slot.</summary>
    public async Task<ResultadoDeConexoes> CriarAsync(NovaConexaoDto pedido, CancellationToken ct)
    {
        if (atual.CasalId is not { } casal || atual.UsuarioId is not { } usuario) return new ResultadoDeConexoes.NaoEncontrado();

        var erros = new Dictionary<string, string[]>();
        if (pedido.Slot is not { } slot || !Conexao.SlotValido(slot)) erros["slot"] = ["Escolha o slot 1 ou o slot 2."];
        var item = ValidarItem(pedido.ItemId, pedido.CodigoBanco, pedido.Banco, erros);
        if (erros.Count > 0 || item is null) return new ResultadoDeConexoes.Invalido(erros);
        var slotEscolhido = pedido.Slot!.Value;

        if (await ConflitoDeConexaoAsync(usuario, slotEscolhido, ct) is { } conflito) return conflito;
        if (segredos.Ler(slotEscolhido) is not { } chaves) return new ResultadoDeConexoes.Recusado(TituloSlotSemChaves, 400);
        if (await ItemJaCadastradoAsync(item.ItemId, ct)) return new ResultadoDeConexoes.Recusado(TituloItemRepetido, 409);

        var (doBanco, recusa) = await ValidarNoBancoAsync(chaves, item.ItemId, ct);
        if (recusa is not null) return recusa;

        var conexao = Conexao.Criar(casal, usuario, slotEscolhido);
        var novo = NovoItem(casal, conexao.Id, item, doBanco!);
        return await NaTravaDoCasalAsync(casal, async () =>
        {
            if (await ConflitoDeConexaoAsync(usuario, slotEscolhido, ct) is { } depois) return depois;
            if (await ItemJaCadastradoAsync(item.ItemId, ct)) return new ResultadoDeConexoes.Recusado(TituloItemRepetido, 409);
            db.Conexoes.Add(conexao);
            db.Items.Add(novo);
            var apelidos = (await usuarios.ListarDoCasalAsync(casal, ct)).ToDictionary(u => u.UsuarioId, u => u.Apelido);
            return new ResultadoDeConexoes.Criada(Dto(conexao, apelidos, true, [novo]));
        }, ct);
    }

    /// <summary>Mais um banco na própria conexão, validado com as chaves do slot dela.</summary>
    public async Task<ResultadoDeConexoes> AcrescentarItemAsync(Guid conexaoId, NovoItemDto pedido, CancellationToken ct)
    {
        if (atual.CasalId is not { } casal) return new ResultadoDeConexoes.NaoEncontrado();
        var (conexao, recusaDoTitular) = await DoTitularAsync(conexaoId, ct);
        if (recusaDoTitular is not null) return recusaDoTitular;

        var erros = new Dictionary<string, string[]>();
        var item = ValidarItem(pedido.ItemId, pedido.CodigoBanco, pedido.Banco, erros);
        if (erros.Count > 0 || item is null) return new ResultadoDeConexoes.Invalido(erros);

        if (segredos.Ler(conexao!.Slot) is not { } chaves) return new ResultadoDeConexoes.Recusado(TituloSlotSemChaves, 400);
        if (await ItemJaCadastradoAsync(item.ItemId, ct)) return new ResultadoDeConexoes.Recusado(TituloItemRepetido, 409);

        var (doBanco, recusa) = await ValidarNoBancoAsync(chaves, item.ItemId, ct);
        if (recusa is not null) return recusa;

        var novo = NovoItem(casal, conexao.Id, item, doBanco!);
        return await NaTravaDoCasalAsync(casal, async () =>
        {
            if (!await db.Conexoes.AnyAsync(c => c.Id == conexao.Id, ct)) return new ResultadoDeConexoes.NaoEncontrado();
            if (await ItemJaCadastradoAsync(item.ItemId, ct)) return new ResultadoDeConexoes.Recusado(TituloItemRepetido, 409);
            db.Items.Add(novo);
            return new ResultadoDeConexoes.ItemCriado(conexao.Id, Dto(novo));
        }, ct);
    }

    public async Task<ResultadoDeConexoes> ApagarItemAsync(Guid conexaoId, Guid itemId, CancellationToken ct)
    {
        if (atual.CasalId is not { } casal) return new ResultadoDeConexoes.NaoEncontrado();
        var (conexao, recusa) = await DoTitularAsync(conexaoId, ct);
        if (recusa is not null) return recusa;

        return await NaTravaDoCasalAsync(casal, async () =>
        {
            var item = await db.Items.FirstOrDefaultAsync(i => i.Id == itemId && i.ConexaoId == conexao!.Id, ct);
            if (item is null) return new ResultadoDeConexoes.NaoEncontrado();
            if (await SoltarVinculosAsync([item.Id], ct) is { } recusa) return recusa;
            db.Items.Remove(item);
            return new ResultadoDeConexoes.Feito();
        }, ct);
    }

    /// <summary>
    /// Escolha do banco de um item cadastrado antes do código (D18): só o titular, uma vez só (as identidades das contas
    /// dependem dele). Nada muda na Pluggy.
    /// </summary>
    public async Task<ResultadoDeConexoes> EscolherBancoAsync(Guid itemId, BancoDoItemDto pedido, CancellationToken ct)
    {
        if (atual.CasalId is not { } casal) return new ResultadoDeConexoes.NaoEncontrado();
        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null) return new ResultadoDeConexoes.NaoEncontrado();
        var (_, recusa) = await DoTitularAsync(item.ConexaoId, ct);
        if (recusa is not null) return recusa;

        var erros = new Dictionary<string, string[]>();
        var banco = ValidarBanco(pedido.CodigoBanco, pedido.Banco, erros);
        if (erros.Count > 0 || banco is null) return new ResultadoDeConexoes.Invalido(erros);

        return await NaTravaDoCasalAsync(casal, async () =>
        {
            var noBanco = await db.Items.FirstOrDefaultAsync(i => i.Id == itemId, ct);
            if (noBanco is null) return new ResultadoDeConexoes.NaoEncontrado();
            if (noBanco.CodigoBanco is not null) return new ResultadoDeConexoes.Recusado(TituloBancoJaEscolhido, 409);
            noBanco.EscolherBanco(banco.Value.Codigo, banco.Value.Nome);
            return new ResultadoDeConexoes.Feito();
        }, ct);
    }

    /// <summary>Apaga a própria conexão e os items dela (soft delete); o slot e o cônjuge ficam livres para um novo cadastro.</summary>
    public async Task<ResultadoDeConexoes> ApagarAsync(Guid conexaoId, CancellationToken ct)
    {
        if (atual.CasalId is not { } casal) return new ResultadoDeConexoes.NaoEncontrado();
        var (_, recusa) = await DoTitularAsync(conexaoId, ct);
        if (recusa is not null) return recusa;

        return await NaTravaDoCasalAsync(casal, async () =>
        {
            var conexao = await db.Conexoes.FirstOrDefaultAsync(c => c.Id == conexaoId, ct);
            if (conexao is null) return new ResultadoDeConexoes.NaoEncontrado();
            var items = await db.Items.Where(i => i.ConexaoId == conexaoId).ToListAsync(ct);
            if (await SoltarVinculosAsync(items.Select(i => i.Id).ToList(), ct) is { } recusa) return recusa;
            db.Items.RemoveRange(items);
            db.Conexoes.Remove(conexao);
            return new ResultadoDeConexoes.Feito();
        }, ct);
    }

    // ------------------------------------------------------------------ regras

    /// <summary>
    /// Decisão do CEO na E3: com conta não arquivada vindo destes items, recusa (409); a pessoa arquiva antes e o histórico
    /// fica. Senão, os vínculos vão junto (soft delete) e as contas, já arquivadas, continuam como estão.
    /// </summary>
    private async Task<ResultadoDeConexoes?> SoltarVinculosAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct)
    {
        var vinculos = await db.Vinculos.Where(v => itemIds.Contains(v.ItemId)).ToListAsync(ct);
        foreach (var contaId in vinculos.Where(v => v.ContaId is not null).Select(v => v.ContaId!.Value).Distinct())
            if (await contas.ObterAsync(contaId, ct) is { Arquivada: false })
                return new ResultadoDeConexoes.Recusado(TituloArquiveAntes, 409);
        db.Vinculos.RemoveRange(vinculos);
        return null;
    }

    private static ItemPedido? ValidarItem(string? itemId, string? codigoBanco, string? banco, Dictionary<string, string[]> erros)
    {
        var id = itemId?.Trim() ?? "";
        if (id.Length == 0) erros["itemId"] = ["Cole o id do item, copiado do Meu Pluggy."];
        else if (id.Length > Item.ItemIdMaximo) erros["itemId"] = [$"O id do item tem no máximo {Item.ItemIdMaximo} caracteres."];
        var escolhido = ValidarBanco(codigoBanco, banco, erros);
        return id.Length is > 0 and <= Item.ItemIdMaximo && escolhido is { } b ? new ItemPedido(id, b.Codigo, b.Nome) : null;
    }

    /// <summary>
    /// Código COMPE de 3 dígitos (D18). Da lista: o nome é o da lista e o digitado é ignorado. "Outro": o nome digitado,
    /// de 1 a 60 caracteres.
    /// </summary>
    private static (string Codigo, string Nome)? ValidarBanco(string? codigoBanco, string? banco, Dictionary<string, string[]> erros)
    {
        var codigo = codigoBanco?.Trim() ?? "";
        if (codigo.Length == 0)
        {
            erros["codigoBanco"] = ["Escolha o banco."];
            return null;
        }
        if (!Item.CodigoValido(codigo))
        {
            erros["codigoBanco"] = ["O código do banco tem 3 dígitos."];
            return null;
        }
        if (BancosConhecidos.NomeDe(codigo) is { } daLista) return (codigo, daLista);
        var nome = banco?.Trim() ?? "";
        if (nome.Length == 0) erros["banco"] = ["Informe o nome do banco."];
        else if (nome.Length > Item.BancoMaximo) erros["banco"] = [$"O nome do banco tem no máximo {Item.BancoMaximo} caracteres."];
        return nome.Length is > 0 and <= Item.BancoMaximo ? (codigo, nome) : null;
    }

    private async Task<ResultadoDeConexoes?> ConflitoDeConexaoAsync(Guid usuario, int slot, CancellationToken ct)
    {
        if (await db.Conexoes.AnyAsync(c => c.UsuarioId == usuario, ct)) return new ResultadoDeConexoes.Recusado(TituloJaTemConexao, 409);
        if (await db.Conexoes.AnyAsync(c => c.Slot == slot, ct)) return new ResultadoDeConexoes.Recusado(TituloSlotEmUso, 409);
        return null;
    }

    private async Task<bool> ItemJaCadastradoAsync(string itemId, CancellationToken ct)
    {
        var indice = Item.Indice(itemId, cofre);
        return await db.Items.AnyAsync(i => i.ItemIdHmac == indice, ct);
    }

    /// <summary>A conexão do casal, se for de quem está na sessão; senão 404 ou 403 (D21).</summary>
    private async Task<(Conexao? Conexao, ResultadoDeConexoes? Recusa)> DoTitularAsync(Guid conexaoId, CancellationToken ct)
    {
        var conexao = await db.Conexoes.AsNoTracking().FirstOrDefaultAsync(c => c.Id == conexaoId, ct);
        if (conexao is null) return (null, new ResultadoDeConexoes.NaoEncontrado());
        if (conexao.UsuarioId != atual.UsuarioId) return (null, new ResultadoDeConexoes.Recusado(TituloSoOTitular, 403));
        return (conexao, null);
    }

    /// <summary>
    /// Autentica com as chaves do slot e consulta o item. Resposta de erro da Pluggy = recusa (400); rede ou tempo
    /// esgotado = 503. O log leva só o slot, o status e o código (D26); nunca o item, as chaves ou a mensagem da exceção.
    /// </summary>
    private async Task<(ItemPluggy? Item, ResultadoDeConexoes? Recusa)> ValidarNoBancoAsync(
        CredenciaisPluggy chaves, string itemId, CancellationToken ct)
    {
        try
        {
            return (await pluggy.ObterItemAsync(chaves, Item.NormalizarItemId(itemId), ct), null);
        }
        catch (ErroDaPluggy e)
        {
            ItemRecusado(log, chaves.Slot, e.Status, e.Codigo ?? "-");
            return (null, new ResultadoDeConexoes.Recusado(TituloRecusado(chaves.Slot), 400));
        }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            PluggyFora(log, chaves.Slot, e.GetType().Name);
            return (null, new ResultadoDeConexoes.Recusado(TituloPluggyFora, 503));
        }
    }

    private Item NovoItem(Guid casal, Guid conexaoId, ItemPedido pedido, ItemPluggy doBanco)
    {
        var (estado, motivo) = EstadoDoItem.Do(doBanco.Status, doBanco.ConsentimentoVenceEm, relogio.GetUtcNow());
        return Item.Criar(casal, conexaoId, pedido.ItemId, pedido.CodigoBanco, pedido.Banco, estado, motivo, doBanco.ConsentimentoVenceEm, cofre);
    }

    /// <summary>
    /// Transação com <c>pg_advisory_xact_lock</c> por casal: escritas de conexões do casal ficam em fila. Se ainda assim um
    /// índice parcial acusar 23505, vira o 409 correspondente. Só grava quando a ação dá certo.
    /// </summary>
    private async Task<ResultadoDeConexoes> NaTravaDoCasalAsync(Guid casalId, Func<Task<ResultadoDeConexoes>> acao, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended('integracoes.casal:' || {casalId}::text, 0))", ct);
        try
        {
            var resultado = await acao();
            if (resultado is not (ResultadoDeConexoes.Criada or ResultadoDeConexoes.ItemCriado or ResultadoDeConexoes.Feito))
            {
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                return resultado;
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return resultado;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: IndiceDoSlot or IndiceDoUsuario or IndiceDoItem,
        } pg)
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return new ResultadoDeConexoes.Recusado(pg.ConstraintName switch
            {
                IndiceDoSlot => TituloSlotEmUso,
                IndiceDoUsuario => TituloJaTemConexao,
                _ => TituloItemRepetido,
            }, 409);
        }
    }

    // ------------------------------------------------------------------ DTOs

    private ConexaoDto Dto(Conexao c, IReadOnlyDictionary<Guid, string> apelidos, bool slotConfigurado, IEnumerable<Item> items,
        IReadOnlyDictionary<Guid, List<ContaDoItemDto>>? contasPorItem = null) =>
        new(c.Id, c.UsuarioId, apelidos.GetValueOrDefault(c.UsuarioId, "?"), c.Slot, slotConfigurado,
            c.UsuarioId == atual.UsuarioId, items.Select(i => Dto(i, contasPorItem?.GetValueOrDefault(i.Id))).ToList());

    private static ItemDto Dto(Item i, IReadOnlyList<ContaDoItemDto>? contas = null) =>
        new(i.Id, i.Banco, i.CodigoBanco, i.Estado, i.MotivoErro,
            i.ConsentimentoVenceEm is { } vence ? ConversaoPluggy.DiaDeSaoPaulo(vence) : null, i.UltimaSincronizacaoEm, i.UltimaTentativaEm,
            contas ?? []);

    /// <summary>
    /// Contas e cartões nascidos de cada item (vínculo CONTA ou CARTAO com conta que existe no casal), com o histórico e as
    /// diferenças que já persistiram 2 sincronizações (Tarefa 4.5). Sem id do banco.
    /// </summary>
    private async Task<Dictionary<Guid, List<ContaDoItemDto>>> ContasPorItemAsync(CancellationToken ct)
    {
        var vinculos = await db.Vinculos.AsNoTracking().Where(v => v.ContaId != null).ToListAsync(ct);
        if (vinculos.Count == 0) return [];
        var doCasal = (await contas.ListarAsync(ct)).ToDictionary(k => k.Id);
        var grupos = Grupos(vinculos);
        return vinculos
            .Where(v => doCasal.ContainsKey(v.ContaId!.Value))
            .GroupBy(v => v.ItemId)
            .ToDictionary(g => g.Key, g => g
                // Um grupo aparece uma vez por item, pela primeira aplicação dele.
                .DistinctBy(v => (v.Tipo == TipoDeVinculo.Investimento, v.Tipo == TipoDeVinculo.Investimento ? v.ContaId : v.Id))
                .Select(v => (Vinculo: v, Conta: doCasal[v.ContaId!.Value]))
                .OrderBy(x => x.Conta.Apelido, StringComparer.CurrentCulture)
                .Select(x => x.Vinculo.Tipo == TipoDeVinculo.Investimento
                    ? new ContaDoItemDto(x.Conta.Id, x.Conta.Apelido, x.Conta.Tipo, x.Conta.Arquivada, null, grupos[x.Conta.Id].DiferencaCentavos, [],
                        grupos[x.Conta.Id].ValorNoBancoCentavos)
                    : new ContaDoItemDto(x.Conta.Id, x.Conta.Apelido, x.Conta.Tipo, x.Conta.Arquivada, x.Vinculo.HistoricoDesde,
                        x.Vinculo.Tipo == TipoDeVinculo.Conta ? x.Vinculo.DiferencaVisivel : null,
                        x.Vinculo.FaturasVisiveis.Select(f => new FaturaDivergenteDto(f.Vencimento, f.DiferencaCentavos)).ToList()))
                .ToList());
    }

    /// <summary>Grupos de investimento do casal e o que o banco diz deles na última sincronização (D21), para a tela de Contas.</summary>
    public async Task<IReadOnlyList<GrupoNoBancoDto>> GruposDeInvestimentoAsync(CancellationToken ct)
    {
        var vinculos = await db.Vinculos.AsNoTracking()
            .Where(v => v.ContaId != null && v.Tipo == TipoDeVinculo.Investimento)
            .ToListAsync(ct);
        if (vinculos.Count == 0) return [];
        var doCasal = (await contas.ListarAsync(ct)).Select(k => k.Id).ToHashSet();
        return Grupos(vinculos).Values.Where(g => doCasal.Contains(g.ContaId)).OrderBy(g => g.ContaId).ToList();
    }

    /// <summary>
    /// Por grupo (conta das aplicações): valor no banco = soma das aplicações, só se todas têm valor; diferença = a gravada
    /// na última sincronização (a mesma em todas as aplicações do grupo).
    /// </summary>
    private static Dictionary<Guid, GrupoNoBancoDto> Grupos(IEnumerable<Vinculo> vinculos) =>
        vinculos.Where(v => v.Tipo == TipoDeVinculo.Investimento && v.ContaId != null)
            .GroupBy(v => v.ContaId!.Value)
            .ToDictionary(g => g.Key, g => new GrupoNoBancoDto(g.Key,
                g.All(v => v.ValorBancoCentavos is not null) ? g.Sum(v => v.ValorBancoCentavos!.Value) : null,
                g.Select(v => v.DiferencaCentavos).FirstOrDefault(d => d is not null)));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pluggy recusou o item no cadastro (slot {Slot}, status {Status}, código {Codigo}).")]
    private static partial void ItemRecusado(ILogger logger, int slot, int status, string codigo);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pluggy indisponível no cadastro do item (slot {Slot}, {Tipo}).")]
    private static partial void PluggyFora(ILogger logger, int slot, string tipo);
}
