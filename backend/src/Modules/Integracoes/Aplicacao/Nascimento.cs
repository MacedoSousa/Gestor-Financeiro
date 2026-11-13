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
        /// <param name="Vinculos">Os vínculos que apontam para a conta: a troca de id (IdEmUso) mexe só neles.</param>
        /// <param name="Linha">Posição da conta no pedido: o 409 de apelido aponta a linha, nunca o id da Pluggy (D18).</param>
        public sealed record CriarConta(int Linha, NovaContaLigada Pedido, IReadOnlyList<Guid> Vinculos) : Tarefa;
        public sealed record MarcarLigada(Guid ContaId) : Tarefa;
        public sealed record CriarGrupo(string Nome, NovaContaLigada Pedido, Guid? MetaId, IReadOnlyList<Guid> Vinculos) : Tarefa;
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

        foreach (var (confirmada, linha) in contas.Select((c, i) => (c, i)))
        {
            var conta = doBanco[confirmada.IdExterno!];
            var tipo = Vinculo.TipoDe(conta);
            var identidade = Vinculo.IdentidadeDe(conta, a.CodigoBanco, cofre);
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
                db.Vinculos.Add(vinculo = Vinculo.Criar(casal, a.Item.Id, tipo, conta.Id, destino, identidade));
            else if (vinculo.ContaId is null)
                vinculo.DefinirConta(destino);
            else if (vinculo.ContaId != destino)
            {
                // A conta do vínculo existe no casal: ele já está ligado a ela (ou um LIGAR ficou pela metade); não muda de
                // alvo. Se ela nunca chegou a existir, o LIGAR pode escolher outra.
                if (doCasal.ContainsKey(vinculo.ContaId.Value) || !ligar) return new ResultadoDeNascimento.Recusado(TituloJaTemConta, 409);
                vinculo.TrocarConta(vinculo.ContaId, destino);
            }
            else if (!ligar && doCasal.TryGetValue(destino, out var atual) && !atual.Ligada)
            {
                // CRIAR sobre um LIGAR pela metade: a pessoa escolhe "Ligar" de novo, explicitamente.
                return new ResultadoDeNascimento.Recusado(TituloJaTemConta, 409);
            }

            tarefas.Add(ligar
                ? new Tarefa.MarcarLigada(destino)
                : new Tarefa.CriarConta(linha, NovaConta(a, conta, confirmada, destino), [vinculo.Id]));
        }

        return GravarAplicacoes(a, leitura, aplicacoes, vinculos, doCasal, tarefas) ?? new ResultadoDeNascimento.Feito();
    }

    /// <summary>
    /// Cada grupo distinto (nome sem caixa e sem espaços em volta) é uma conta INVESTIMENTO; as aplicações do grupo apontam
    /// para ela. Saldo inicial = soma das aplicações, no dia do nascimento (São Paulo). Sem grupo, a aplicação aguarda.
    /// Aplicação já nascida (conta existente no casal) não troca de grupo. Um grupo que não chegou a nascer reaproveita o id
    /// só se os vínculos com ele forem exatamente os do grupo; senão o grupo ganha id novo, e quem ficou com o id antigo
    /// (fora do pedido ou sem grupo) volta a aguardar.
    /// </summary>
    private ResultadoDeNascimento? GravarAplicacoes(AcessoAoItem a, LeituraDoBanco leitura, IReadOnlyList<AplicacaoConfirmadaDto> aplicacoes,
        List<Vinculo> vinculos, IReadOnlyDictionary<Guid, ContaResumo> doCasal, List<Tarefa> tarefas)
    {
        var doBanco = leitura.Aplicacoes.ToDictionary(ap => ap.Id, StringComparer.Ordinal);
        var deInvestimento = vinculos.Where(v => v.Tipo == TipoDeVinculo.Investimento).ToList();
        Vinculo? Existente(string id) => deInvestimento.FirstOrDefault(v => v.IdExterno == id);
        bool Pendente(Guid? contaId) => contaId is { } id && !doCasal.ContainsKey(id);
        var usados = new HashSet<Guid>();

        foreach (var grupo in aplicacoes.Where(ap => ap.Grupo is not null).GroupBy(ap => ap.Grupo!.Nome!.Trim().ToUpperInvariant()))
        {
            var membros = grupo.ToList();
            var nome = membros[0].Grupo!.Nome!.Trim();
            var ids = membros.Select(m => m.IdExterno!).ToHashSet(StringComparer.Ordinal);
            var atuais = membros.Select(m => Existente(m.IdExterno!)?.ContaId).ToList();

            var nascidas = atuais.OfType<Guid>().Where(doCasal.ContainsKey).Distinct().ToList();
            if (nascidas.Count > 1 || (nascidas.Count == 1 && !string.Equals(doCasal[nascidas[0]].Apelido, nome, StringComparison.OrdinalIgnoreCase)))
                return new ResultadoDeNascimento.Recusado(TituloOutroGrupo, 409);

            var pendentes = atuais.Where(Pendente).OfType<Guid>().Distinct().ToList();
            var contaId = nascidas.Count == 1 ? nascidas[0]
                : pendentes.Count == 1 && atuais.All(c => c == pendentes[0]) && deInvestimento.Where(v => v.ContaId == pendentes[0]).All(v => ids.Contains(v.IdExterno))
                    ? pendentes[0]
                    : Guid.CreateVersion7();
            usados.Add(contaId);

            var doGrupo = new List<Guid>();
            foreach (var m in membros)
            {
                var vinculo = Existente(m.IdExterno!);
                if (vinculo is null)
                {
                    vinculo = Vinculo.Criar(a.Item.CasalId, a.Item.Id, TipoDeVinculo.Investimento, m.IdExterno!, contaId, null);
                    db.Vinculos.Add(vinculo);
                    deInvestimento.Add(vinculo);
                }
                else if (vinculo.ContaId is null) vinculo.DefinirConta(contaId);
                else if (vinculo.ContaId != contaId) vinculo.TrocarConta(vinculo.ContaId, contaId); // só pendente chega aqui
                doGrupo.Add(vinculo.Id);
            }

            // Aplicação sem valor no banco (D21, item 4) entra com 0: o grupo nasce, e a conferência mostra quando o valor vier.
            var saldo = membros.Sum(m => doBanco[m.IdExterno!].SaldoCentavos ?? 0);
            var pedido = new NovaContaLigada(contaId, "INVESTIMENTO", nome, a.Item.Banco, a.Conexao.UsuarioId, saldo,
                ConversaoPluggy.DiaDeSaoPaulo(relogio.GetUtcNow()), null, null, null);
            tarefas.Add(new Tarefa.CriarGrupo(nome, pedido, membros.Select(m => m.Grupo!.MetaId).FirstOrDefault(id => id is not null), doGrupo));
        }

        foreach (var semGrupo in aplicacoes.Where(ap => ap.Grupo is null))
        {
            var vinculo = Existente(semGrupo.IdExterno!);
            if (vinculo is null)
                db.Vinculos.Add(Vinculo.Criar(a.Item.CasalId, a.Item.Id, TipoDeVinculo.Investimento, semGrupo.IdExterno!, null, null));
            else if (Pendente(vinculo.ContaId))
                vinculo.TrocarConta(vinculo.ContaId, null);
        }

        // Grupo que não nasceu e foi desfeito: quem ainda aponta para ele volta a aguardar.
        foreach (var abandonado in deInvestimento.Where(v => Pendente(v.ContaId) && !usados.Contains(v.ContaId!.Value)))
            abandonado.TrocarConta(abandonado.ContaId, null);
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
                return (await CriarAsync(t.Pedido, t.Vinculos, t.Linha, ct)).Recusa;
            case Tarefa.MarcarLigada t:
                return await contasLigadas.MarcarLigadaAsync(t.ContaId, ct) ? null : new ResultadoDeNascimento.Recusado(TituloContaSumiu, 409);
            case Tarefa.CriarGrupo t:
                var (recusa, contaId) = await CriarAsync(t.Pedido, t.Vinculos, null, ct);
                if (recusa is not null) return recusa;
                if (t.MetaId is { } meta && !await metas.LigarContaAMetaAsync(contaId, meta, ct))
                    return new ResultadoDeNascimento.Recusado(TituloMeta(t.Nome), 409);
                return null;
            default:
                throw new InvalidOperationException(tarefa.GetType().Name);
        }
    }

    /// <summary>
    /// Cria a conta pelo contrato e devolve a recusa (ou nula) e o id com que a conta ficou. Se o id ficou indisponível (a
    /// conta dele foi apagada, por exemplo), na trava do casal recarrega os vínculos desta tarefa: se ainda apontam para o id
    /// antigo, troca por um novo; se outra confirmação já trocou, usa o id dela (sem criar outra conta). Nunca cria conta
    /// que nenhum vínculo aponta.
    /// </summary>
    private async Task<(ResultadoDeNascimento? Recusa, Guid ContaId)> CriarAsync(NovaContaLigada pedido, IReadOnlyList<Guid> vinculoIds,
        int? linha, CancellationToken ct)
    {
        var resultado = await contasLigadas.CriarAsync(pedido, ct);
        if (resultado is not ResultadoDaContaLigada.IdEmUso) return (Traduzir(resultado, linha), pedido.ContaId);

        var casal = atual.CasalId ?? throw new InvalidOperationException("Nascimento sem casal.");
        Guid? alvo = null;
        var troca = await NaTravaDoCasalAsync(casal, async () =>
        {
            // Os vínculos da fase 1 ainda estão rastreados com o valor antigo: relê do banco, já dentro da trava.
            db.ChangeTracker.Clear();
            var deles = await db.Vinculos.Where(v => vinculoIds.Contains(v.Id)).ToListAsync(ct);
            var atuais = deles.Select(v => v.ContaId).Distinct().ToList();
            if (deles.Count == 0 || atuais.Count != 1 || atuais[0] is not { } contaAtual)
                return new ResultadoDeNascimento.Recusado(TituloTenteDeNovo, 409);
            if (contaAtual == pedido.ContaId)
            {
                var novo = Guid.CreateVersion7();
                foreach (var v in deles) v.TrocarConta(pedido.ContaId, novo);
                alvo = novo;
            }
            else alvo = contaAtual;
            return new ResultadoDeNascimento.Feito();
        }, ct);
        if (troca is not ResultadoDeNascimento.Feito || alvo is not { } id) return (troca, pedido.ContaId);

        resultado = await contasLigadas.CriarAsync(pedido with { ContaId = id }, ct);
        return resultado is ResultadoDaContaLigada.IdEmUso
            ? (new ResultadoDeNascimento.Recusado(TituloTenteDeNovo, 409), id)
            : (Traduzir(resultado, linha), id);
    }

    private static ResultadoDeNascimento? Traduzir(ResultadoDaContaLigada resultado, int? linha) => resultado switch
    {
        ResultadoDaContaLigada.Pronta => null,
        ResultadoDaContaLigada.ApelidoRepetido r => new ResultadoDeNascimento.Recusado(TituloApelidoRepetido, 409,
            new Dictionary<string, object?> { ["sugestao"] = r.Sugestao, ["linha"] = linha }),
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
