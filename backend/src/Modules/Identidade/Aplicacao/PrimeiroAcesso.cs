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

        // 2. Bloqueado: não testa a senha e não conta outra falha.
        var agora = relogio.GetUtcNow();
        if (usuario.EstaBloqueado(agora)) return new ResultadoDoPrimeiroAcesso.Bloqueado();

        // 3. Primeiro acesso encerrado: nunca mais por senha.
        if (usuario.PrimeiroAcessoEncerrado) return new ResultadoDoPrimeiroAcesso.JaConfigurado();

        // 4. Senha: abre o envelope USUARIO desse cônjuge.
        var envelope = await db.Chaves
            .IgnoreQueryFilters([Filtros.Casal])
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.UsuarioId == usuario.Id && c.Tipo == TipoChave.Usuario, ct);

        if (envelope is null || !ConferenciaDeSegredo.SenhaAbreEnvelope(envelope, senha))
        {
            usuario.RegistrarFalha(agora);
            await db.SaveChangesAsync(ct);
            return new ResultadoDoPrimeiroAcesso.Falhou();
        }

        // 5. Certa: zera as falhas e emite a autorização.
        usuario.RegistrarSucesso();
        await db.SaveChangesAsync(ct);
        return new ResultadoDoPrimeiroAcesso.Autorizado(autorizacoes.Emitir(usuario.Id, usuario.CasalId, OrigemDoRegistro.PrimeiroAcesso));
    }

    private async Task<Usuario?> BuscarAsync(string cpf, CancellationToken ct)
    {
        if (!Cpf.TentarCriar(cpf, out var valido)) return null;
        var hmac = cofre.IndiceCego(valido.Digitos, Usuario.PropositoCpf);

        // O casal ainda não é conhecido: só o filtro de soft delete continua ativo.
        return await db.Usuarios
            .IgnoreQueryFilters([Filtros.Casal])
            .FirstOrDefaultAsync(u => u.CpfHmac == hmac, ct);
    }
}
