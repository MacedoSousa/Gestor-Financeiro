using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.Modules.Identidade.Dominio;
using GestorFinanceiro.Modules.Identidade.Infra;
using GestorFinanceiro.SharedKernel.Seguranca;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace GestorFinanceiro.Modules.Identidade.Aplicacao;

/// <summary>Resultado do cadastro do casal. Nenhum caso carrega dado pessoal além do código de recuperação.</summary>
internal abstract record ResultadoDoCadastro
{
    private ResultadoDoCadastro() { }

    public sealed record Criado(Guid CasalId, string CodigoRecuperacao) : ResultadoDoCadastro
    {
        public override string ToString() => nameof(Criado);
    }

    public sealed record Invalido(IReadOnlyDictionary<string, string[]> Erros) : ResultadoDoCadastro;

    public sealed record CodigoInstalacaoIncorreto : ResultadoDoCadastro;

    public sealed record JaCadastrado : ResultadoDoCadastro;
}

/// <summary>
/// Cria o casal, os dois cônjuges vinculados e os três envelopes da DEK (senha de cada um e recuperação)
/// numa única transação. Só funciona uma vez por instalação.
/// </summary>
internal sealed partial class CadastroDoCasal(
    IdentidadeDbContext db,
    ICofre cofre,
    IEnvelopesDaDek envelopes,
    ICodigoDeInstalacao instalacao,
    ILogger<CadastroDoCasal> logger)
{
    /// <summary>O código de instalação tem 39 caracteres; acima disso nem é conferido (evita hash de corpo gigante).</summary>
    private const int TamanhoMaximoDoCodigo = 64;

    private const string SqlTrava = "SELECT pg_advisory_xact_lock(hashtextextended('identidade.cadastro', 0))";

    public async Task<bool> EstaDisponivelAsync(CancellationToken ct) => !await db.Casais.AnyAsync(ct);

    public async Task<ResultadoDoCadastro> CadastrarAsync(CadastroDoCasalDto pedido, CancellationToken ct)
    {
        // 0. Com casal cadastrado, a rota deixa de existir, antes de olhar código ou dados.
        if (await db.Casais.AnyAsync(ct)) return new ResultadoDoCadastro.JaCadastrado();

        // 1. Código de instalação antes de qualquer dado pessoal.
        if (pedido.CodigoInstalacao is not { Length: <= TamanhoMaximoDoCodigo } codigo || !instalacao.Confere(codigo))
            return new ResultadoDoCadastro.CodigoInstalacaoIncorreto();

        // 2. Validação com mensagens fixas.
        var erros = ValidadorDeCadastro.Validar(pedido);
        if (erros.Count > 0) return new ResultadoDoCadastro.Invalido(erros);

        Cpf.TentarCriar(pedido.Conjuge1.Cpf, out var cpf1);
        Cpf.TentarCriar(pedido.Conjuge2.Cpf, out var cpf2);

        // 3. Argon2 fora da transação: leva tempo e não deve segurar o lock.
        var codigoRecuperacao = EnvelopeDeChaves.GerarCodigoRecuperacao();
        var envelope1 = envelopes.Embrulhar(pedido.Conjuge1.Senha);
        var envelope2 = envelopes.Embrulhar(pedido.Conjuge2.Senha);
        var envelopeRecuperacao = envelopes.Embrulhar(codigoRecuperacao);

        Guid casalId;
        try
        {
            // 4. Serializa cadastros concorrentes e confere de novo.
            // Daqui em diante, sem o token da requisição: se o cliente desconectar depois que o commit
            // chegou ao banco, um OperationCanceledException viraria 500, o código de instalação não
            // seria consumido e o código de recuperação se perderia.
            var semCancelar = CancellationToken.None;
            await using var transacao = await db.Database.BeginTransactionAsync(semCancelar);
            await db.Database.ExecuteSqlRawAsync(SqlTrava, semCancelar);
            if (await db.Casais.AnyAsync(semCancelar))
            {
                await transacao.RollbackAsync(semCancelar);
                return new ResultadoDoCadastro.JaCadastrado();
            }

            // 5. Casal e usuários sem vínculo (conjuge_id é FK para a própria tabela).
            var casal = Casal.Criar(pedido.NomeFamilia);
            db.Casais.Add(casal);
            await db.SaveChangesAsync(semCancelar);

            var u1 = Usuario.Criar(casal.Id, pedido.Conjuge1.NomeCompleto, cpf1, cofre);
            var u2 = Usuario.Criar(casal.Id, pedido.Conjuge2.NomeCompleto, cpf2, cofre);
            db.Usuarios.AddRange(u1, u2);
            await db.SaveChangesAsync(semCancelar);

            // 6. Vínculo e os três envelopes.
            Usuario.Vincular(u1, u2);
            db.Chaves.AddRange(
                ChaveDoCasal.Criar(casal.Id, TipoChave.Usuario, u1.Id, envelope1.DekCifrada, envelope1.Salt),
                ChaveDoCasal.Criar(casal.Id, TipoChave.Usuario, u2.Id, envelope2.DekCifrada, envelope2.Salt),
                ChaveDoCasal.Criar(casal.Id, TipoChave.Recuperacao, null, envelopeRecuperacao.DekCifrada, envelopeRecuperacao.Salt));
            await db.SaveChangesAsync(semCancelar);
            await transacao.CommitAsync(semCancelar);
            casalId = casal.Id;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // 7. Corrida perdida ou CPF já usado: para quem está de fora, a rota não existe.
            return new ResultadoDoCadastro.JaCadastrado();
        }

        // 6a. O casal já existe e a rota já responde 404; uma falha aqui não desfaz o cadastro.
        try
        {
            instalacao.Consumir();
        }
        catch (Exception e)
        {
            CodigoNaoConsumido(logger, casalId, e.GetType().Name);
        }

        return new ResultadoDoCadastro.Criado(casalId, codigoRecuperacao);
    }

    /// <summary>Só o id do casal e o tipo da exceção: a mensagem pode trazer caminho ou conteúdo do arquivo.</summary>
    [LoggerMessage(Level = LogLevel.Error, Message = "Cadastro {CasalId} concluído, mas o código de instalação não foi consumido ({Tipo}).")]
    private static partial void CodigoNaoConsumido(ILogger logger, Guid casalId, string tipo);
}
