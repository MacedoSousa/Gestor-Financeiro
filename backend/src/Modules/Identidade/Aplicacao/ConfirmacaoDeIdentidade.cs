using GestorFinanceiro.Modules.Identidade.Contracts;

namespace GestorFinanceiro.Modules.Identidade.Aplicacao;

internal enum ResultadoDaConfirmacao
{
    Confirmado,
    Falhou,
    Bloqueado,
    /// <summary>Corpo sem código TOTP nem passkey, ou com os dois.</summary>
    Invalido,
}

/// <summary>
/// Confirmação recente (<c>POST /auth/confirmar</c>): o usuário da sessão prova de novo que é ele, com o TOTP ou
/// uma passkey dele. Toda conferência passa por <see cref="TentativaDeLogin"/>. Por TOTP, erro conta falha e conta
/// bloqueada é 423; por passkey, o bloqueio não se aplica (ADR-011).
/// </summary>
internal sealed class ConfirmacaoDeIdentidade(
    TentativaDeLogin tentativa,
    ServicoDeTotp totp,
    ServicoDePasskeys passkeys,
    DesafiosWebAuthn desafios,
    ServicoDeSessoes sessoes,
    TimeProvider relogio)
{
    public async Task<ResultadoDaConfirmacao> ConfirmarAsync(Guid usuarioId, Guid sessaoId, ConfirmacaoDeIdentidadeDto pedido, CancellationToken ct)
    {
        var temTotp = pedido.CodigoTotp is not null;
        var temPasskey = pedido.Passkey is not null;
        if (temTotp == temPasskey) return ResultadoDaConfirmacao.Invalido;

        var agora = relogio.GetUtcNow();
        ResultadoDaTentativa resultado;
        if (temTotp)
        {
            resultado = await tentativa.ExecutarAsync(usuarioId, (_, _) => totp.ConferirCodigoAsync(usuarioId, pedido.CodigoTotp, agora), ct: ct);
        }
        else
        {
            // Desafio ligado à sessão e de uso único: sai do cache já aqui, dê certo ou não.
            // Sem desafio pendente ou com resposta malformada: 401 sem contar, como no login.
            if (desafios.RetirarConfirmacao(sessaoId) is not { } opcoes) return ResultadoDaConfirmacao.Falhou;
            if (ServicoDePasskeys.ParaAssercao(pedido.Passkey) is not { } resposta) return ResultadoDaConfirmacao.Falhou;
            resultado = await tentativa.ExecutarAsync(
                usuarioId, (_, _) => passkeys.ConferirConfirmacaoAsync(usuarioId, resposta, opcoes, agora),
                regra: RegraDeBloqueio.NaoSeAplica, ct: ct);
        }

        switch (resultado)
        {
            case ResultadoDaTentativa.Sucesso:
                await sessoes.ConfirmarAsync(sessaoId, CancellationToken.None);
                return ResultadoDaConfirmacao.Confirmado;
            case ResultadoDaTentativa.Bloqueado:
                return ResultadoDaConfirmacao.Bloqueado;
            default:
                return ResultadoDaConfirmacao.Falhou;
        }
    }
}
