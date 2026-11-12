using GestorFinanceiro.Modules.Contas.Contracts;
using GestorFinanceiro.Modules.Contas.Dominio;
using GestorFinanceiro.Modules.Contas.Infra;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.SharedKernel.Aplicacao;
using GestorFinanceiro.SharedKernel.Dominio;
using GestorFinanceiro.SharedKernel.Seguranca;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GestorFinanceiro.Modules.Contas.Aplicacao;

internal abstract record ResultadoDeConta
{
    public sealed record Ok(Guid Id) : ResultadoDeConta;
    public sealed record Invalido(IReadOnlyDictionary<string, string[]> Erros) : ResultadoDeConta;
    public sealed record NaoEncontrada : ResultadoDeConta;
    public sealed record ApelidoRepetido : ResultadoDeConta;
    public sealed record EmUso : ResultadoDeConta;
    public sealed record SaldoDoBanco : ResultadoDeConta;
    public sealed record Ligada : ResultadoDeConta;
}

/// <summary>Casos de uso de contas e cartões. O casal e o autor vêm sempre da sessão.</summary>
internal sealed class ServicoDeContas(
    ContasDbContext db,
    IUsuarioAtual atual,
    ICofre cofre,
    IConsultaDeUsuarios usuarios,
    TimeProvider relogio,
    IEnumerable<IVerificadorDeUsoDaConta> verificadores)
{
    internal const string TituloApelidoRepetido = "Já existe uma conta com esse nome.";
    internal const string TituloEmUso = "Conta com lançamentos: arquive em vez de apagar.";
    internal const string TituloSaldoDoBanco = "Esta conta vem do banco. O saldo inicial é calculado.";
    internal const string TituloLigada = "Esta conta vem do banco. Arquive para parar a sincronização.";
    private const string IndiceDeApelido = "ux_contas_apelido";

    public async Task<IReadOnlyList<ContaDto>> ListarAsync(CancellationToken ct)
    {
        var contas = await db.Contas.AsNoTracking().OrderBy(c => c.Apelido).ToListAsync(ct);
        var apelidos = await ApelidosDoCasalAsync(ct);
        return contas.Select(c => ParaDto(c, apelidos)).ToList();
    }

    public async Task<ResultadoDeConta> CriarAsync(NovaContaDto pedido, CancellationToken ct)
    {
        if (atual.CasalId is not { } casalId) return new ResultadoDeConta.NaoEncontrada();
        var erros = await ValidarAsync(pedido, casalId, tipoAtual: null, ct);
        if (erros.Count > 0) return new ResultadoDeConta.Invalido(erros);

        var conta = Conta.Criar(casalId, TentarTipo(pedido.Tipo, out var tipo) ? tipo : throw new InvalidOperationException("Tipo validado antes."), pedido.Apelido.Trim());
        Aplicar(conta, pedido, arquivada: false);
        conta.DefinirAgencia(pedido.Agencia, cofre);
        conta.DefinirNumero(pedido.Numero, cofre);
        db.Contas.Add(conta);
        return await SalvarAsync(conta.Id, ct);
    }

    public async Task<ResultadoDeConta> AtualizarAsync(Guid id, NovaContaDto pedido, CancellationToken ct)
    {
        if (atual.CasalId is not { } casalId) return new ResultadoDeConta.NaoEncontrada();
        var conta = await db.Contas.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (conta is null) return new ResultadoDeConta.NaoEncontrada();

        var erros = await ValidarAsync(pedido, casalId, conta.Tipo, ct);
        if (erros.Count > 0) return new ResultadoDeConta.Invalido(erros);

        // D3: em conta ligada o saldo inicial é calculado pela importação; apelido, dias, limite e arquivar continuam livres.
        if (conta.Ligada && (pedido.SaldoInicialCentavos != conta.SaldoInicialCentavos || pedido.SaldoInicialEm != conta.SaldoInicialEm))
            return new ResultadoDeConta.SaldoDoBanco();

        Aplicar(conta, pedido, pedido.Arquivada ?? conta.Arquivada);
        if (pedido.Agencia is not null) conta.DefinirAgencia(pedido.Agencia, cofre);
        if (pedido.Numero is not null) conta.DefinirNumero(pedido.Numero, cofre);
        return await SalvarAsync(conta.Id, ct);
    }

    /// <summary>
    /// Só apaga conta sem uso. A trava da conta (<see cref="ChaveDeTrava.Conta"/>, a mesma que criar e editar lançamento
    /// tomam) vai até o commit: um lançamento nesta conta ou já está gravado quando os verificadores olham, ou espera e
    /// encontra a conta apagada.
    /// </summary>
    public async Task<ResultadoDeConta> ApagarAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({ChaveDeTrava.Conta(id)}, 0))", ct);

        var conta = await db.Contas.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (conta is null) return new ResultadoDeConta.NaoEncontrada();
        if (conta.Ligada) return new ResultadoDeConta.Ligada(); // decisão 7: arquivar para a sincronização; apagar, nunca.

        foreach (var verificador in verificadores)
            if (await verificador.EstaEmUsoAsync(id, ct))
                return new ResultadoDeConta.EmUso();

        db.Contas.Remove(conta); // SoftDeleteInterceptor converte em is_deleted = true.
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new ResultadoDeConta.Ok(id);
    }

    private static void Aplicar(Conta conta, NovaContaDto p, bool arquivada) =>
        conta.Definir(
            p.Apelido.Trim(),
            string.IsNullOrWhiteSpace(p.Instituicao) ? null : p.Instituicao.Trim(),
            p.TitularUsuarioId,
            new Dinheiro(p.SaldoInicialCentavos),
            p.SaldoInicialEm,
            p.DiaFechamento,
            p.DiaVencimento,
            p.LimiteCentavos is { } limite ? new Dinheiro(limite) : null,
            arquivada);

    private async Task<ResultadoDeConta> SalvarAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return new ResultadoDeConta.Ok(id);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: IndiceDeApelido })
        {
            return new ResultadoDeConta.ApelidoRepetido();
        }
    }

    internal async Task<Dictionary<string, string[]>> ValidarAsync(NovaContaDto p, Guid casalId, TipoDeConta? tipoAtual, CancellationToken ct)
    {
        var erros = new Dictionary<string, string[]>();

        if (!TentarTipo(p.Tipo, out var tipo))
            erros["tipo"] = ["Tipo inválido: use CORRENTE, POUPANCA, INVESTIMENTO, CARTAO ou DINHEIRO."];
        else if (tipoAtual is { } atualTipo && atualTipo != tipo)
            erros["tipo"] = ["O tipo da conta não pode mudar."];

        var apelido = p.Apelido?.Trim() ?? "";
        if (apelido.Length is < 1 or > 60) erros["apelido"] = ["O apelido deve ter de 1 a 60 caracteres."];
        if (p.Instituicao is { } inst && inst.Trim().Length > 60) erros["instituicao"] = ["A instituição deve ter até 60 caracteres."];

        if (erros.ContainsKey("tipo") is false)
        {
            var ehCartao = tipo == TipoDeConta.Cartao;
            var diasOk = p.DiaFechamento is >= 1 and <= 31 && p.DiaVencimento is >= 1 and <= 31;
            if (ehCartao && !diasOk)
                erros["diaFechamento"] = ["Cartão precisa dos dias de fechamento e de vencimento (1 a 31)."];
            else if (!ehCartao && (p.DiaFechamento is not null || p.DiaVencimento is not null))
                erros["diaFechamento"] = ["Só cartão tem dia de fechamento e de vencimento."];
        }

        if (p.SaldoInicialEm == default)
            erros["saldoInicialEm"] = ["Informe a data do saldo inicial."];
        else if (p.SaldoInicialEm > Hoje())
            erros["saldoInicialEm"] = ["A data do saldo inicial não pode ser no futuro."];

        if (p.LimiteCentavos is < 0) erros["limiteCentavos"] = ["O limite não pode ser negativo."];

        if (p.TitularUsuarioId is { } titular)
        {
            var membros = await usuarios.ListarDoCasalAsync(casalId, ct);
            if (membros.All(m => m.UsuarioId != titular))
                erros["titularUsuarioId"] = ["O titular precisa ser um dos cônjuges do casal."];
        }

        return erros;
    }

    private DateOnly Hoje()
    {
        var fuso = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(relogio.GetUtcNow(), fuso).DateTime);
    }

    /// <summary>Só os nomes do enum (sem diferenciar maiúsculas); "2" não vale.</summary>
    internal static bool TentarTipo(string? texto, out TipoDeConta tipo)
    {
        tipo = default;
        var nome = Enum.GetNames<TipoDeConta>().FirstOrDefault(n => string.Equals(n, texto?.Trim(), StringComparison.OrdinalIgnoreCase));
        return nome is not null && Enum.TryParse(nome, out tipo);
    }

    private async Task<Dictionary<Guid, string>> ApelidosDoCasalAsync(CancellationToken ct) =>
        atual.CasalId is { } casalId
            ? (await usuarios.ListarDoCasalAsync(casalId, ct)).ToDictionary(u => u.UsuarioId, u => u.Apelido)
            : [];

    private ContaDto ParaDto(Conta c, Dictionary<Guid, string> apelidos) => new(
        c.Id,
        c.Tipo.ToString().ToUpperInvariant(),
        c.Apelido,
        c.Instituicao,
        c.TitularUsuarioId is { } t && apelidos.TryGetValue(t, out var nome) ? new TitularDto(t, nome) : null,
        c.AgenciaMascarada,
        c.MascararNumero(cofre),
        c.SaldoInicialCentavos,
        c.SaldoInicialEm,
        c.DiaFechamento,
        c.DiaVencimento,
        c.LimiteCentavos,
        c.Arquivada,
        c.Ligada);
}
