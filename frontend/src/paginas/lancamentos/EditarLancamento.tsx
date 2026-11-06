import { useEffect, useRef, useState, type FormEvent } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { ErroDeApi } from "../../api/autenticacao";
import { n } from "../../api/formulario";
import { apagarLancamento, editarLancamento, type Categoria, type Escopo, type Lancamento } from "../../api/lancamentos";
import { centavosDeTexto, textoDeCentavos } from "../../lib/dinheiro";
import { Alerta, Campo, CampoSelecao } from "../Campo";

type Props = { lancamento: Lancamento; categorias: Categoria[]; aoFechar: () => void };

/** Editor de um lançamento, num &lt;dialog&gt;. Parcelado pergunta o escopo; transferência muda as duas contas. */
export function EditarLancamento({ lancamento: l, categorias, aoFechar }: Props) {
  const ref = useRef<HTMLDialogElement>(null);
  const consultas = useQueryClient();
  const transferencia = l.natureza === "TRANSFERENCIA";
  const parcelado = l.parcela != null && l.parcelas != null;

  const [natureza, setNatureza] = useState(l.natureza);
  const [valor, setValor] = useState(() => textoDeCentavos(n(l.valorCentavos)));
  const [descricao, setDescricao] = useState(l.descricao);
  const [contraparte, setContraparte] = useState(l.contraparte ?? "");
  const [categoriaId, setCategoriaId] = useState(l.categoria?.id ?? "");
  const [data, setData] = useState(l.data);
  const [escopo, setEscopo] = useState<Escopo>("este");
  const [confirmandoApagar, setConfirmandoApagar] = useState(false);
  const [enviando, setEnviando] = useState(false);
  const [erro, setErro] = useState<string>();
  const [tentativa, setTentativa] = useState(0);

  useEffect(() => {
    const d = ref.current;
    if (d && !d.open) d.showModal();
  }, []);

  // A categoria atual continua na lista mesmo arquivada; as outras, só as ativas do tipo da natureza.
  const opcoesDeCategoria = categorias
    .filter((c) => c.tipo === natureza && (!c.arquivada || c.id === l.categoria?.id))
    .map((c) => ({ valor: c.id, rotulo: c.arquivada ? `${c.nome} (arquivada)` : c.nome }));
  const categoria = opcoesDeCategoria.some((o) => o.valor === categoriaId) ? categoriaId : (opcoesDeCategoria[0]?.valor ?? "");

  function falhar(e: unknown) {
    if (e instanceof ErroDeApi && e.status === 401) void consultas.invalidateQueries({ queryKey: ["eu"] });
    setErro(e instanceof ErroDeApi ? e.message : "Não foi possível concluir. Tente de novo.");
    setTentativa((t) => t + 1);
  }

  async function concluir(acao: () => Promise<unknown>) {
    setErro(undefined);
    setEnviando(true);
    try {
      await acao();
      await Promise.all([
        consultas.invalidateQueries({ queryKey: ["lancamentos"] }),
        consultas.invalidateQueries({ queryKey: ["saldos"] }),
      ]);
      aoFechar();
    } catch (e) {
      falhar(e);
      setEnviando(false);
    }
  }

  function salvar(e: FormEvent) {
    e.preventDefault();
    const centavos = centavosDeTexto(valor);
    if (!centavos) return falhar(new ErroDeApi(400, "Informe um valor maior que zero, como 142,50."));
    if (!descricao.trim()) return falhar(new ErroDeApi(400, "Informe a descrição."));
    const corpo = transferencia
      ? { natureza: "TRANSFERENCIA", valorCentavos: centavos, data, descricao: descricao.trim() }
      : {
          natureza,
          valorCentavos: centavos,
          data,
          descricao: descricao.trim(),
          contraparte: contraparte.trim() || null,
          categoriaId: categoria || null,
        };
    void concluir(() => editarLancamento(l.id, escopo, corpo));
  }

  return (
    <dialog ref={ref} className="dialogo" aria-labelledby="titulo-editar" onCancel={aoFechar}>
      <h2 id="titulo-editar" className="dialogo-titulo">
        {transferencia ? "Editar transferência" : "Editar lançamento"}
      </h2>
      {transferencia && <p className="cartao-nota">A mudança vale para as duas contas da transferência.</p>}
      <Alerta mensagem={erro} tentativa={tentativa} />
      <form className="bloco-form" onSubmit={salvar} noValidate>
        {!transferencia && !parcelado && (
          <CampoSelecao
            rotulo="Tipo"
            valor={natureza}
            aoMudar={setNatureza}
            opcoes={[
              { valor: "DESPESA", rotulo: "Despesa" },
              { valor: "RECEITA", rotulo: "Receita" },
            ]}
          />
        )}
        <Campo
          rotulo={parcelado ? "Valor da parcela" : "Valor"}
          valor={valor}
          aoMudar={setValor}
          inputMode="decimal"
          classeEntrada="num"
          autoComplete="off"
        />
        <Campo rotulo="Descrição" valor={descricao} aoMudar={setDescricao} maxLength={140} autoComplete="off" />
        {!transferencia && (
          <>
            <Campo rotulo="Contraparte (opcional)" valor={contraparte} aoMudar={setContraparte} maxLength={140} autoComplete="off" />
            <CampoSelecao rotulo="Categoria" valor={categoria} aoMudar={setCategoriaId} opcoes={opcoesDeCategoria} />
          </>
        )}
        <Campo rotulo="Data" tipo="date" valor={data} aoMudar={setData} />
        {parcelado && (
          <fieldset className="escolha">
            <legend className="campo-rotulo">
              Parcela {l.parcela}/{l.parcelas}: aplicar a
            </legend>
            <label className="campo-check">
              <input type="radio" name="escopo" checked={escopo === "este"} onChange={() => setEscopo("este")} />
              Só esta parcela
            </label>
            <label className="campo-check">
              <input type="radio" name="escopo" checked={escopo === "proximos"} onChange={() => setEscopo("proximos")} />
              Esta e as próximas
            </label>
          </fieldset>
        )}
        {!confirmandoApagar && (
          <div className="acoes-linha">
            <button type="submit" className="botao-primario" disabled={enviando}>
              {enviando ? "Salvando…" : "Salvar"}
            </button>
            <button type="button" className="botao-secundario" onClick={aoFechar}>
              Cancelar
            </button>
            <button type="button" className="botao-perigo" disabled={enviando} onClick={() => setConfirmandoApagar(true)}>
              Apagar
            </button>
          </div>
        )}
      </form>
      {confirmandoApagar && (
        <div className="confirmacao" role="group" aria-labelledby="titulo-apagar">
          <p id="titulo-apagar" className="confirmacao-texto">
            {transferencia
              ? "Apagar a transferência nas duas contas?"
              : parcelado && escopo === "proximos"
                ? "Apagar esta parcela e as próximas?"
                : "Apagar este lançamento?"}
          </p>
          <div className="acoes-linha">
            <button
              type="button"
              className="botao-perigo"
              disabled={enviando}
              onClick={() => void concluir(() => apagarLancamento(l.id, escopo))}
            >
              {enviando ? "Apagando…" : "Apagar"}
            </button>
            <button type="button" className="botao-secundario" disabled={enviando} onClick={() => setConfirmandoApagar(false)}>
              Voltar
            </button>
          </div>
        </div>
      )}
    </dialog>
  );
}
