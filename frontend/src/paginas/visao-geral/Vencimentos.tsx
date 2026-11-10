import { n } from "../../api/formulario";
import type { Vencimento } from "../../api/painel";
import { formatarReais } from "../../lib/dinheiro";
import { aparenciaDaFatura } from "../../lib/painel";

const diaEMes = (iso: string) => `${iso.slice(8, 10)}/${iso.slice(5, 7)}`;

export function Vencimentos({ dados }: { dados: Vencimento[] }) {
  if (dados.length === 0) return <p className="cartao-nota">Nenhuma fatura a vencer</p>;
  return (
    <ul className="lista-painel">
      {dados.map((v) => {
        const a = aparenciaDaFatura(v.estado);
        const atraso = n(v.atrasoCentavos);
        return (
          <li key={`${v.contaId}-${v.vencimento}`}>
            <div className="linha-painel">
              <span className="linha-nome">
                {v.apelido} <span className="num texto-suave">vence {diaEMes(v.vencimento)}</span>
              </span>
              <span className="num">{formatarReais(n(v.valorCentavos))}</span>
            </div>
            <div className="linha-painel cartao-nota">
              <span>{v.estado === "ABERTA" ? `${a.rotulo} · fecha em ${diaEMes(v.fechamento)}` : `${a.rotulo} em ${diaEMes(v.fechamento)}`}</span>
              {atraso > 0 && (
                <span className="tom-alerta">
                  inclui <span className="num">{formatarReais(atraso)}</span> em atraso
                </span>
              )}
            </div>
          </li>
        );
      })}
    </ul>
  );
}
