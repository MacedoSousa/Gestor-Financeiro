import { Link } from "react-router";
import { n } from "../../api/formulario";
import type { OrcamentoDoPainel as Dados } from "../../api/painel";
import { formatarReaisInteiros } from "../../lib/dinheiro";

export function OrcamentoDoPainel({ dados }: { dados: Dados }) {
  if (dados.categorias.length === 0) return (
      <p className="cartao-nota">
        Sem tetos neste mês. <Link to="/orcamento">Definir tetos</Link>
      </p>
    );
  const outras = n(dados.outrasCategorias);
  return (
    <ul className="lista-painel">
      {dados.categorias.map((c) => {
        const passou = c.estado === "PASSOU";
        const pct = Math.min(Math.max(n(c.percentualUsado), 0), 100);
        return (
          <li key={c.categoriaId}>
            <div className="linha-painel">
              <span className="linha-nome">{c.nome}</span>
              <span className={`num${passou ? " tom-alerta" : ""}`}>{n(c.percentualUsado)}%</span>
            </div>
            <div className="uso-trilho" aria-hidden="true">
              <div className={`uso-barra${passou ? " uso-acima" : ""}`} style={{ width: `${pct}%` }} />
            </div>
            <div className="linha-painel cartao-nota">
              <span className="num">
                {formatarReaisInteiros(n(c.usadoCentavos))} de {formatarReaisInteiros(n(c.tetoCentavos))}
              </span>
              {passou && <span className="tom-alerta">Passou do teto</span>}
            </div>
          </li>
        );
      })}
      {outras > 0 && <li className="cartao-nota">+{outras} {outras === 1 ? "categoria" : "categorias"}</li>}
    </ul>
  );
}
