using GestorFinanceiro.SharedKernel.Dominio;
using GestorFinanceiro.SharedKernel.Seguranca;

namespace GestorFinanceiro.Modules.Contas.Dominio;

internal enum TipoDeConta
{
    Corrente,
    Poupanca,
    Investimento,
    Cartao,
    Dinheiro,
}

/// <summary>Conta bancária, dinheiro ou cartão. Agência e número só existem cifrados.</summary>
internal sealed class Conta : Entidade, ICasalScoped
{
    internal const string Tabela = "contas.contas";
    internal const string ColunaAgencia = "agencia_cifrada";
    internal const string ColunaNumero = "numero_cifrado";

    public Guid CasalId { get; private set; }
    public TipoDeConta Tipo { get; private set; }
    public string Apelido { get; private set; } = string.Empty;
    public string? Instituicao { get; private set; }
    /// <summary>Nulo = conta conjunta.</summary>
    public Guid? TitularUsuarioId { get; private set; }
    public byte[]? AgenciaCifrada { get; private set; }
    public byte[]? NumeroCifrado { get; private set; }
    public long SaldoInicialCentavos { get; private set; }
    public DateOnly SaldoInicialEm { get; private set; }
    public int? DiaFechamento { get; private set; }
    public int? DiaVencimento { get; private set; }
    public long? LimiteCentavos { get; private set; }
    public bool Arquivada { get; private set; }

    public Dinheiro SaldoInicial => new(SaldoInicialCentavos);

    private Conta() { }

    public static Conta Criar(Guid casalId, TipoDeConta tipo, string apelido) =>
        new() { CasalId = casalId, Tipo = tipo, Apelido = apelido };

    /// <summary>Dados de cadastro (tudo menos agência e número, que têm métodos próprios).</summary>
    public void Definir(string apelido, string? instituicao, Guid? titularUsuarioId, Dinheiro saldoInicial, DateOnly saldoInicialEm,
        int? diaFechamento, int? diaVencimento, Dinheiro? limite, bool arquivada)
    {
        Apelido = apelido;
        Instituicao = instituicao;
        TitularUsuarioId = titularUsuarioId;
        SaldoInicialCentavos = saldoInicial.Centavos;
        SaldoInicialEm = saldoInicialEm;
        DiaFechamento = diaFechamento;
        DiaVencimento = diaVencimento;
        LimiteCentavos = limite?.Centavos;
        Arquivada = arquivada;
    }

    /// <summary>Texto vazio ou nulo limpa o campo.</summary>
    public void DefinirAgencia(string? agencia, ICofre cofre) =>
        AgenciaCifrada = string.IsNullOrWhiteSpace(agencia) ? null : cofre.Cifrar(agencia.Trim(), Cofre.Aad(Tabela, ColunaAgencia, Id));

    public void DefinirNumero(string? numero, ICofre cofre) =>
        NumeroCifrado = string.IsNullOrWhiteSpace(numero) ? null : cofre.Cifrar(numero.Trim(), Cofre.Aad(Tabela, ColunaNumero, Id));

    /// <summary>"••••" quando há agência; nunca devolve o valor.</summary>
    public string? AgenciaMascarada => AgenciaCifrada is null ? null : "••••";

    /// <summary>"••••-7" (só o último dígito); nulo quando não há número.</summary>
    public string? MascararNumero(ICofre cofre)
    {
        if (NumeroCifrado is null) return null;
        var numero = cofre.Decifrar(NumeroCifrado, Cofre.Aad(Tabela, ColunaNumero, Id));
        var ultimo = numero.LastOrDefault(char.IsLetterOrDigit);
        return ultimo == default ? "••••" : $"••••-{ultimo}";
    }
}
