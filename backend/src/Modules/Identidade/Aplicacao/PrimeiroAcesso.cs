using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.Modules.Identidade.Dominio;
using GestorFinanceiro.Modules.Identidade.Infra;
using GestorFinanceiro.SharedKernel.Persistencia;
using GestorFinanceiro.SharedKernel.Seguranca;
using Microsoft.EntityFrameworkCore;

namespace GestorFinanceiro.Modules.Identidade.Aplicacao;

internal abstract record ResultadoDoPrimeiroAcesso
{
    private ResultadoDoPrimeiroAcesso() { }

    public sealed record Autorizado(string Token) : ResultadoDoPrimeiroAcesso
    {
        public override string ToString() => nameof(Autorizado);
    }

    public sealed record Falhou : ResultadoDoPrimeiroAcesso;

    public sealed record Bloqueado : ResultadoDoPrimeiroAcesso;

    public sealed record JaConfigurado : ResultadoDoPrimeiroAcesso;
}

/// <summary>Primeiro acesso por CPF e senha do cadastro. Só vale até o cônjuge confirmar o primeiro dispositivo.</summary>
internal sealed class PrimeiroAcesso(
    IdentidadeDbContext db,
    ICofre cofre,
    TentativaDeLogin tentativa,
    AutorizacoesDeRegistro autorizacoes,
    TimeProvider relogio)
{
    /// <summary>Senhas e CPFs gigantes nem chegam ao Argon2.</summary>
    private const int TamanhoMaximoDaSenha = 256;

    public async Task<ResultadoDoPrimeiroAcesso> ExecutarAsync(string cpf, string senha, CancellationToken ct)
    {
        if (senha is null or { Length: > TamanhoMaximoDaSenha }) senha = string.Empty;
        var usuario = await BuscarAsync(cpf, ct);

        // 1. CPF desconhecido: mesmo custo e mesma resposta de uma senha errada.
        if (usuario is null)
        {
            ConferenciaDeSegredo.IgualarTempo(senha);
            return new ResultadoDoPrimeiroAcesso.Falhou();
        }

        // 2. Bloqueado (423), depois encerrado (403), depois senha. Antes do Argon2 é só uma olhada barata;
        // a decisão que vale é refeita dentro de TentativaDeLogin, com o lock e o usuário recarregado.
        var agora = relogio.GetUtcNow();
        if (usuario.EstaBloqueado(agora)) return new ResultadoDoPrimeiroAcesso.Bloqueado();
        if (usuario.PrimeiroAcessoEncerrado) return new ResultadoDoPrimeiroAcesso.JaConfigurado();

        var usuarioId = usuario.Id;
        var casalId = usuario.CasalId;
        var resultado = await tentativa.ExecutarAsync(
            usuarioId,
            async (_, token) =>
            {
                // Envelope USUARIO desse cônjuge.
                var envelope = await db.Chaves
                    .IgnoreQueryFilters([Filtros.Casal])
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.UsuarioId == usuarioId && c.Tipo == TipoChave.Usuario, token);
                return envelope is not null && ConferenciaDeSegredo.SenhaAbreEnvelope(envelope, senha);
            },
            u => u.PrimeiroAcessoEncerrado,
            ct);

        return resultado switch
        {
            ResultadoDaTentativa.Sucesso => new ResultadoDoPrimeiroAcesso.Autorizado(
                autorizacoes.Emitir(usuarioId, casalId, OrigemDoRegistro.PrimeiroAcesso)),
            ResultadoDaTentativa.Bloqueado => new ResultadoDoPrimeiroAcesso.Bloqueado(),
            ResultadoDaTentativa.Impedido => new ResultadoDoPrimeiroAcesso.JaConfigurado(),
            _ => new ResultadoDoPrimeiroAcesso.Falhou(),
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
