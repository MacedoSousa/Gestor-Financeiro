using GestorFinanceiro.Modules.Contas.Contracts;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.Modules.Integracoes.Contracts;
using GestorFinanceiro.Modules.Integracoes.Dominio;
using GestorFinanceiro.Modules.Integracoes.Infra;
using GestorFinanceiro.Modules.Integracoes.Pluggy;
using GestorFinanceiro.Modules.Investimentos.Contracts;
using GestorFinanceiro.SharedKernel.Aplicacao;
using GestorFinanceiro.SharedKernel.Seguranca;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GestorFinanceiro.Modules.Integracoes.Aplicacao;

/// <summary>
/// Nascimento das contas ligadas (Tarefa 3.3): o que a pessoa confirmou na tela vira vínculo e conta. Em duas fases (D17):
/// primeiro os vínculos, numa transação do Integracoes, já com o id da conta; depois cada conta e cada meta, cada uma numa
/// transação do módulo dono. Os contratos são idempotentes pelo id, então repetir o pedido (ou retomar depois de uma falha
/// no meio) completa o que faltou sem duplicar nada.
/// </summary>
internal sealed class Nascimento(
    IntegracoesDbContext db,
    IUsuarioAtual atual,
    Descoberta descoberta,
    IContasLigadas contasLigadas,
    IMetasDeContas metas,
    IConsultaDeMetas metasDoCasal,
    IConsultaDeContas consultaDeContas,
    IConsultaDeUsuarios usuarios,
    ICofre cofre,
    TimeProvider relogio)
{
    internal const string Criar = "CRIAR";
    internal const string Ligar = "LIGAR";
    internal const string NaoImportar = "NAO_IMPORTAR";

    internal const string TituloApelidoRepetido = "Já existe uma conta com esse nome.";
    internal const string TituloContaJaRecebe = "Esta conta já recebe outra conta do banco.";
    internal const string TituloJaTemConta = "Esta conta do banco já está ligada a outra conta.";
    internal const string TituloSoNaoImportada = "Esta conta não está marcada como \"não importar\".";
    internal const string TituloJaVemDeOutraConexao = "Esta conta já vem de outra conexão.";
    internal const string TituloTenteDeNovo = "Outra confirmação deste banco estava em andamento. Tente de novo.";
    internal const string TituloContaSumiu = "A conta escolhida já não existe. Atualize a página.";
    internal const string TituloOutroGrupo = "Esta aplicação já está em outro grupo.";
    internal static string TituloJaVemDe(string nome) => $"Esta conta já vem da conexão de {nome}.";
    internal static string TituloMeta(string grupo) => $"Não deu para ligar o grupo {grupo} à meta escolhida.";

    /// <summary>Saldo inicial provisório das contas: 0 no início do histórico; a etapa 4 recalcula (D3).</summary>
    internal static readonly DateOnly InicioDoHistorico = new(2026, 1, 1);

    private const int NomeMaximo = 60;
    private const string IndiceDaIdentidade = "ux_vinculos_identidade";
    private const string IndiceDoExterno = "ux_vinculos_externo";

    private static readonly HashSet<string> TiposDeConta = new(StringComparer.Ordinal) { "CORRENTE", "POUPANCA" };

    /// <summary>O que falta fazer nos módulos donos, depois dos vínculos gravados.</summary>
    private abstract record Tarefa
    {
        public sealed record CriarConta(string IdExterno, NovaContaLigada Pedido) : Tarefa;
        public sealed record MarcarLigada(Guid ContaId) : Tarefa;
        public sealed record CriarGrupo(string Nome, NovaContaLigada Pedido, Guid? MetaId) : Tarefa;
    }

    public async Task<ResultadoDeNascimento> NascerAsync(Guid itemId, NascimentoDto pedido, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pedido);
        var (acesso, recusa) = await descoberta.AcessoAsync(itemId, ct);
        if (recusa is not null) return recusa;
        var (leitura, falha) = await descoberta.LerAsync(acesso!, ct);
        if (falha is not null) return falha;
        return await ConfirmarAsync(acesso!, leitura!, pedido.Contas ?? [], pedido.Aplicacoes ?? [], ct);
    }

    /// <summary>Inclui uma conta marcada "não importar" (decisão 16): segue como um nascimento normal.</summary>
    public async Task<ResultadoDeNascimento> IncluirAsync(Guid vinculoId, InclusaoDto pedido, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pedido);
        if (atual.CasalId is null) return new ResultadoDeNascimento.NaoEncontrado();
        var vinculo = await db.Vinculos.AsNoTracking().FirstOrDefaultAsync(v => v.Id == vinculoId, ct);
        if (vinculo is null) return new ResultadoDeNascimento.NaoEncontrado();
        var (acesso, recusa) = await descoberta.AcessoAsync(vinculo.ItemId, ct);
        if (recusa is not null) return recusa;
        if (!vinculo.NaoImportada) return new ResultadoDeNascimento.Recusado(TituloSoNaoImportada, 409);
        if (pedido.Acao is not (Criar or Ligar))
            return new ResultadoDeNascimento.Invalido(new Dictionary<string, string[]> { ["acao"] = ["Use CRIAR ou LIGAR."] });

        var (leitura, falha) = await descoberta.LerAsync(acesso!, ct);
        if (falha is not null) return falha;
        var conta = new ContaConfirmadaDto(vinculo.IdExterno, pedido.Acao, pedido.ContaExistenteId, pedido.Apelido, pedido.TitularUsuarioId,
            pedido.DiaFechamento, pedido.DiaVencimento);
        return await ConfirmarAsync(acesso!, leitura!, [conta], [], ct);
    }

    private async Task<ResultadoDeNascimento> ConfirmarAsync(AcessoAoItem a, LeituraDoBanco leitura,
        IReadOnlyList<ContaConfirmadaDto> contas, IReadOnlyList<AplicacaoConfirmadaDto> aplicacoes, CancellationToken ct)
    {
        var erros = await ValidarAsync(a.Item.CasalId, leitura, contas, aplicacoes, ct);
        if (erros.Count > 0) return new ResultadoDeNascimento.Invalido(erros);

        var tarefas = new List<Tarefa>();
        var vinculos = await NaTravaDoCasalAsync(a.Item.CasalId, () => GravarVinculosAsync(a, leitura, contas, aplicacoes, tarefas, ct), ct);
        if (vinculos is not ResultadoDeNascimento.Feito) return vinculos;

        foreach (var tarefa in tarefas)
            if (await ExecutarAsync(tarefa, ct) is { } recusa)
                return recusa;
        return new ResultadoDeNascimento.Feito();
    }

    // ------------------------------------------------------------------ fase 1: vínculos

    private async Task<ResultadoDeNascimento> GravarVinculosAsync(AcessoAoItem a, LeituraDoBanco leitura,
        IReadOnlyList<ContaConfirmadaDto> contas, IReadOnlyList<AplicacaoConfirmadaDto> aplicacoes, List<Tarefa> tarefas, CancellationToken ct)
    {
        var casal = a.Item.CasalId;
        var vinculos = await db.Vinculos.Where(v => v.ItemId == a.Item.Id).ToListAsync(ct);
        var deOutrosItems = await descoberta.IdentidadesDeOutrosItemsAsync(a.Item.Id, ct);
        var doBanco = leitura.Contas.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var doCasal = (await consultaDeContas.ListarAsync(ct)).ToDictionary(c => c.Id);
        var agora = relogio.GetUtcNow();

        foreach (var confirmada in contas)
        {
            var conta = doBanco[confirmada.IdExterno!];
            var tipo = Vinculo.TipoDe(conta);
            var identidade = Vinculo.IdentidadeDe(conta, cofre);
            var vinculo = vinculos.FirstOrDefault(v => v.Tipo == tipo && v.IdExterno == conta.Id);

            if (confirmada.Acao == NaoImportar)
            {
                if (vinculo is null) db.Vinculos.Add(Vinculo.NaoImportar(casal, a.Item.Id, tipo, conta.Id, identidade, agora));
                continue;
            }

            if (vinculo is null && identidade is not null && deOutrosItems.TryGetValue(Convert.ToBase64String(identidade), out var dono))
            {
                var nome = (await usuarios.ListarDoCasalAsync(casal, ct)).FirstOrDefault(u => u.UsuarioId == dono)?.Apelido;
                return new ResultadoDeNascimento.Recusado(nome is null ? TituloJaVemDeOutraConexao : TituloJaVemDe(nome), 409);
            }

            var ligar = confirmada.Acao == Ligar;
            var destino = ligar ? confirmada.ContaExistenteId!.Value : vinculo?.ContaId ?? Guid.CreateVersion7();
            if (ligar && await db.Vinculos.AnyAsync(v => v.ContaId == destino && v.Id != (vinculo == null ? Guid.Empty : vinculo.Id), ct))
                return new ResultadoDeNascimento.Recusado(TituloContaJaRecebe, 409);

            if (vinculo is null)
                db.Vinculos.Add(Vinculo.Criar(casal, a.Item.Id, tipo, conta.Id, destino, identidade));
            else if (vinculo.ContaId is null)
                vinculo.DefinirConta(destino);
            else if (vinculo.ContaId != destino)
            {
                // A conta do vínculo existe no casal: ele já está ligado a ela (ou um LIGAR ficou pela metade); não muda de
                // alvo. Se ela nunca chegou a existir, o LIGAR pode escolher outra.
                if (doCasal.ContainsKey(vinculo.ContaId.Value) || !ligar) return new ResultadoDeNascimento.Recusado(TituloJaTemConta, 409);
                vinculo.TrocarConta(destino);
            }
            else if (!ligar && doCasal.TryGetValue(destino, out var atual) && !atual.Ligada)
            {
                // CRIAR sobre um LIGAR pela metade: a pessoa escolhe "Ligar" de novo, explicitamente.
                return new ResultadoDeNascimento.Recusado(TituloJaTemConta, 409);
            }

            tarefas.Add(ligar
                ? new Tarefa.MarcarLigada(destino)
                : new Tarefa.CriarConta(conta.Id, NovaConta(a, conta, confirmada, destino)));
        }

        return GravarAplicacoes(a, leitura, aplicacoes, vinculos, doCasal, tarefas) ?? new ResultadoDeNascimento.Feito();
    }

    /// <summary>
    /// Cada grupo distinto (nome sem caixa e sem espaços em volta) é uma conta INVESTIMENTO; as aplicações do grupo apontam
    /// para ela. Saldo inicial = soma das aplicações, no dia do nascimento (São Paulo). Sem grupo, a aplicação aguarda.
    /// </summary>
    private ResultadoDeNascimento? GravarAplicacoes(AcessoAoItem a, LeituraDoBanco leitura, IReadOnlyList<AplicacaoConfirmadaDto> aplicacoes,
        List<Vinculo> vinculos, IReadOnlyDictionary<Guid, ContaResumo> doCasal, List<Tarefa> tarefas)
    {
        var doBanco = leitura.Aplicacoes.ToDictionary(ap => ap.Id, StringComparer.Ordinal);
        Vinculo? Existente(string id) => vinculos.FirstOrDefault(v => v.Tipo == TipoDeVinculo.Investimento && v.IdExterno == id);

        foreach (var semGrupo in aplicacoes.Where(ap => ap.Grupo is null && Existente(ap.IdExterno!) is null))
            db.Vinculos.Add(Vinculo.Criar(a.Item.CasalId, a.Item.Id, TipoDeVinculo.Investimento, semGrupo.IdExterno!, null, null));

        foreach (var grupo in aplicacoes.Where(ap => ap.Grupo is not null).GroupBy(ap => ap.Grupo!.Nome!.Trim().ToUpperInvariant()))
        {
            var membros = grupo.ToList();
            var nome = membros[0].Grupo!.Nome!.Trim();
            // Aplicação já nascida não troca de grupo: nem misturada com outra conta, nem levada para um grupo de outro nome.
            var jaTem = membros.Select(m => Existente(m.IdExterno!)?.ContaId).OfType<Guid>().Distinct().ToList();
            if (jaTem.Count > 1 || (jaTem.Count == 1 && doCasal.TryGetValue(jaTem[0], out var daConta)
                                    && !string.Equals(daConta.Apelido, nome, StringComparison.OrdinalIgnoreCase)))
                return new ResultadoDeNascimento.Recusado(TituloOutroGrupo, 409);
            var contaId = jaTem.Count == 1 ? jaTem[0] : Guid.CreateVersion7();
            foreach (var m in membros)
            {
                var vinculo = Existente(m.IdExterno!);
                if (vinculo is null) db.Vinculos.Add(Vinculo.Criar(a.Item.CasalId, a.Item.Id, TipoDeVinculo.Investimento, m.IdExterno!, contaId, null));
                else if (vinculo.ContaId is null) vinculo.DefinirConta(contaId);
            }

            var saldo = membros.Sum(m => doBanco[m.IdExterno!].SaldoCentavos);
            var pedido = new NovaContaLigada(contaId, "INVESTIMENTO", nome, a.Item.Banco, a.Conexao.UsuarioId, saldo,
                ConversaoPluggy.DiaDeSaoPaulo(relogio.GetUtcNow()), null, null, null);
            tarefas.Add(new Tarefa.CriarGrupo(nome, pedido, membros.Select(m => m.Grupo!.MetaId).FirstOrDefault(id => id is not null)));
        }
        return null;
    }

    private static NovaContaLigada NovaConta(AcessoAoItem a, ContaPluggy conta, ContaConfirmadaDto c, Guid contaId)
    {
        var tipo = Vinculo.TipoDaConta(conta);
        var cartao = tipo == "CARTAO";
        return new NovaContaLigada(contaId, tipo, c.Apelido!.Trim(), a.Item.Banco, c.TitularUsuarioId, 0, InicioDoHistorico,
            cartao ? c.DiaFechamento : null, cartao ? c.DiaVencimento : null, cartao ? conta.LimiteCentavos : null);
    }

    // ------------------------------------------------------------------ fase 2: módulos donos

    private async Task<ResultadoDeNascimento?> ExecutarAsync(Tarefa tarefa, CancellationToken ct)
    {
        switch (tarefa)
        {
            case Tarefa.CriarConta t:
                return Traduzir(await CriarAsync(t.Pedido, ct), t.IdExterno);
            case Tarefa.MarcarLigada t:
                return await contasLigadas.MarcarLigadaAsync(t.ContaId, ct) ? null : new ResultadoDeNascimento.Recusado(TituloContaSumiu, 409);
            case Tarefa.CriarGrupo t:
                if (Traduzir(await CriarAsync(t.Pedido, ct), null) is { } recusa) return recusa;
                if (t.MetaId is { } meta && !await metas.LigarContaAMetaAsync(t.Pedido.ContaId, meta, ct))
                    return new ResultadoDeNascimento.Recusado(TituloMeta(t.Nome), 409);
                return null;
            default:
                throw new InvalidOperationException(tarefa.GetType().Name);
        }
    }

    /// <summary>
    /// Cria a conta pelo contrato. Se o id do vínculo ficou indisponível (a conta dele foi apagada, por exemplo), troca o id
    /// nos vínculos, na trava do casal, e tenta uma vez com o id novo.
    /// </summary>
    private async Task<ResultadoDaContaLigada> CriarAsync(NovaContaLigada pedido, CancellationToken ct)
    {
        var resultado = await contasLigadas.CriarAsync(pedido, ct);
        if (resultado is not ResultadoDaContaLigada.IdEmUso) return resultado;

        var novo = Guid.CreateVersion7();
        var casal = atual.CasalId ?? throw new InvalidOperationException("Nascimento sem casal.");
        var troca = await NaTravaDoCasalAsync(casal, async () =>
        {
            foreach (var v in await db.Vinculos.Where(v => v.ContaId == pedido.ContaId).ToListAsync(ct)) v.TrocarConta(novo);
            return new ResultadoDeNascimento.Feito();
        }, ct);
        if (troca is not ResultadoDeNascimento.Feito) throw new InvalidOperationException("Não deu para trocar o id da conta.");

        resultado = await contasLigadas.CriarAsync(pedido with { ContaId = novo }, ct);
        return resultado is ResultadoDaContaLigada.IdEmUso ? throw new InvalidOperationException("Id novo também indisponível.") : resultado;
    }

    private static ResultadoDeNascimento? Traduzir(ResultadoDaContaLigada resultado, string? idExterno) => resultado switch
    {
        ResultadoDaContaLigada.Pronta => null,
        ResultadoDaContaLigada.ApelidoRepetido r => new ResultadoDeNascimento.Recusado(TituloApelidoRepetido, 409,
            new Dictionary<string, object?> { ["sugestao"] = r.Sugestao, ["idExterno"] = idExterno }),
        ResultadoDaContaLigada.Invalida i => new ResultadoDeNascimento.Invalido(i.Erros),
        _ => throw new InvalidOperationException(resultado.GetType().Name),
    };

    // ------------------------------------------------------------------ validação

    private async Task<Dictionary<string, string[]>> ValidarAsync(Guid casal, LeituraDoBanco leitura, IReadOnlyList<ContaConfirmadaDto> contas,
        IReadOnlyList<AplicacaoConfirmadaDto> aplicacoes, CancellationToken ct)
    {
        var erros = new Dictionary<string, string[]>();
        // Titular e meta são conferidos antes de gravar qualquer coisa: o que vem do cliente tem de ser do casal da sessão.
        var membros = (await usuarios.ListarDoCasalAsync(casal, ct)).Select(u => u.UsuarioId).ToHashSet();
        var metasDoPainel = aplicacoes.Any(ap => ap.Grupo?.MetaId is not null)
            ? (await metasDoCasal.PainelAsync(ct)).Metas.Select(m => m.Id).ToHashSet()
            : [];
        var doBanco = leitura.Contas.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var vistas = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < contas.Count; i++)
        {
            var c = contas[i];
            var chave = $"contas[{i}]";
            if (c.IdExterno is null || !doBanco.TryGetValue(c.IdExterno, out var conta))
            {
                erros[$"{chave}.idExterno"] = ["Esta conta não está no banco."];
                continue;
            }
            if (!vistas.Add(c.IdExterno)) erros[$"{chave}.idExterno"] = ["Conta repetida na confirmação."];

            switch (c.Acao)
            {
                case Criar when c.Apelido?.Trim() is not { Length: > 0 and <= NomeMaximo }:
                    erros[$"{chave}.apelido"] = [$"O apelido deve ter de 1 a {NomeMaximo} caracteres."];
                    break;
                case Criar when c.TitularUsuarioId is { } titular && !membros.Contains(titular):
                    erros[$"{chave}.titularUsuarioId"] = ["O titular precisa ser um dos cônjuges do casal."];
                    break;
                case Criar or NaoImportar:
                    break;
                case Ligar when c.ContaExistenteId is not { } existente:
                    erros[$"{chave}.contaExistenteId"] = ["Escolha a conta que vai receber esta."];
                    break;
                case Ligar:
                    var alvo = await consultaDeContas.ObterAsync(c.ContaExistenteId!.Value, ct);
                    var compativel = Vinculo.TipoDe(conta) == TipoDeVinculo.Cartao ? alvo?.Tipo == "CARTAO" : alvo is not null && TiposDeConta.Contains(alvo.Tipo);
                    if (!compativel) erros[$"{chave}.contaExistenteId"] = ["Escolha uma conta do mesmo tipo (corrente ou poupança, ou cartão)."];
                    break;
                default:
                    erros[$"{chave}.acao"] = ["Use CRIAR, LIGAR ou NAO_IMPORTAR."];
                    break;
            }
        }

        var aplicacoesDoBanco = leitura.Aplicacoes.Select(ap => ap.Id).ToHashSet(StringComparer.Ordinal);
        vistas.Clear();
        for (var i = 0; i < aplicacoes.Count; i++)
        {
            var ap = aplicacoes[i];
            var chave = $"aplicacoes[{i}]";
            if (ap.IdExterno is null || !aplicacoesDoBanco.Contains(ap.IdExterno)) erros[$"{chave}.idExterno"] = ["Esta aplicação não está no banco."];
            else if (!vistas.Add(ap.IdExterno)) erros[$"{chave}.idExterno"] = ["Aplicação repetida na confirmação."];
            if (ap.Grupo is not null && ap.Grupo.Nome?.Trim() is not { Length: > 0 and <= NomeMaximo })
                erros[$"{chave}.grupo.nome"] = [$"O nome do grupo deve ter de 1 a {NomeMaximo} caracteres."];
            if (ap.Grupo?.MetaId is { } meta && !metasDoPainel.Contains(meta))
                erros[$"{chave}.grupo.metaId"] = ["A meta escolhida não existe."];
        }

        return erros;
    }

    /// <summary>
    /// Transação com <c>pg_advisory_xact_lock</c> por casal, a mesma das escritas de conexões. Se um índice parcial ainda
    /// acusar 23505 (a mesma conta física por outro item, ou outra confirmação em paralelo), vira 409. Só grava se der certo.
    /// </summary>
    private async Task<ResultadoDeNascimento> NaTravaDoCasalAsync(Guid casalId, Func<Task<ResultadoDeNascimento>> acao, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended('integracoes.casal:' || {casalId}::text, 0))", ct);
        try
        {
            var resultado = await acao();
            if (resultado is not ResultadoDeNascimento.Feito)
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
            SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: IndiceDaIdentidade or IndiceDoExterno,
        } pg)
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return new ResultadoDeNascimento.Recusado(pg.ConstraintName == IndiceDaIdentidade ? TituloJaVemDeOutraConexao : TituloTenteDeNovo, 409);
        }
    }
}
