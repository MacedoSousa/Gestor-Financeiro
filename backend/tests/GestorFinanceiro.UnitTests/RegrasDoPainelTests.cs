using GestorFinanceiro.Modules.Contas.Contracts;
using GestorFinanceiro.Modules.Investimentos.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Contracts;
using GestorFinanceiro.Modules.Orcamento.Contracts;
using GestorFinanceiro.Modules.Painel.Dominio;

namespace GestorFinanceiro.UnitTests;

public class RegrasDoPainelTests
{
    private static readonly DateOnly Hoje = new(2026, 11, 5);

    private static ContaResumo Conta(string apelido, string tipo = "CORRENTE", bool arquivada = false, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), apelido, tipo, null, 0, new DateOnly(2026, 9, 1), null, null, arquivada);

    private static MetaDto Meta(string nome, long planejado = 0, long aporteDoMes = 0) =>
        new(Guid.NewGuid(), nome, 1_000_000, null, planejado, 0m, [], 250_000, 25, aporteDoMes,
            new ProjecaoDto(planejado, "CHEGA", 10, "2027-09", null, false, null));

    private static TetoDaCategoriaDto Cat(string nome, long? teto, long usado, string? papel = null) =>
        new(Guid.NewGuid(), nome, papel, false, teto, usado, teto - usado);

    [Fact]
    public void Restante_por_dia_conta_hoje_ate_o_fim_do_mes()
    {
        var r = RegrasDoPainel.AindaDaParaGastar(new FatiaDto(400_000, 83_600, null), Hoje);

        Assert.Equal("COM_TETO", r.Situacao);
        Assert.Equal(316_400, r.RestanteCentavos);
        Assert.Equal(12_100, r.PorDiaCentavos);
        Assert.Equal(20, r.PercentualUsado);
        Assert.Equal(new DateOnly(2026, 11, 30), r.FimDoMes);
    }

    [Fact]
    public void Restante_por_dia_no_ultimo_dia_do_mes()
    {
        var r = RegrasDoPainel.AindaDaParaGastar(new FatiaDto(100_000, 87_655, null), new DateOnly(2026, 11, 30));

        Assert.Equal(12_345, r.RestanteCentavos);
        Assert.Equal(12_300, r.PorDiaCentavos);
    }

    [Fact]
    public void Passou_do_teto_nao_tem_valor_por_dia()
    {
        var r = RegrasDoPainel.AindaDaParaGastar(new FatiaDto(100_000, 100_500, null), Hoje);

        Assert.Equal("PASSOU", r.Situacao);
        Assert.Equal(-500, r.RestanteCentavos);
        Assert.Null(r.PorDiaCentavos);
    }

    [Fact]
    public void Restante_exatamente_zero_esta_esgotado()
    {
        var r = RegrasDoPainel.AindaDaParaGastar(new FatiaDto(100_000, 100_000, null), Hoje);

        Assert.Equal("ESGOTADO", r.Situacao);
        Assert.Null(r.PorDiaCentavos);
        Assert.Equal(100, r.PercentualUsado);
    }

    [Fact]
    public void Teto_zero_sem_uso_esta_esgotado()
    {
        var r = RegrasDoPainel.AindaDaParaGastar(new FatiaDto(0, 0, null), Hoje);

        Assert.Equal("ESGOTADO", r.Situacao);
        Assert.Null(r.PercentualUsado);
        Assert.Null(r.PorDiaCentavos);
    }

    [Fact]
    public void Sem_teto_devolve_sem_teto()
    {
        var r = RegrasDoPainel.AindaDaParaGastar(new FatiaDto(null, 12_000, null), Hoje);

        Assert.Equal("SEM_TETO", r.Situacao);
        Assert.Null(r.TetoCentavos);
        Assert.Null(r.RestanteCentavos);
        Assert.Null(r.PorDiaCentavos);
        Assert.Null(r.PercentualUsado);
        Assert.Equal(12_000, r.UsadoCentavos);
    }

    [Fact]
    public void Percentual_do_teto_arredonda_para_baixo()
    {
        var r = RegrasDoPainel.AindaDaParaGastar(new FatiaDto(100_000, 99_999, null), Hoje);

        Assert.Equal(99, r.PercentualUsado);
    }

    [Fact]
    public void Variacao_do_patrimonio_sem_percentual_quando_mes_anterior_e_zero_negativo_ou_nulo()
    {
        static IReadOnlyList<PatrimonioDoMes> Dois(long? anterior, long? hoje) =>
            [new("2026-10", anterior), new("2026-11", hoje)];

        var zero = RegrasDoPainel.Patrimonio(Dois(0, 1_000));
        Assert.Equal(1_000, zero.VariacaoCentavos);
        Assert.Null(zero.VariacaoPercentual);

        var negativo = RegrasDoPainel.Patrimonio(Dois(-2_000, 1_000));
        Assert.Equal(3_000, negativo.VariacaoCentavos);
        Assert.Null(negativo.VariacaoPercentual);

        var nulo = RegrasDoPainel.Patrimonio(Dois(null, 1_000));
        Assert.Equal(1_000, nulo.TotalCentavos);
        Assert.Null(nulo.VariacaoCentavos);
        Assert.Null(nulo.VariacaoPercentual);
    }

    [Fact]
    public void Variacao_do_patrimonio_com_percentual_de_uma_casa()
    {
        var r = RegrasDoPainel.Patrimonio([new("2026-10", 9_905_000), new("2026-11", 9_855_000)]);

        Assert.Equal(9_855_000, r.TotalCentavos);
        Assert.Equal(-50_000, r.VariacaoCentavos);
        Assert.Equal(-0.5m, r.VariacaoPercentual);
    }

    [Fact]
    public void Intervalo_e_rotulos_dos_12_meses()
    {
        var pontos = Enumerable.Range(0, 12)
            .Select(i => new PatrimonioDoMes(new DateOnly(2025, 12, 1).AddMonths(i).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture), i == 11 ? 5 : null))
            .ToList();

        var r = RegrasDoPainel.PatrimonioEm12Meses(pontos);

        Assert.Equal("dez/2025 → nov/2026", r.Intervalo);
        Assert.Equal(["dez", "jan", "fev", "mar", "abr", "mai", "jun", "jul", "ago", "set", "out", "nov"], r.Pontos.Select(p => p.Rotulo));
        Assert.Equal("2025-12", r.Pontos[0].Mes);
        Assert.Null(r.Pontos[0].TotalCentavos);
        Assert.Equal(5, r.Pontos[11].TotalCentavos);
    }

    private static (IReadOnlyList<FaturasDoCartao> Faturas, IReadOnlyDictionary<Guid, ContaResumo> Contas) Cartao(
        string apelido, DateOnly fechamento, DateOnly vencimento, long total = 15_000, long restante = 15_000, long atraso = 0,
        DateOnly? vencAberta = null, long aberta = 18_800)
    {
        var c = Conta(apelido, "CARTAO");
        var f = new FaturasDoCartao(c.Id,
            new FaturaFechadaAPagar(vencimento, fechamento, total, restante, atraso),
            new FaturaAbertaAteHoje(vencAberta ?? new DateOnly(2026, 12, 10), new DateOnly(2026, 12, 1), aberta));
        return ([f], new Dictionary<Guid, ContaResumo> { [c.Id] = c });
    }

    [Fact]
    public void Aviso_de_fatura_so_ate_5_dias_do_fechamento()
    {
        var (faturas, contas) = Cartao("Itaú Visa", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 10));

        var em06 = RegrasDoPainel.AvisosDeFatura(faturas, contas, new DateOnly(2026, 11, 6));
        var em07 = RegrasDoPainel.AvisosDeFatura(faturas, contas, new DateOnly(2026, 11, 7));

        var aviso = Assert.Single(em06);
        Assert.Equal("Compras no cartão Itaú Visa a partir de hoje só vencem em 10/12 — a fatura fechou em 01/11", aviso.Texto);
        Assert.Equal(faturas[0].ContaId, aviso.ContaId);
        Assert.Empty(em07);
    }

    [Fact]
    public void Aviso_de_fatura_ignora_cartao_arquivado()
    {
        var (faturas, contas) = Cartao("Itaú Visa", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 10));
        var arquivado = contas[faturas[0].ContaId] with { Arquivada = true };
        var contasArquivadas = new Dictionary<Guid, ContaResumo> { [arquivado.Id] = arquivado };

        Assert.Empty(RegrasDoPainel.AvisosDeFatura(faturas, contasArquivadas, new DateOnly(2026, 11, 6)));
    }

    [Fact]
    public void Saldo_das_contas_lista_3_ou_as_2_maiores_e_mais_N()
    {
        var a = Conta("A"); var b = Conta("B", "DINHEIRO"); var c = Conta("C"); var d = Conta("D");
        var poup = Conta("Poup", "POUPANCA"); var arq = Conta("Arq", arquivada: true);
        var saldos = new Dictionary<Guid, long>
        {
            [a.Id] = 100, [b.Id] = 300, [c.Id] = 200, [d.Id] = 50, [poup.Id] = 9_999, [arq.Id] = 8_888,
        };

        var tres = RegrasDoPainel.SaldoDasContas([a, b, c, poup, arq], saldos);
        Assert.Equal(600, tres.TotalCentavos);
        Assert.Equal(3, tres.Contas.Count);
        Assert.Equal(0, tres.OutrasContas);

        var quatro = RegrasDoPainel.SaldoDasContas([a, b, c, d, poup, arq], saldos);
        Assert.Equal(650, quatro.TotalCentavos);
        Assert.Equal(["B", "C"], quatro.Contas.Select(x => x.Apelido));
        Assert.Equal(2, quatro.OutrasContas);
    }

    [Fact]
    public void Aporte_planejado_soma_planejado_e_aportado()
    {
        var r = RegrasDoPainel.AportePlanejado([Meta("Casa", 500_000, 500_000), Meta("Carro", 180_000, 0)]);

        Assert.Equal(680_000, r.TotalCentavos);
        Assert.Equal(500_000, r.JaAportadoCentavos);
        Assert.Equal(["Casa", "Carro"], r.Metas.Select(m => m.Nome));
    }

    [Fact]
    public void Orcamento_ordena_por_percentual_e_corta_em_6()
    {
        var cats = new List<TetoDaCategoriaDto>
        {
            Cat("B", 1_000, 500), Cat("A", 1_000, 500), Cat("C", 1_000, 900), Cat("D", 1_000, 100),
            Cat("E", 1_000, 0), Cat("F", 1_000, 300), Cat("G", 1_000, 700), Cat("H", 1_000, 1_200),
            Cat("Investir", 1_000, 999, papel: "INVESTIR"), Cat("SemTeto", null, 800),
        };

        var r = RegrasDoPainel.Orcamento(cats);

        Assert.Equal(["H", "C", "G", "A", "B", "F"], r.Categorias.Select(c => c.Nome));
        Assert.Equal(2, r.OutrasCategorias);
        Assert.Equal("PASSOU", r.Categorias[0].Estado);
        Assert.Equal(120, r.Categorias[0].PercentualUsado);
        Assert.Equal("NORMAL", r.Categorias[1].Estado);
        Assert.Equal(50, r.Categorias[3].PercentualUsado);
    }

    [Fact]
    public void Categoria_com_teto_zero_e_uso_vem_primeiro_como_passou()
    {
        var r = RegrasDoPainel.Orcamento([Cat("Mercado", 1_000, 5_000), Cat("Extra", 0, 100), Cat("Vazia", 0, 0)]);

        Assert.Equal(["Extra", "Mercado", "Vazia"], r.Categorias.Select(c => c.Nome));
        Assert.Equal("PASSOU", r.Categorias[0].Estado);
        Assert.Equal(100, r.Categorias[0].PercentualUsado);
        Assert.Equal("NORMAL", r.Categorias[2].Estado);
        Assert.Equal(0, r.Categorias[2].PercentualUsado);
    }

    [Fact]
    public void Vencimentos_ordena_e_corta_em_5()
    {
        var contas = new Dictionary<Guid, ContaResumo>();
        var faturas = new List<FaturasDoCartao>();
        void Add(string apelido, DateOnly venc, long restante, long atraso, long aberta, DateOnly vencAberta)
        {
            var c = Conta(apelido, "CARTAO");
            contas[c.Id] = c;
            faturas.Add(new FaturasDoCartao(c.Id,
                new FaturaFechadaAPagar(venc, venc.AddDays(-9), restante, restante, atraso),
                new FaturaAbertaAteHoje(vencAberta, vencAberta.AddDays(-9), aberta)));
        }
        Add("Visa", new DateOnly(2026, 11, 10), 15_000, 5_000, 18_800, new DateOnly(2026, 12, 10));
        Add("Master", new DateOnly(2026, 11, 5), 7_000, 0, 0, new DateOnly(2026, 12, 5));
        Add("Velho", new DateOnly(2026, 11, 4), 9_000, 0, 0, new DateOnly(2026, 12, 4));
        Add("Quitado", new DateOnly(2026, 11, 20), 0, 0, 3_000, new DateOnly(2026, 12, 20));
        Add("Elo", new DateOnly(2026, 11, 25), 1_000, 0, 2_000, new DateOnly(2026, 12, 25));
        Add("Amex", new DateOnly(2026, 11, 8), 4_000, 0, 6_000, new DateOnly(2026, 12, 8));

        var r = RegrasDoPainel.Vencimentos(faturas, contas, Hoje);

        Assert.Equal(5, r.Count);
        Assert.Equal(
            [("Master", "FECHADA"), ("Amex", "FECHADA"), ("Visa", "COM_ATRASO"), ("Elo", "FECHADA"), ("Amex", "ABERTA")],
            r.Select(v => (v.Apelido, v.Estado)));
        var visa = r[2];
        Assert.Equal(15_000, visa.ValorCentavos);
        Assert.Equal(5_000, visa.AtrasoCentavos);
        Assert.Equal(new DateOnly(2026, 11, 1), visa.Fechamento);
        Assert.DoesNotContain(r, v => v.Apelido is "Velho" or "Quitado");
    }

    [Fact]
    public void Vencida_fica_no_topo_fora_do_corte_de_5()
    {
        var contas = new Dictionary<Guid, ContaResumo>();
        var faturas = new List<FaturasDoCartao>();
        void Add(string apelido, int dia, FaturaVencida? vencida = null)
        {
            var c = Conta(apelido, "CARTAO");
            contas[c.Id] = c;
            var venc = new DateOnly(2026, 11, dia);
            faturas.Add(new FaturasDoCartao(c.Id,
                new FaturaFechadaAPagar(venc, venc.AddDays(-9), 1_000, 1_000, 0),
                new FaturaAbertaAteHoje(venc.AddMonths(1), venc.AddMonths(1).AddDays(-9), 2_000),
                vencida));
        }
        Add("A", 20);
        Add("B", 21);
        Add("C", 22, new FaturaVencida(new DateOnly(2026, 10, 22), new DateOnly(2026, 10, 13), 7_000));
        Add("D", 23, new FaturaVencida(new DateOnly(2026, 10, 3), new DateOnly(2026, 9, 24), 4_000));

        var r = RegrasDoPainel.Vencimentos(faturas, contas, Hoje);

        Assert.Equal(7, r.Count);
        Assert.Equal([("D", "VENCIDA"), ("C", "VENCIDA")], r.Take(2).Select(v => (v.Apelido, v.Estado)));
        Assert.Equal(new DateOnly(2026, 10, 3), r[0].Vencimento);
        Assert.Equal(4_000, r[0].ValorCentavos);
        Assert.Equal(0, r[0].AtrasoCentavos);
        Assert.Equal(7_000, r[1].ValorCentavos);
        Assert.Equal(5, r.Skip(2).Count(v => v.Estado != "VENCIDA"));
        Assert.Equal(["A", "B", "C", "D", "A"], r.Skip(2).Select(v => v.Apelido));
    }

    [Fact]
    public void Ainda_da_para_gastar_soma_as_faturas_vencidas()
    {
        var (visa, _) = Cartao("Visa", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 10));
        var (master, _) = Cartao("Master", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 10));
        var (elo, _) = Cartao("Elo", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 10));
        var faturas = new[]
        {
            visa[0] with { Vencida = new FaturaVencida(new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 1), 5_000) },
            master[0] with { Vencida = new FaturaVencida(new DateOnly(2026, 11, 3), new DateOnly(2026, 10, 25), 12_345) },
            elo[0],
        };

        Assert.Equal(17_345, RegrasDoPainel.TotalVencido(faturas));
        Assert.Null(RegrasDoPainel.TotalVencido([elo[0]]));
        Assert.Null(RegrasDoPainel.TotalVencido([]));

        var comTeto = RegrasDoPainel.AindaDaParaGastar(new FatiaDto(400_000, 83_600, null), Hoje, 17_345);
        var semTeto = RegrasDoPainel.AindaDaParaGastar(new FatiaDto(null, 0, null), Hoje, 5_000);
        var passou = RegrasDoPainel.AindaDaParaGastar(new FatiaDto(100_000, 100_500, null), Hoje, 5_000);
        var nada = RegrasDoPainel.AindaDaParaGastar(new FatiaDto(400_000, 83_600, null), Hoje, null);

        Assert.Equal(17_345, comTeto.FaturaVencidaCentavos);
        Assert.Equal(5_000, semTeto.FaturaVencidaCentavos);
        Assert.Equal(5_000, passou.FaturaVencidaCentavos);
        Assert.Null(nada.FaturaVencidaCentavos);
    }

    [Fact]
    public void Metas_corta_em_3_e_mantem_a_ordem()
    {
        var r = RegrasDoPainel.Metas([Meta("D"), Meta("A"), Meta("C"), Meta("B")]);

        Assert.Equal(4, r.Total);
        Assert.Equal(["D", "A", "C"], r.Metas.Select(m => m.Nome));
        Assert.Equal("CHEGA", r.Metas[0].Situacao);
        Assert.Equal("2027-09", r.Metas[0].ChegaEm);
        Assert.Equal(25, r.Metas[0].ProgressoPercentual);
    }
}
