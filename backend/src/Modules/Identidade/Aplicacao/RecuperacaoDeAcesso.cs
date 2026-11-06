using GestorFinanceiro.Modules.Identidade.Dominio;
using GestorFinanceiro.Modules.Identidade.Infra;
using GestorFinanceiro.SharedKernel.Persistencia;
using GestorFinanceiro.SharedKernel.Seguranca;
using Microsoft.EntityFrameworkCore;

namespace GestorFinanceiro.Modules.Identidade.Aplicacao;

internal abstract record ResultadoDaRecuperacao
{
    private ResultadoDaRecuperacao() { }

    /// <param name="Token">Token da autorização de registro.</param>
    /// <param name="NovoCodigo">Código de recuperação novo, no formato exibido; mostrado uma única vez.</param>
    public sealed record Autorizado(string Token, string NovoCodigo) : ResultadoDaRecuperacao
    {
        public override string ToString() => nameof(Autorizado);
    }

    public sealed record Falhou : ResultadoDaRecuperacao;

    public sealed record Bloqueado : ResultadoDaRecuperacao;
}

/// <summary>
/// CPF + código de recuperação dão a autorização de registro de um dispositivo. Usar o código o gira na hora:
/// na mesma transação (com o lock do cônjuge), o envelope antigo é removido, o novo é gravado e o sucesso é registrado.
/// </summary>
internal sealed class RecuperacaoDeAcesso(
    IdentidadeDbContext db,
    ICofre cofre,
    TentativaDeLogin tentativa,
    RotacaoDoCodigoDeRecuperacao rotacao,
    AutorizacoesDeRegistro autorizacoes,
    TimeProvider relogio)
{
    /// <summary>O código tem 39 caracteres; acima disso nem chega ao Argon2.</summary>
    private const int TamanhoMaximoDoCodigo = 128;

    public async Task<ResultadoDaRecuperacao> ExecutarAsync(string cpf, string codigo, CancellationToken ct)
    {
        if (codigo is null or { Length: > TamanhoMaximoDoCodigo }) codigo = string.Empty;
        var usuario = await BuscarAsync(cpf, ct);

        // CPF desconhecido: mesmo custo (um Argon2) e mesma resposta de um código errado.
        if (usuario is null)
        {
            ConferenciaDeSegredo.IgualarTempo(EnvelopeDeChaves.CanonizarCodigo(codigo));
            return new ResultadoDaRecuperacao.Falhou();
        }

        // Olhada barata antes do Argon2; a decisão que vale é refeita com o lock em TentativaDeLogin.
        if (usuario.EstaBloqueado(relogio.GetUtcNow())) return new ResultadoDaRecuperacao.Bloqueado();

        var usuarioId = usuario.Id;
        var casalId = usuario.CasalId;
        string? novoCodigo = null;
        var resultado = await tentativa.ExecutarAsync(
            usuarioId,
            async (_, _) =>
            {
                // O envelope é do casal: o lock da recuperação impede que os dois cônjuges girem ao mesmo tempo.
                await rotacao.TravarAsync(casalId);
                var envelope = await rotacao.EnvelopeAtivoAsync(casalId);
                if (envelope is null || !ConferenciaDeSegredo.CodigoAbreEnvelope(envelope, codigo)) return false;
                novoCodigo = await rotacao.GirarAsync(casalId);
                return true;
            },
            ct: ct);

        return resultado switch
        {
            ResultadoDaTentativa.Sucesso => new ResultadoDaRecuperacao.Autorizado(
                autorizacoes.Emitir(usuarioId, casalId, OrigemDoRegistro.CodigoDeRecuperacao), novoCodigo!),
            ResultadoDaTentativa.Bloqueado => new ResultadoDaRecuperacao.Bloqueado(),
            _ => new ResultadoDaRecuperacao.Falhou(),
        };
    }

    private async Task<Usuario?> BuscarAsync(string cpf, CancellationToken ct)
    {
        if (!Cpf.TentarCriar(cpf, out var valido)) return null;
        var hmac = cofre.IndiceCego(valido.Digitos, Usuario.PropositoCpf);

        // O casal ainda não é conhecido: só o filtro de soft delete continua ativo.
        return await db.Usuarios
            .IgnoreQueryFilters([Filtros.Casal])
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.CpfHmac == hmac, ct);
    }
}
