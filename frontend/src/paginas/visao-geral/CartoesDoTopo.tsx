import type { ReactNode } from "react";
import { n } from "../../api/formulario";
import type { Secao, VisaoGeral } from "../../api/painel";
import { formatarReais, formatarReaisInteiros } from "../../lib/dinheiro";
import { ErroDeSecao } from "./Secao";

type Props = { visao: VisaoGeral; tentando: boolean; aoTentarDeNovo: () => void };

const diaEMes = (iso: string) => `${iso.slice(8, 10)}/${iso.slice(5, 7)}`;
const percentual = (v: number) => new Intl.NumberFormat("pt-BR", { maximumFractionDigits: 1 }).format(v);

function Cartao<T>({
  rotulo,
  secao,
  destaque,
  tentando,
  aoTentarDeNovo,
  children,
}: {
  rotulo: string;
  secao: Secao<T>;
  destaque?: boolean;
  tentando: boolean;
  aoTentarDeNovo: () => void;
  children: (dados: T) => { valor: ReactNode; nota: ReactNode; tom?: "alerta" | "positivo" };
}) {
  return (
    <div className={`cartao${destaque ? " destaque" : ""}`}>
      <div className="cartao-rotulo">{rotulo}</div>
      {secao.erro !== null || secao.dados === null ? (
        <ErroDeSecao tentando={tentando} aoTentarDeNovo={aoTentarDeNovo} />
      ) : (
        (() => {
          const { valor, nota, tom } = children(secao.dados);
          return (
            <>
              <div className={`cartao-valor num${tom ? ` tom-${tom}` : ""}`}>{valor}</div>
              <div className="cartao-nota">{nota}</div>
            </>
          );
        })()
      )}
    </div>
  );
}

export function CartoesDoTopo({ visao, tentando, aoTentarDeNovo }: Props) {
  const base = { tentando, aoTentarDeNovo };
  return (
    <section className="grade-kpi" aria-label="Resumo do mês">
      <Cartao rotulo="Ainda dá para gastar" destaque secao={visao.aindaDaParaGastar} {...base}>
        {(d) => {
          const restante = d.restanteCentavos == null ? null : n(d.restanteCentavos);
          if (d.situacao === "SEM_TETO") return { valor: "—", nota: "Sem teto de gastos neste mês" };
          if (d.situacao === "PASSOU")
            return { valor: formatarReais(restante ?? 0), nota: `Passou ${formatarReais(Math.abs(restante ?? 0))} do teto`, tom: "alerta" };
          if (d.situacao === "ESGOTADO") return { valor: formatarReais(0), nota: "Teto atingido" };
          return {
            valor: formatarReais(restante ?? 0),
            nota: (
              <>
                {d.porDiaCentavos != null && (
                  <>
                    <span className="num">{formatarReaisInteiros(n(d.porDiaCentavos))}</span> por dia até {diaEMes(d.fimDoMes)}
                  </>
                )}
                {d.percentualUsado != null && <> · {n(d.percentualUsado)}% do teto usado</>}
              </>
            ),
          };
        }}
      </Cartao>

      <Cartao rotulo="Saldo das contas" secao={visao.saldoDasContas} {...base}>
        {(d) => ({
          valor: formatarReais(n(d.totalCentavos)),
          nota:
            d.contas.length === 0 ? (
              "Nenhuma conta de saldo"
            ) : (
              <ul className="lista-nota">
                {d.contas.map((c) => (
                  <li key={c.contaId}>
                    {c.apelido} <span className="num">{formatarReais(n(c.saldoCentavos))}</span>
                  </li>
                ))}
                {n(d.outrasContas) > 0 && <li>e mais {n(d.outrasContas)} {n(d.outrasContas) === 1 ? "conta" : "contas"}</li>}
              </ul>
            ),
        })}
      </Cartao>

      <Cartao rotulo="Aporte planejado" secao={visao.aportePlanejado} {...base}>
        {(d) =>
          d.metas.length === 0
            ? { valor: formatarReais(0), nota: "Nenhuma meta" }
            : {
                valor: formatarReais(n(d.totalCentavos)),
                nota: (
                  <>
                    Já aportado no mês: <span className="num">{formatarReais(n(d.jaAportadoCentavos))}</span>
                  </>
                ),
              }
        }
      </Cartao>

      <Cartao rotulo="Patrimônio do casal" secao={visao.patrimonio} {...base}>
        {(d) => {
          const variacao = d.variacaoCentavos == null ? null : n(d.variacaoCentavos);
          return {
            valor: formatarReais(n(d.totalCentavos)),
            nota: (
              <>
                {variacao === null ? (
                  "Sem mês anterior para comparar. "
                ) : (
                  <span className={variacao < 0 ? "tom-alerta" : variacao > 0 ? "tom-positivo" : ""}>
                    <span className="num">
                      {variacao > 0 ? "+" : variacao < 0 ? "−" : ""}
                      {formatarReais(Math.abs(variacao))}
                    </span>
                    {d.variacaoPercentual != null && (
                      <span className="num">
                        {" "}
                        ({n(d.variacaoPercentual) > 0 ? "+" : n(d.variacaoPercentual) < 0 ? "−" : ""}
                        {percentual(Math.abs(n(d.variacaoPercentual)))}%)
                      </span>
                    )}{" "}
                    sobre o mês anterior.{" "}
                  </span>
                )}
                Não inclui dívidas fora do cartão (empréstimo, consórcio)
              </>
            ),
          };
        }}
      </Cartao>
    </section>
  );
}
