using System.Globalization;
using GestorFinanceiro.Modules.Contas.Contracts;
using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.Modules.Investimentos.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Contracts;
using GestorFinanceiro.Modules.Orcamento.Contracts;
using GestorFinanceiro.Modules.Painel.Contracts;
using GestorFinanceiro.Modules.Painel.Dominio;
using GestorFinanceiro.SharedKernel.Aplicacao;
using Microsoft.Extensions.Logging;

namespace GestorFinanceiro.Modules.Painel.Aplicacao;

/// <summary>
/// Monta a Visão geral chamando os Contracts dos outros módulos em sequência. Cada seção falha sozinha:
/// o log leva só o nome da seção, o tipo da exceção e a correlação, e a seção vai com texto fixo.
/// </summary>
internal sealed partial class MontagemDoPainel(
    IUsuarioAtual usuario,
    TimeProvider relogio,
    IConsultaDeContas contas,
    IConsultaDeSaldos saldos,
    IConsultaDeFaturas faturas,
    IConsultaDoExtrato extrato,
    IConsultaDoOrcamento orcamento,
    IConsultaDeMetas metas,
    IConsultaDeUsuarios usuarios,
    ILogger<MontagemDoPainel> logger)
{
    private const string TextoDeErro = "Não foi possível carregar";

    /// <summary>Uma leitura feita uma vez: guarda o valor ou o tipo da exceção para cada seção dependente.</summary>
    private sealed class Leitura<T>
    {
        public T? Valor { get; init; }
        public string? TipoDoErro { get; init; }
    }

    public async Task<VisaoGeralDto?> MontarAsync(string correlacao, CancellationToken ct)
    {
        if (usuario.CasalId is not { } casalId) return null;

        var hoje = Brasilia.Hoje(relogio);
        var mes = new DateOnly(hoje.Year, hoje.Month, 1);
        var primeiroMes = mes.AddMonths(-11);

        var lContas = await LerAsync(() => contas.ListarAsync(ct));
        var lFaturas = await LerAsync(() => faturas.AVencerAsync(ct));
        var lMetas = await LerAsync(() => metas.PainelAsync(ct));
        var lOrcamento = await LerAsync(() => orcamento.DoMesAsync(mes, ct));
        var lPatrimonio = await LerAsync(() => saldos.PatrimonioPorMesAsync(primeiroMes, 12, ct));

        // Se as faturas falharem, o cartão sai sem o alerta de vencida; a seção Vencimentos é que mostra (e registra) o erro.
        var vencido = lFaturas.TipoDoErro is null ? RegrasDoPainel.TotalVencido(lFaturas.Valor!) : null;
        var secaoGastar = Secao("AindaDaParaGastar", correlacao, lOrcamento,
            o => RegrasDoPainel.AindaDaParaGastar(o.Fatias.TetoDeGastos, hoje, vencido));

        var secaoSaldo = await SecaoAsync("SaldoDasContas", correlacao, async () =>
        {
            var lista = Valor(lContas);
            var porConta = (await saldos.SaldosAsync(hoje, ct)).ToDictionary(s => s.ContaId, s => s.SaldoCentavos);
            return RegrasDoPainel.SaldoDasContas(lista, porConta);
        });

        var secaoAporte = Secao("AportePlanejado", correlacao, lMetas, m => RegrasDoPainel.AportePlanejado(m.Metas));
        var secaoPatrimonio = Secao("Patrimonio", correlacao, lPatrimonio, RegrasDoPainel.Patrimonio);

        var secaoAvisos = Secao<IReadOnlyList<AvisoDeFaturaDto>>("AvisosDeFatura", correlacao, () =>
            RegrasDoPainel.AvisosDeFatura(Valor(lFaturas), PorId(Valor(lContas)), hoje));

        var secao12 = Secao("PatrimonioEm12Meses", correlacao, lPatrimonio, RegrasDoPainel.PatrimonioEm12Meses);
        var secaoMetas = Secao("Metas", correlacao, lMetas, m => RegrasDoPainel.Metas(m.Metas));
        var secaoOrcamento = Secao("Orcamento", correlacao, lOrcamento, o => RegrasDoPainel.Orcamento(o.Categorias));

        var secaoVencimentos = Secao<IReadOnlyList<VencimentoDto>>("Vencimentos", correlacao, () =>
            RegrasDoPainel.Vencimentos(Valor(lFaturas), PorId(Valor(lContas)), hoje));

        var secaoUltimos = await SecaoAsync("UltimosLancamentos", correlacao, () => extrato.UltimosAsync(5, ct));

        return new VisaoGeralDto(
            mes.ToString("yyyy-MM", CultureInfo.InvariantCulture), hoje, hoje.Day, DateTime.DaysInMonth(hoje.Year, hoje.Month),
            await ApelidoAsync(casalId, correlacao, ct),
            secaoGastar, secaoSaldo, secaoAporte, secaoPatrimonio, secaoAvisos, secao12, secaoMetas, secaoOrcamento,
            secaoVencimentos, secaoUltimos);
    }

    private async Task<string> ApelidoAsync(Guid casalId, string correlacao, CancellationToken ct)
    {
        try
        {
            var lista = await usuarios.ListarDoCasalAsync(casalId, ct);
            return lista.FirstOrDefault(u => u.UsuarioId == usuario.UsuarioId)?.Apelido ?? "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Registrar("Apelido", ex.GetType().Name, correlacao);
            return "";
        }
    }

    private static async Task<Leitura<T>> LerAsync<T>(Func<Task<T>> ler)
    {
        try
        {
            return new Leitura<T> { Valor = await ler() };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new Leitura<T> { TipoDoErro = ex.GetType().Name };
        }
    }

    /// <summary>Leitura que falhou: relança só o tipo (sem mensagem) para a seção dependente registrar.</summary>
    private sealed class LeituraFalhouException(string tipo) : Exception
    {
        public string Tipo { get; } = tipo;
    }

    private static T Valor<T>(Leitura<T> leitura) =>
        leitura.TipoDoErro is { } tipo ? throw new LeituraFalhouException(tipo) : leitura.Valor!;

    private static Dictionary<Guid, ContaResumo> PorId(IReadOnlyList<ContaResumo> lista) =>
        lista.ToDictionary(c => c.Id);

    private SecaoDto<TSaida> Secao<TEntrada, TSaida>(string nome, string correlacao, Leitura<TEntrada> leitura, Func<TEntrada, TSaida> montar) =>
        Secao(nome, correlacao, () => montar(Valor(leitura)));

    private SecaoDto<T> Secao<T>(string nome, string correlacao, Func<T> montar)
    {
        try
        {
            return new SecaoDto<T>(montar(), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Falha<T>(nome, ex, correlacao);
        }
    }

    private async Task<SecaoDto<T>> SecaoAsync<T>(string nome, string correlacao, Func<Task<T>> montar)
    {
        try
        {
            return new SecaoDto<T>(await montar(), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Falha<T>(nome, ex, correlacao);
        }
    }

    private SecaoDto<T> Falha<T>(string nome, Exception ex, string correlacao)
    {
        Registrar(nome, ex is LeituraFalhouException l ? l.Tipo : ex.GetType().Name, correlacao);
        return new SecaoDto<T>(default, TextoDeErro);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Seção {Secao} falhou ({Tipo}); correlação {Correlacao}")]
    private partial void Registrar(string secao, string tipo, string correlacao);
}
