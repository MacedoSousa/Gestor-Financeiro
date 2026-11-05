using GestorFinanceiro.SharedKernel.Dominio;
using GestorFinanceiro.SharedKernel.Seguranca;

namespace GestorFinanceiro.Modules.Identidade.Dominio;

/// <summary>Raiz do agregado: todo dado financeiro pertence a um casal.</summary>
internal sealed class Casal : Entidade
{
    public string NomeFamilia { get; private set; } = string.Empty;

    private Casal() { }

    public static Casal Criar(string nomeFamilia)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nomeFamilia);
        return new Casal { NomeFamilia = nomeFamilia.Trim() };
    }
}

/// <summary>Cônjuge. CPF e telefone ficam cifrados; os HMACs servem para busca e unicidade.</summary>
internal sealed class Usuario : Entidade, ICasalScoped
{
    internal const string Tabela = "identidade.usuarios";
    internal const string PropositoCpf = "cpf";
    internal const string PropositoTelefone = "telefone";

    public Guid CasalId { get; private set; }
    public string NomeCompleto { get; private set; } = string.Empty;
    public byte[] CpfCifrado { get; private set; } = [];
    public byte[] CpfHmac { get; private set; } = [];
    public byte[]? TelefoneCifrado { get; private set; }
    public byte[]? TelefoneHmac { get; private set; }
    public Guid? ConjugeId { get; private set; }
    public int FalhasSeguidas { get; private set; }
    public int Bloqueios { get; private set; }
    public DateTimeOffset? BloqueadoAte { get; private set; }
    public DateTimeOffset? PrimeiroAcessoEncerradoEm { get; private set; }

    public bool PrimeiroAcessoEncerrado => PrimeiroAcessoEncerradoEm is not null;

    private Usuario() { }

    public static Usuario Criar(Guid casalId, string nomeCompleto, Cpf cpf, ICofre cofre)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nomeCompleto);
        var usuario = new Usuario { CasalId = casalId, NomeCompleto = nomeCompleto.Trim() };
        usuario.CpfCifrado = cofre.Cifrar(cpf.Digitos, Cofre.Aad(Tabela, "cpf_cifrado", usuario.Id));
        usuario.CpfHmac = cofre.IndiceCego(cpf.Digitos, PropositoCpf);
        return usuario;
    }

    /// <param name="telefoneE164">Formato +5519999998888.</param>
    public void DefinirTelefone(string telefoneE164, ICofre cofre)
    {
        TelefoneCifrado = cofre.Cifrar(telefoneE164, Cofre.Aad(Tabela, "telefone_cifrado", Id));
        TelefoneHmac = cofre.IndiceCego(telefoneE164, PropositoTelefone);
    }

    /// <summary>Vincula os dois cônjuges um ao outro. Ambos precisam ser do mesmo casal.</summary>
    public static void Vincular(Usuario a, Usuario b)
    {
        if (a.Id == b.Id) throw new InvalidOperationException("Um usuário não pode ser cônjuge de si mesmo.");
        if (a.CasalId != b.CasalId) throw new InvalidOperationException("Cônjuges precisam pertencer ao mesmo casal.");
        a.ConjugeId = b.Id;
        b.ConjugeId = a.Id;
    }

    public bool EstaBloqueado(DateTimeOffset agora) => BloqueadoAte is { } ate && agora < ate;

    /// <summary>Na 5ª falha seguida bloqueia por 15 min x 2^bloqueios (teto 24 h) e zera as falhas.</summary>
    public void RegistrarFalha(DateTimeOffset agora)
    {
        FalhasSeguidas++;
        if (FalhasSeguidas < PoliticaDeBloqueio.FalhasParaBloquear) return;
        BloqueadoAte = agora + PoliticaDeBloqueio.DuracaoDoBloqueio(Bloqueios);
        Bloqueios++;
        FalhasSeguidas = 0;
    }

    public void RegistrarSucesso()
    {
        FalhasSeguidas = 0;
        Bloqueios = 0;
        BloqueadoAte = null;
    }

    /// <summary>Destrava na hora (pelo outro cônjuge); a contagem de bloqueios continua.</summary>
    public void Destravar()
    {
        FalhasSeguidas = 0;
        BloqueadoAte = null;
    }

    /// <summary>Marca o fim do primeiro acesso por senha; só grava se ainda estiver nulo.</summary>
    public void EncerrarPrimeiroAcesso(DateTimeOffset agora) => PrimeiroAcessoEncerradoEm ??= agora;

    public string LerCpf(ICofre cofre) => cofre.Decifrar(CpfCifrado, Cofre.Aad(Tabela, "cpf_cifrado", Id));
}

/// <summary>Um envelope da DEK do casal (senha de um cônjuge, recuperação ou serviço).</summary>
internal sealed class ChaveDoCasal : Entidade, ICasalScoped
{
    public Guid CasalId { get; private set; }
    public TipoChave Tipo { get; private set; }
    public Guid? UsuarioId { get; private set; }
    public byte[] DekCifrada { get; private set; } = [];
    public byte[]? Salt { get; private set; }
    public int Versao { get; private set; } = 1;

    private ChaveDoCasal() { }

    public static ChaveDoCasal Criar(Guid casalId, TipoChave tipo, Guid? usuarioId, byte[] dekCifrada, byte[]? salt) =>
        new() { CasalId = casalId, Tipo = tipo, UsuarioId = usuarioId, DekCifrada = dekCifrada, Salt = salt };
}

internal enum TipoChave
{
    Usuario,
    Recuperacao,
    Servico,
}
