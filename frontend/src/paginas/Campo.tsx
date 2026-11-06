import { useEffect, useId, useRef, type ReactNode, type RefObject } from "react";

type CampoProps = {
  rotulo: string;
  valor: string;
  aoMudar: (v: string) => void;
  erro?: string;
  nota?: string;
  tipo?: string;
  autoComplete?: string;
  inputMode?: "numeric";
  maxLength?: number;
};

export function Campo({ rotulo, valor, aoMudar, erro, nota, tipo = "text", autoComplete, inputMode, maxLength }: CampoProps) {
  const id = useId();
  const descricao = [nota ? `${id}-nota` : null, erro ? `${id}-erro` : null].filter(Boolean).join(" ");
  return (
    <div className="campo">
      <label className="campo-rotulo" htmlFor={id}>
        {rotulo}
      </label>
      <input
        id={id}
        className="entrada"
        type={tipo}
        value={valor}
        autoComplete={autoComplete}
        inputMode={inputMode}
        maxLength={maxLength}
        aria-invalid={erro ? true : undefined}
        aria-describedby={descricao || undefined}
        onChange={(e) => aoMudar(e.target.value)}
      />
      {nota && (
        <span id={`${id}-nota`} className="cartao-nota">
          {nota}
        </span>
      )}
      {erro && (
        <span id={`${id}-erro`} className="campo-erro">
          {erro}
        </span>
      )}
    </div>
  );
}

/** Leva o foco ao alerta quando uma mensagem nova aparece. */
export function useFocoNoAlerta(mensagem: string | undefined, ref: RefObject<HTMLElement | null>, tentativa?: number) {
  useEffect(() => {
    if (mensagem) ref.current?.focus();
  }, [mensagem, ref, tentativa]);
}

/** Mensagem de erro de formulário; o foco vai para ela quando aparece. */
export function Alerta({ mensagem, tentativa }: { mensagem?: string; tentativa?: number }) {
  const ref = useRef<HTMLParagraphElement>(null);
  // `tentativa` muda a cada envio: a mesma mensagem repetida volta a receber o foco.
  useFocoNoAlerta(mensagem, ref, tentativa);
  if (!mensagem) return null;
  return (
    <p role="alert" className="alerta-form" tabIndex={-1} ref={ref}>
      {mensagem}
    </p>
  );
}

/** Moldura das telas públicas (sem barra lateral). */
export function PaginaPublica({ titulo, subtitulo, children }: { titulo: string; subtitulo?: string; children: ReactNode }) {
  return (
    <main className="pagina-centro estreita">
      <div className="marca">
        <div className="marca-icone" aria-hidden="true">
          GF
        </div>
        <div style={{ fontWeight: 600, fontSize: 15 }}>Gestor Financeiro</div>
      </div>
      <header style={{ display: "flex", flexDirection: "column", gap: 6 }}>
        <h1 className="titulo">{titulo}</h1>
        {subtitulo && <span className="subtitulo">{subtitulo}</span>}
      </header>
      {children}
    </main>
  );
}
