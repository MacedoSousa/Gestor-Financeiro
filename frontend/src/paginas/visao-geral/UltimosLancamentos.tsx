import { n } from "../../api/formulario";
import type { UltimoLancamento } from "../../api/painel";
import { formatarReais } from "../../lib/dinheiro";

const diaEMes = (iso: string) => `${iso.slice(8, 10)}/${iso.slice(5, 7)}`;

export function UltimosLancamentos({ dados }: { dados: UltimoLancamento[] }) {
  if (dados.length === 0) return <p className="cartao-nota">Nenhum lançamento ainda</p>;
  return (
    <ul className="lista-painel">
      {dados.map((l) => {
        const centavos = n(l.valorCentavos);
        const classe = l.natureza === "TRANSFERENCIA" ? "valor-neutro" : centavos > 0 ? "valor-positivo" : "";
        return (
          <li key={l.id}>
            <div className="linha-painel">
              <span className="linha-nome">{l.descricao}</span>
              <span className={`num ${classe}`}>
                {centavos < 0 ? "−" : "+"}
                {formatarReais(Math.abs(centavos))}
              </span>
            </div>
            <div className="cartao-nota">
              <span className="num">{diaEMes(l.data)}</span> · {l.conta.apelido}
            </div>
          </li>
        );
      })}
    </ul>
  );
}
