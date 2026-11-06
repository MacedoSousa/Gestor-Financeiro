import { useRef, useState, type KeyboardEvent } from "react";
import { n } from "../../api/formulario";
import type { TetoDaCategoria } from "../../api/orcamento";
import { formatarReais, textoDeCentavos } from "../../lib/dinheiro";
import { lerTeto } from "../../lib/orcamento";

type Acoes = {
  definirTeto: (mes: string, categoriaId: string, centavos: number) => Promise<unknown>;
  removerTeto: (mes: string, categoriaId: string) => Promise<unknown>;
};

const textoDoServidor = (c: TetoDaCategoria) => (c.tetoCentavos == null ? "" : textoDeCentavos(n(c.tetoCentavos)));

function Restante({ c }: { c: TetoDaCategoria }) {
  if (c.restanteCentavos == null) return <td className="num celula-valor texto-fraco">—</td>;
  const restante = n(c.restanteCentavos);
  if (restante < 0) {
    return <td className="num celula-valor restante-acima">{formatarReais(Math.abs(restante))} acima do teto</td>;
  }
  return <td className="num celula-valor">{formatarReais(restante)}</td>;
}

function Uso({ c }: { c: TetoDaCategoria }) {
  const teto = c.tetoCentavos == null ? null : n(c.tetoCentavos);
  const usado = n(c.usadoCentavos);
  const razao = teto === null ? 0 : teto > 0 ? usado / teto : usado > 0 ? 1 : 0;
  const largura = Math.min(Math.max(razao, 0), 1) * 100;
  const classe = razao > 1 ? "uso-acima" : razao >= 0.9 ? "uso-perto" : "";
  return (
    <td>
      <div className="uso">
        <div className="uso-trilho" aria-hidden="true">
          <div className={`uso-barra ${classe}`} style={{ width: `${largura}%` }} />
        </div>
        <span className="num">{formatarReais(usado)}</span>
      </div>
    </td>
  );
}

/**
 * Uma categoria com o campo de teto. O texto digitado fica no estado da linha e só volta ao valor do servidor
 * depois de salvar com sucesso ou com Esc; um erro de salvar mantém o que foi digitado.
 */
export function LinhaDoTeto({ mes, categoria, acoes }: { mes: string; categoria: TetoDaCategoria; acoes: Acoes }) {
  const [texto, setTexto] = useState(() => textoDoServidor(categoria));
  const [status, setStatus] = useState<"ocioso" | "salvando" | "salvo">("ocioso");
  const [erro, setErro] = useState<string>();
  const fila = useRef<Promise<void>>(Promise.resolve());
  const saindoComEsc = useRef(false);
  // Mudança vinda de fora (copiar tetos, outra aba): acompanha o servidor se a linha não tem digitação própria.
  const doServidor = textoDoServidor(categoria);
  const [vistoDoServidor, setVistoDoServidor] = useState(doServidor);
  if (doServidor !== vistoDoServidor) {
    setVistoDoServidor(doServidor);
    if (!erro && texto === vistoDoServidor) setTexto(doServidor);
  }
  const idErro = `erro-teto-${categoria.categoriaId}`;
  const semTeto = categoria.tetoCentavos == null;
  const desabilitado = categoria.arquivada && semTeto;

  // Ações da linha rodam uma depois da outra; nenhuma é descartada (a última a ser pedida vale por último).
  function executar(acao: () => Promise<unknown>, textoDepois: string) {
    fila.current = fila.current.then(async () => {
      setStatus("salvando");
      setErro(undefined);
      try {
        await acao();
        setTexto(textoDepois);
        setStatus("salvo");
      } catch (e) {
        setStatus("ocioso");
        setErro(e instanceof Error ? e.message : "Não foi possível salvar. Tente de novo.");
      }
    });
  }

  function aoSair() {
    if (saindoComEsc.current) {
      saindoComEsc.current = false;
      return;
    }
    const lido = lerTeto(texto);
    if (lido.tipo === "vazio") {
      // Esvaziar o campo não remove o teto: volta ao valor anterior.
      setTexto(textoDoServidor(categoria));
      setErro(undefined);
      return;
    }
    if (lido.tipo === "invalido") {
      setErro(lido.mensagem);
      return;
    }
    if (!erro && !semTeto && lido.centavos === n(categoria.tetoCentavos)) {
      setTexto(textoDoServidor(categoria));
      return;
    }
    executar(() => acoes.definirTeto(mes, categoria.categoriaId, lido.centavos), textoDeCentavos(lido.centavos));
  }

  function aoTeclar(e: KeyboardEvent<HTMLInputElement>) {
    if (e.key === "Enter") {
      e.preventDefault();
      e.currentTarget.blur();
    } else if (e.key === "Escape") {
      e.preventDefault();
      saindoComEsc.current = true;
      setTexto(textoDoServidor(categoria));
      setErro(undefined);
      e.currentTarget.blur();
    }
  }

  return (
    <tr>
      <td>
        <span className="linha-nome">{categoria.nome}</span>
        {categoria.arquivada && <span className="texto-fraco"> (arquivada)</span>}
      </td>
      <td>
        <div className="celula-teto">
          <label className="sr-only" htmlFor={`teto-${categoria.categoriaId}`}>
            Teto {categoria.nome}
          </label>
          <input
            id={`teto-${categoria.categoriaId}`}
            className="entrada entrada-teto num"
            type="text"
            inputMode="decimal"
            autoComplete="off"
            aria-invalid={erro ? true : undefined}
            aria-describedby={erro ? idErro : undefined}
            disabled={desabilitado}
            placeholder={desabilitado ? "" : "sem teto"}
            value={texto}
            onChange={(e) => setTexto(e.target.value)}
            onBlur={aoSair}
            onKeyDown={aoTeclar}
          />
          {!semTeto && (
            <button
              type="button"
              className="botao-link"
              aria-label={`Remover teto ${categoria.nome}`}
              onMouseDown={(e) => e.preventDefault()}
              onClick={() => executar(() => acoes.removerTeto(mes, categoria.categoriaId), "")}
            >
              Remover teto
            </button>
          )}
        </div>
        <span className="status-linha" aria-live="polite">
          {status === "salvando" && <span className="texto-fraco">Salvando…</span>}
          {status === "salvo" && <span className="texto-fraco">Salvo</span>}
          {erro && (
            <span id={idErro} className="campo-erro">
              {erro}
            </span>
          )}
        </span>
      </td>
      <Uso c={categoria} />
      <Restante c={categoria} />
    </tr>
  );
}
