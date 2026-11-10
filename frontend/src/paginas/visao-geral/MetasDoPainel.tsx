import { Link } from "react-router";
import { n } from "../../api/formulario";
import type { MetasDoPainel as Dados } from "../../api/painel";
import { formatarReaisInteiros } from "../../lib/dinheiro";
import { aparenciaDaMeta } from "../../lib/painel";

export function MetasDoPainel({ dados }: { dados: Dados }) {
  if (dados.metas.length === 0) return (
      <p className="cartao-nota">
        Nenhuma meta ainda. <Link to="/metas">Criar meta</Link>
      </p>
    );
  return (
    <ul className="lista-painel">
      {dados.metas.map((m) => {
        const a = aparenciaDaMeta(m.situacao, m.chegaEm);
        const pct = Math.min(Math.max(n(m.progressoPercentual), 0), 100);
        return (
          <li key={m.id}>
            <div className="linha-painel">
              <span className="linha-nome">{m.nome}</span>
              <span className="num">{n(m.progressoPercentual)}%</span>
            </div>
            <div className="uso-trilho" aria-hidden="true">
              <div className="uso-barra" style={{ width: `${pct}%` }} />
            </div>
            <div className="linha-painel cartao-nota">
              <span className="num">
                {formatarReaisInteiros(n(m.guardadoCentavos))} de {formatarReaisInteiros(n(m.valorAlvoCentavos))}
              </span>
              <span className={a.tom === "alerta" ? "tom-alerta" : a.tom === "ok" ? "tom-positivo" : ""}>{a.texto}</span>
            </div>
          </li>
        );
      })}
    </ul>
  );
}
