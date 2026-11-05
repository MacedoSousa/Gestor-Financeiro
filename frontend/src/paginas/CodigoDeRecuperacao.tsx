import { useEffect, useState } from "react";

type Props = { codigo: string; aoConcluir: () => void };

/** O código vive só no estado da mutação: nada em localStorage/sessionStorage. */
export function CodigoDeRecuperacao({ codigo, aoConcluir }: Props) {
  const [guardado, setGuardado] = useState(false);

  useEffect(() => {
    if (guardado) return;
    const avisar = (e: BeforeUnloadEvent) => {
      e.preventDefault();
      e.returnValue = "";
    };
    window.addEventListener("beforeunload", avisar);
    return () => window.removeEventListener("beforeunload", avisar);
  }, [guardado]);

  return (
    <div className="pagina-centro">
      <div className="marca">
        <div className="marca-icone" aria-hidden="true">
          GF
        </div>
        <div style={{ fontWeight: 600, fontSize: 15 }}>Gestor Financeiro</div>
      </div>
      <h1 className="titulo">Cadastro criado</h1>
      <section className="cartao destaque" aria-labelledby="titulo-codigo">
        <h2 id="titulo-codigo" className="cartao-rotulo" style={{ margin: 0 }}>
          Guarde este código em papel
        </h2>
        <p className="codigo-recuperacao num" aria-label="Código de recuperação">
          {codigo.split("-").map((grupo, i) => (
            <span key={i}>{grupo}</span>
          ))}
        </p>
        <p className="cartao-nota">
          É a única forma de abrir os dados se os dois esquecerem a senha. Ele não será mostrado de novo.
        </p>
        <div>
          <button type="button" className="botao-secundario" onClick={() => window.print()}>
            Imprimir
          </button>
        </div>
        <label className="campo-check">
          <input type="checkbox" checked={guardado} onChange={(e) => setGuardado(e.target.checked)} />
          Imprimi e guardei o código
        </label>
        <div>
          <button type="button" className="botao-primario" disabled={!guardado} onClick={aoConcluir}>
            Concluir
          </button>
        </div>
      </section>
    </div>
  );
}
