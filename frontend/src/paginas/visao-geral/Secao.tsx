import type { ReactNode } from "react";
import type { Secao as SecaoDaApi } from "../../api/painel";

type Props<T> = {
  titulo: string;
  secao: SecaoDaApi<T>;
  tentando: boolean;
  aoTentarDeNovo: () => void;
  /** Link ou botão no canto do título. */
  acao?: ReactNode;
  className?: string;
  children: (dados: T) => ReactNode;
};

/** Erro de seção: texto fixo e botão que refaz a consulta. Nada vindo do servidor vai para a tela. */
export function ErroDeSecao({ tentando, aoTentarDeNovo }: { tentando: boolean; aoTentarDeNovo: () => void }) {
  return (
    <div role="alert" className="erro-secao">
      <span>Não foi possível carregar</span>
      <button type="button" className="botao-secundario" disabled={tentando} onClick={aoTentarDeNovo}>
        Tentar de novo
      </button>
    </div>
  );
}

export function Esqueleto({ linhas = 3 }: { linhas?: number }) {
  return (
    <div className="esqueleto" aria-hidden="true">
      {Array.from({ length: linhas }, (_, i) => (
        <span key={i} />
      ))}
    </div>
  );
}

/** Moldura de uma seção: título, conteúdo ou erro. */
export function Secao<T>({ titulo, secao, tentando, aoTentarDeNovo, acao, className = "", children }: Props<T>) {
  const id = `secao-${titulo.replace(/\W+/g, "-").toLowerCase()}`;
  return (
    <section className={`cartao ${className}`} aria-labelledby={id}>
      <div className="cartao-cabecalho">
        <h2 id={id} className="cartao-titulo">
          {titulo}
        </h2>
        {acao}
      </div>
      {secao.erro !== null || secao.dados === null ? (
        <ErroDeSecao tentando={tentando} aoTentarDeNovo={aoTentarDeNovo} />
      ) : (
        children(secao.dados)
      )}
    </section>
  );
}
