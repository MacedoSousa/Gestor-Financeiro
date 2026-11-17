import { useRef, useState, type FormEvent } from "react";
import { Link } from "react-router";
import { useQueryClient } from "@tanstack/react-query";
import { ErroDeApi } from "../api/autenticacao";
import { resolverItem, useAConferir, type ItemAConferir } from "../api/aConferir";
import { useContas, type Conta } from "../api/contas";
import { n } from "../api/formulario";
import { useCategorias, type Categoria } from "../api/lancamentos";
import { avisoDaConta, categoriasDaOpcao, contaSugerida, contasDaOpcao, oQuePede, rotuloDaOpcao, textoDaResolucao, tituloDoItem } from "../lib/aConferir";
import { formatarReais } from "../lib/dinheiro";
import { Alerta, CampoSelecao } from "./Campo";

const mensagem = (e: unknown) => (e instanceof ErroDeApi ? e.message : "Não foi possível resolver. Tente de novo.");
const diaEMes = (iso: string) => `${iso.slice(8, 10)}/${iso.slice(5, 7)}`;

/** Um item: escolhe a opção (e a conta ou a categoria, quando ela pede) e resolve. */
function ItemDaCaixa({ item, contas, categorias, aoResolver }: { item: ItemAConferir; contas: Conta[]; categorias: Categoria[]; aoResolver: (texto: string) => void }) {
  const consultas = useQueryClient();
  const [opcao, setOpcao] = useState("");
  const [escolha, setEscolha] = useState("");
  const [falta, setFalta] = useState<string>();
  const [erro, setErro] = useState<string>();
  const [tentativa, setTentativa] = useState(0);
  const [aviso, setAviso] = useState<string>();
  const [ocupado, setOcupado] = useState(false);
  const form = useRef<HTMLFormElement>(null);
  const botao = useRef<HTMLButtonElement>(null);
  const idDoErro = `erro-opcao-${item.id}`;
  const titulo = tituloDoItem(item);
  const valor = n(item.valorCentavos);
  const pede = opcao ? oQuePede(opcao) : "nada";
  const escolhas =
    pede === "conta"
      ? contasDaOpcao(opcao, item.contaId, contas).map((c) => ({ valor: c.id, rotulo: c.apelido }))
      : categoriasDaOpcao(opcao, categorias).map((c) => ({ valor: c.id, rotulo: c.nome }));

  async function resolver(e: FormEvent) {
    e.preventDefault();
    if (ocupado) return;
    if (!opcao) {
      setFalta("Escolha uma opção.");
      form.current?.querySelector<HTMLInputElement>("input[type=radio]")?.focus();
      return;
    }
    if (pede !== "nada" && !escolha) {
      setFalta(pede === "conta" ? "Escolha a outra conta." : "Escolha a categoria.");
      form.current?.querySelector<HTMLSelectElement>("select")?.focus();
      return;
    }
    setOcupado(true);
    setErro(undefined);
    setAviso(undefined);
    try {
      await resolverItem(item.id, opcao, pede === "conta" ? escolha : null, pede === "categoria" ? escolha : null);
      if (opcao === "DEIXAR_PARA_DEPOIS") {
        setAviso(textoDaResolucao(opcao));
        // O item fica: o foco volta ao botão (o fieldset desabilitado durante o envio o tinha tirado).
        requestAnimationFrame(() => botao.current?.focus());
        return;
      }
      aoResolver(textoDaResolucao(opcao));
      await Promise.all(
        ["a-conferir", "lancamentos", "a-revisar", "visao-geral", "saldos"].map((chave) => consultas.invalidateQueries({ queryKey: [chave] })),
      );
    } catch (falha) {
      setErro(mensagem(falha));
      setTentativa((t) => t + 1);
    } finally {
      setOcupado(false);
    }
  }

  return (
    <li className="item-linha grupo-revisao">
      <form ref={form} className="grupo-revisao-form" onSubmit={(e) => void resolver(e)} noValidate aria-label={`${titulo} em ${item.contaApelido}, ${diaEMes(item.data)}`}>
        <div className="item-texto">
          <span style={{ fontWeight: 500 }}>{item.descricao || titulo}</span>
          <span className="cartao-nota">
            {item.descricao ? `${titulo} · ` : ""}
            {item.contaApelido} · <span className="num">{diaEMes(item.data)}</span> ·{" "}
            <span className={`num${valor > 0 ? " valor-positivo" : ""}`}>
              {valor < 0 ? "−" : "+"}
              {formatarReais(Math.abs(valor))}
            </span>
          </span>
        </div>
        <Alerta mensagem={erro} tentativa={tentativa} />
        <p role="status" className={aviso ? "aviso-form" : "sr-only"}>
          {aviso}
        </p>
        <fieldset className="escolha" disabled={ocupado}>
          <legend className="sr-only">
            {titulo}, {item.contaApelido}: como resolver
          </legend>
          <div
            className="opcoes-aplicar"
            role="radiogroup"
            aria-label={`Como resolver: ${titulo}, ${item.contaApelido}, ${diaEMes(item.data)}`}
            aria-invalid={falta && !opcao ? true : undefined}
            aria-describedby={falta && !opcao ? idDoErro : undefined}
          >
            {item.opcoes.map((o) => (
              <label key={o} className="campo-check opcao-slot">
                <input
                  type="radio"
                  name={`opcao-${item.id}`}
                  value={o}
                  checked={opcao === o}
                  onChange={() => {
                    setOpcao(o);
                    setEscolha(contaSugerida(o, item.contaId, contas));
                    setFalta(undefined);
                    setAviso(undefined);
                  }}
                />
                {rotuloDaOpcao(o)}
              </label>
            ))}
          </div>
          {falta && !opcao && (
            <span id={idDoErro} className="campo-erro" role="alert">
              {falta}
            </span>
          )}
          {pede !== "nada" && (
            <CampoSelecao
              rotulo={pede === "conta" ? "Com qual conta" : "Categoria"}
              valor={escolha}
              aoMudar={(v) => {
                setEscolha(v);
                setFalta(undefined);
                setErro(undefined);
              }}
              erro={falta && opcao ? falta : undefined}
              opcoes={[{ valor: "", rotulo: pede === "conta" ? "Escolha a conta" : "Escolha a categoria" }, ...escolhas]}
              nota={
                pede === "conta" && escolhas.length === 0
                  ? "Nenhuma conta serve. Crie uma conta de poupança ou investimento em Contas."
                  : pede === "conta"
                    ? avisoDaConta(opcao, escolha, contas)
                    : undefined
              }
            />
          )}
          <button ref={botao} type="submit" className="botao-secundario" aria-busy={ocupado || undefined}>
            {ocupado ? "Resolvendo…" : "Resolver"}
          </button>
        </fieldset>
      </form>
    </li>
  );
}

/** "A conferir" (Fase 2, D11, Tarefas 6.1 e 6.2): o que o sistema não decidiu sozinho. Nada é descartado. */
export function AConferir() {
  const itens = useAConferir();
  const contas = useContas();
  const categorias = useCategorias();
  const [aviso, setAviso] = useState<string>();
  const titulo = useRef<HTMLHeadingElement>(null);
  // Resolvido, o item sai da lista: o foco vai para o título, em vez de cair no topo da página.
  const aoResolver = (texto: string) => {
    setAviso(texto);
    requestAnimationFrame(() => titulo.current?.focus());
  };
  const carregando = itens.isPending || contas.isPending || categorias.isPending;
  const lista = itens.data ?? [];

  return (
    <>
      <header className="cabecalho-pagina">
        <div className="cabecalho-titulo">
          <span className="subtitulo">
            <Link to="/lancamentos">Lançamentos</Link> · o que o sistema não decidiu sozinho
          </span>
          <h1 className="titulo">A conferir</h1>
        </div>
      </header>

      <p role="status" className={aviso ? "aviso-form" : "sr-only"}>
        {aviso}
      </p>

      <div className="duas-colunas">
        <section className="cartao coluna-principal" aria-labelledby="titulo-a-conferir">
          <h2 id="titulo-a-conferir" className="cartao-titulo" tabIndex={-1} ref={titulo}>
            {lista.length > 0 ? `${lista.length} ${lista.length === 1 ? "item" : "itens"}` : "Itens"}
          </h2>
          {carregando && <p className="cartao-nota">Carregando…</p>}
          {itens.isError && <p className="alerta-form" role="alert">{itens.error.message}</p>}
          {contas.isError && <p className="alerta-form" role="alert">{contas.error.message}</p>}
          {categorias.isError && <p className="alerta-form" role="alert">{categorias.error.message}</p>}
          {!carregando && !itens.isError && lista.length === 0 && (
            <p className="cartao-nota">
              Nada a conferir agora. <Link to="/lancamentos">Ver os lançamentos</Link>
            </p>
          )}
          {contas.data && categorias.data && lista.length > 0 && (
            <ul className="lista-itens">
              {lista.map((i) => (
                <ItemDaCaixa key={i.id} item={i} contas={contas.data} categorias={categorias.data} aoResolver={aoResolver} />
              ))}
            </ul>
          )}
        </section>

        <aside className="coluna-lateral">
          <section className="cartao" aria-labelledby="titulo-como-funciona">
            <h2 id="titulo-como-funciona" className="cartao-titulo">
              Como funciona
            </h2>
            <p className="cartao-nota" style={{ margin: 0 }}>
              Transferência entre as contas de vocês e pagamento de fatura são reconhecidos sozinhos quando o outro lado chega do banco. O
              que fica sem par, a aplicação e o resgate vêm para cá.
            </p>
            <p className="cartao-nota" style={{ margin: 0 }}>
              Transferência, aporte e resgate não contam no orçamento. Nada é apagado: o que você resolve sai daqui e fica guardado.
            </p>
          </section>
        </aside>
      </div>
    </>
  );
}
