import { n } from "../../api/formulario";
import type { PatrimonioEm12Meses } from "../../api/painel";
import { formatarReais } from "../../lib/dinheiro";
import { mesCurto } from "../../lib/mes";
import { resumoDoGrafico } from "../../lib/painel";

const L = 640;
const A = 200;
const MARGEM = { topo: 14, base: 28, lados: 24 };

type Marca = { x: number; y: number; total: number };

/** Posições no desenho (apresentação): um slot por mês; mês sem dado não tem marca e quebra a linha. */
function marcas(pontos: PatrimonioEm12Meses["pontos"]): (Marca | null)[] {
  const totais = pontos.map((p) => (p.totalCentavos == null ? null : n(p.totalCentavos)));
  const dados = totais.filter((t): t is number => t !== null);
  const min = Math.min(...dados);
  const max = Math.max(...dados);
  const util = A - MARGEM.topo - MARGEM.base;
  const passo = pontos.length > 1 ? (L - 2 * MARGEM.lados) / (pontos.length - 1) : 0;
  return totais.map((t, i) =>
    t === null
      ? null
      : {
          x: MARGEM.lados + i * passo,
          y: MARGEM.topo + (max === min ? util / 2 : util - ((t - min) / (max - min)) * util),
          total: t,
        },
  );
}

/** Trechos contínuos de meses com dado. */
function trechos(m: (Marca | null)[]): Marca[][] {
  const saida: Marca[][] = [];
  let atual: Marca[] = [];
  for (const p of m) {
    if (p) atual.push(p);
    else if (atual.length > 0) {
      saida.push(atual);
      atual = [];
    }
  }
  if (atual.length > 0) saida.push(atual);
  return saida;
}

export function GraficoDoPatrimonio({ dados }: { dados: PatrimonioEm12Meses }) {
  const resumo = resumoDoGrafico(dados.pontos);
  if (resumo === null) return <p className="cartao-nota">Ainda não há patrimônio registrado</p>;

  const m = marcas(dados.pontos);
  const base = A - MARGEM.base;
  return (
    <>
      <p className="subtitulo">{dados.intervalo}</p>
      <svg className="grafico" viewBox={`0 0 ${L} ${A}`} role="img" aria-label={resumo} preserveAspectRatio="xMidYMid meet">
        <line x1={MARGEM.lados} x2={L - MARGEM.lados} y1={base} y2={base} className="grafico-eixo" />
        {trechos(m).map((t, i) =>
          t.length === 1 ? (
            <circle key={i} cx={t[0]!.x} cy={t[0]!.y} r={4} className="grafico-ponto" />
          ) : (
            <g key={i}>
              <path
                d={`M${t[0]!.x},${base} ${t.map((p) => `L${p.x},${p.y}`).join(" ")} L${t[t.length - 1]!.x},${base} Z`}
                className="grafico-area"
              />
              <polyline points={t.map((p) => `${p.x},${p.y}`).join(" ")} className="grafico-linha" />
              {t.map((p) => (
                <circle key={p.x} cx={p.x} cy={p.y} r={3} className="grafico-ponto" />
              ))}
            </g>
          ),
        )}
        {dados.pontos.map((p, i) => (
          <text key={p.mes} x={MARGEM.lados + i * ((L - 2 * MARGEM.lados) / Math.max(dados.pontos.length - 1, 1))} y={A - 8} textAnchor="middle" className="grafico-rotulo">
            {p.rotulo}
          </text>
        ))}
      </svg>
      <details className="valores-por-mes">
        <summary>Ver valores por mês</summary>
        <div className="rolagem-x">
          <table className="tabela">
            <thead>
              <tr>
                <th scope="col">Mês</th>
                <th scope="col" className="celula-valor">
                  Patrimônio
                </th>
              </tr>
            </thead>
            <tbody>
              {dados.pontos.map((p) => (
                <tr key={p.mes}>
                  <td>{mesCurto(p.mes)}</td>
                  <td className="num celula-valor">{p.totalCentavos == null ? "—" : formatarReais(n(p.totalCentavos))}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </details>
    </>
  );
}
