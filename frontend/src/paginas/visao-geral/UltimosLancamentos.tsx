import { n } from "../../api/formulario";
import type { UltimoLancamento } from "../../api/painel";
import { formatarReais } from "../../lib/dinheiro";
import { diaDoLancamento, rotuloDaOrigem } from "../../lib/painel";

function Quem({ quem }: { quem: string }) {
  if (quem === "Conjunta")
    return (
      <span className="quem quem-conjunta" role="img" aria-label="Conjunta">
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true">
          <circle cx="9" cy="8" r="3" />
          <circle cx="16" cy="9" r="2.5" />
          <path d="M3 20c0-3.3 2.7-6 6-6s6 2.7 6 6M15 14.5c3 0 6 2 6 5.5" />
        </svg>
      </span>
    );
  return (
    <span className="quem" role="img" aria-label={quem}>
      {quem.charAt(0).toUpperCase()}
    </span>
  );
}

export function UltimosLancamentos({ dados, hoje }: { dados: UltimoLancamento[]; hoje: string }) {
  if (dados.length === 0) return <p className="cartao-nota">Nenhum lançamento ainda</p>;
  return (
    <ul className="lista-painel">
      {dados.map((l) => {
        const centavos = n(l.valorCentavos);
        const classe = l.natureza === "TRANSFERENCIA" ? "valor-neutro" : centavos > 0 ? "valor-positivo" : "";
        const sinal = centavos < 0 ? "−" : centavos > 0 ? "+" : "";
        return (
          <li key={l.id}>
            <div className="linha-painel">
              <span className="linha-nome">
                <Quem quem={l.quem} /> {l.descricao}
              </span>
              <span className={`num ${classe}`}>
                {sinal}
                {formatarReais(Math.abs(centavos))}
              </span>
            </div>
            <div className="cartao-nota">
              {diaDoLancamento(l.data, hoje)} · {l.conta.apelido} · {rotuloDaOrigem(l.origem)}
            </div>
          </li>
        );
      })}
    </ul>
  );
}
