import { n } from "../../api/formulario";
import type { OrcamentoDoMes } from "../../api/orcamento";
import { formatarReais } from "../../lib/dinheiro";
import { segmentosDaBarra, type Segmento } from "../../lib/orcamento";

const NOMES: Record<Segmento["fatia"], string> = {
  investir: "Investir",
  gastos: "Teto de gastos",
  dividas: "Dívidas",
  folga: "Folga",
};

const percentual = (p: number) => `${p.toLocaleString("pt-BR", { maximumFractionDigits: 1 })}%`;

type Item = { fatia: Segmento["fatia"]; centavos: number | null; pct: number | null; detalhe?: string };

/** "Para onde vai cada real": barra empilhada e legenda com o valor de cada fatia escrito. */
export function DivisaoDaRenda({ orcamento }: { orcamento: OrcamentoDoMes }) {
  const receita = orcamento.receitaPrevistaCentavos == null ? 0 : n(orcamento.receitaPrevistaCentavos);
  const segmentos = segmentosDaBarra(orcamento);
  const folga = orcamento.folgaCentavos == null ? null : n(orcamento.folgaCentavos);
  const { investir, tetoDeGastos, dividas } = orcamento.fatias;
  const usado = (u: number | string, teto: number | string | null) =>
    teto == null ? `usado ${formatarReais(n(u))}, sem teto` : `usado ${formatarReais(n(u))} de ${formatarReais(n(teto))}`;
  const pct = (fatia: Segmento["fatia"]) => segmentos.find((s) => s.fatia === fatia)?.percentual ?? null;
  const itens: Item[] = [
    {
      fatia: "investir",
      centavos: investir.tetoCentavos == null ? null : n(investir.tetoCentavos),
      pct: pct("investir"),
      detalhe: `aportes ${formatarReais(n(investir.aportesCentavos))} + categoria ${formatarReais(n(investir.usadoCentavos))}`,
    },
    { fatia: "gastos", centavos: tetoDeGastos.tetoCentavos == null ? null : n(tetoDeGastos.tetoCentavos), pct: pct("gastos"), detalhe: usado(tetoDeGastos.usadoCentavos, tetoDeGastos.tetoCentavos) },
    { fatia: "dividas", centavos: dividas.tetoCentavos == null ? null : n(dividas.tetoCentavos), pct: pct("dividas"), detalhe: usado(dividas.usadoCentavos, dividas.tetoCentavos) },
    { fatia: "folga", centavos: folga, pct: pct("folga") },
  ];

  return (
    <section className="cartao" aria-labelledby="titulo-divisao">
      <div className="divisao-topo">
        <h2 className="cartao-titulo" id="titulo-divisao">
          Para onde vai cada real do mês
        </h2>
        {receita > 0 && <span className="num subtitulo">{formatarReais(receita)} = 100%</span>}
      </div>

      {receita <= 0 ? (
        <p className="subtitulo">Defina a receita prevista para ver a divisão.</p>
      ) : (
        <>
          <div className="barra-divisao" role="img" aria-label={`Divisão da receita: ${segmentos.map((s) => `${NOMES[s.fatia]} ${percentual(s.percentual)}`).join(", ")}`}>
            {segmentos.map((s) => (
              <div key={s.fatia} className={`segmento-barra fatia-${s.fatia}`} style={{ flexGrow: s.centavos }} />
            ))}
          </div>
          {folga !== null && folga < 0 && (
            <p role="alert" className="alerta-form">
              Os tetos passam da receita em {formatarReais(Math.abs(folga))}
            </p>
          )}
          <ul className="legenda-divisao">
            {itens.map((i) => (
              <li key={i.fatia} className="legenda-item">
                <span className={`legenda-cor fatia-${i.fatia}`} aria-hidden="true" />
                <div>
                  <div className="subtitulo">{NOMES[i.fatia]}</div>
                  <div className="num legenda-valor">
                    {i.centavos === null ? "Sem teto" : formatarReais(Math.max(i.centavos, 0))}
                    {i.pct !== null && ` · ${percentual(i.pct)}`}
                  </div>
                  {i.detalhe && <div className="cartao-nota num">{i.detalhe}</div>}
                </div>
              </li>
            ))}
          </ul>
        </>
      )}
    </section>
  );
}
