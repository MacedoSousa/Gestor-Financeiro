import { useRef, useState, type FormEvent } from "react";
import { Link } from "react-router";
import { useQueryClient } from "@tanstack/react-query";
import { ErroDeApi } from "../api/autenticacao";
import { n } from "../api/formulario";
import { useCategorias, type Categoria } from "../api/lancamentos";
import { apontarCategoriaDoBanco, criarCategoria, revisarLote, useARevisar, useMapaDoBanco, type CategoriaExterna, type GrupoARevisar } from "../api/revisao";
import { formatarReais } from "../lib/dinheiro";
import { categoriaInicial, categoriasDoGrupo, opcoesDeAplicar, resumoDoGrupo, textoDaRevisao, type Aplicar } from "../lib/revisao";
import { Alerta, Campo, CampoSelecao } from "./Campo";

const mensagem = (e: unknown) => (e instanceof ErroDeApi ? e.message : "Não foi possível salvar. Tente de novo.");

/** Um grupo de lançamentos a revisar: escolhe a categoria e como aplicar, e revisa o grupo inteiro de uma vez. */
function GrupoDaRevisao({ grupo, categorias, aoRevisar }: { grupo: GrupoARevisar; categorias: Categoria[]; aoRevisar: (texto: string) => void }) {
  const consultas = useQueryClient();
  const [categoria, setCategoria] = useState(() => categoriaInicial(grupo, categorias));
  const [aplicar, setAplicar] = useState<Aplicar>("SO_ESTA");
  const [erro, setErro] = useState<string>();
  const [semCategoria, setSemCategoria] = useState(false);
  const [tentativa, setTentativa] = useState(0);
  const [ocupado, setOcupado] = useState(false);
  const form = useRef<HTMLFormElement>(null);
  const opcoes = categoriasDoGrupo(grupo.natureza, categorias);
  const natureza = grupo.natureza === "RECEITA" ? "receita" : "despesa";
  const total = n(grupo.totalCentavos);
  const quantidade = Number(grupo.quantidade);

  async function revisar(e: FormEvent) {
    e.preventDefault();
    if (!categoria) {
      // O erro fica no próprio campo (aria-invalid), e o foco vai para ele.
      setSemCategoria(true);
      form.current?.querySelector<HTMLSelectElement>("select")?.focus();
      return;
    }
    setOcupado(true);
    setErro(undefined);
    try {
      await revisarLote(grupo.ids, categoria, aplicar, grupo.categoriaDoBanco ?? null);
      aoRevisar(textoDaRevisao(quantidade));
      await Promise.all(
        ["a-revisar", "lancamentos", "categorias-externas", "visao-geral"].map((chave) => consultas.invalidateQueries({ queryKey: [chave] })),
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
      <form
        ref={form}
        className="grupo-revisao-form"
        onSubmit={(e) => void revisar(e)}
        noValidate
        aria-label={`Revisar ${grupo.descricao} (${natureza})`}
      >
        <div className="item-texto">
          <span style={{ fontWeight: 500 }}>{grupo.descricao}</span>
          <span className="cartao-nota">
            {resumoDoGrupo(grupo)} ·{" "}
            <span className={`num${total > 0 ? " valor-positivo" : ""}`}>
              {total < 0 ? "−" : "+"}
              {formatarReais(Math.abs(total))}
            </span>
          </span>
        </div>
        <Alerta mensagem={erro} tentativa={tentativa} />
        <fieldset className="escolha" disabled={ocupado}>
          <legend className="sr-only">Como revisar {grupo.descricao}</legend>
          <CampoSelecao
            rotulo="Categoria"
            valor={categoria}
            aoMudar={(v) => {
              setCategoria(v);
              setSemCategoria(false);
            }}
            erro={semCategoria ? "Escolha a categoria." : undefined}
            opcoes={[{ valor: "", rotulo: "Escolha a categoria" }, ...opcoes.map((c) => ({ valor: c.id, rotulo: c.nome }))]}
            nota={grupo.categoriaSugeridaId && categoria === grupo.categoriaSugeridaId ? "Sugerida pela regra ou pela categoria do banco." : undefined}
          />
          <div className="opcoes-aplicar" role="radiogroup" aria-label={`Como aplicar a ${grupo.descricao} (${natureza})`}>
            {opcoesDeAplicar(grupo).map((o) => (
              <label key={o.valor} className="campo-check opcao-slot">
                <input type="radio" name={`aplicar-${grupo.ids[0]}`} value={o.valor} checked={aplicar === o.valor} onChange={() => setAplicar(o.valor)} />
                {o.rotulo}
              </label>
            ))}
          </div>
          <button type="submit" className="botao-secundario">
            {ocupado ? "Revisando…" : quantidade === 1 ? "Revisar" : `Revisar os ${quantidade}`}
          </button>
        </fieldset>
      </form>
    </li>
  );
}

/** Nova categoria sem sair da tela (usa o POST /categorias de sempre). */
function NovaCategoria() {
  const consultas = useQueryClient();
  const [nome, setNome] = useState("");
  const [tipo, setTipo] = useState<"DESPESA" | "RECEITA">("DESPESA");
  const [erro, setErro] = useState<string>();
  const [tentativa, setTentativa] = useState(0);
  const [aviso, setAviso] = useState<string>();
  const [ocupado, setOcupado] = useState(false);

  async function criar(e: FormEvent) {
    e.preventDefault();
    setOcupado(true);
    setErro(undefined);
    setAviso(undefined);
    try {
      await criarCategoria(nome.trim(), tipo);
      setAviso(`Categoria “${nome.trim()}” criada.`);
      setNome("");
      await consultas.invalidateQueries({ queryKey: ["categorias"] });
    } catch (falha) {
      setErro(mensagem(falha));
      setTentativa((t) => t + 1);
    } finally {
      setOcupado(false);
    }
  }

  return (
    <form className="cartao" onSubmit={(e) => void criar(e)} noValidate aria-labelledby="titulo-nova-categoria">
      <h2 id="titulo-nova-categoria" className="cartao-titulo">
        Nova categoria
      </h2>
      <Alerta mensagem={erro} tentativa={tentativa} />
      <p role="status" className={aviso ? "aviso-form" : "sr-only"}>
        {aviso}
      </p>
      <Campo rotulo="Nome" valor={nome} aoMudar={setNome} maxLength={40} autoComplete="off" />
      <CampoSelecao
        rotulo="Tipo"
        valor={tipo}
        aoMudar={(v) => setTipo(v === "RECEITA" ? "RECEITA" : "DESPESA")}
        opcoes={[
          { valor: "DESPESA", rotulo: "Despesa" },
          { valor: "RECEITA", rotulo: "Receita" },
        ]}
      />
      <button type="submit" className="botao-secundario" disabled={ocupado}>
        {ocupado ? "Criando…" : "Criar categoria"}
      </button>
    </form>
  );
}

/** Uma linha do mapa "sempre que o banco disser": troca a categoria do casal (decisão 2). */
function LinhaDoMapa({ entrada, categorias }: { entrada: CategoriaExterna; categorias: Categoria[] }) {
  const consultas = useQueryClient();
  const [categoria, setCategoria] = useState(entrada.categoriaId);
  const [erro, setErro] = useState<string>();
  const [tentativa, setTentativa] = useState(0);
  const [aviso, setAviso] = useState<string>();
  const [ocupado, setOcupado] = useState(false);
  const nome = entrada.nome ?? entrada.categoriaExterna;

  async function salvar(e: FormEvent) {
    e.preventDefault();
    // O botão não se desabilita (o foco ficaria perdido): o envio repetido é ignorado aqui.
    if (ocupado) return;
    if (categoria === entrada.categoriaId) {
      setAviso("Nada mudou.");
      return;
    }
    setOcupado(true);
    setErro(undefined);
    setAviso(undefined);
    try {
      await apontarCategoriaDoBanco(entrada.id, categoria);
      setAviso("Salvo. Vale para o que vier do banco daqui em diante.");
      await consultas.invalidateQueries({ queryKey: ["categorias-externas"] });
    } catch (falha) {
      setErro(mensagem(falha));
      setTentativa((t) => t + 1);
    } finally {
      setOcupado(false);
    }
  }

  return (
    <li className="item-linha">
      <form className="grupo-revisao-form" onSubmit={(e) => void salvar(e)} noValidate aria-label={`Banco diz ${nome}`}>
        <Alerta mensagem={erro} tentativa={tentativa} />
        <p role="status" className={aviso ? "aviso-form" : "sr-only"}>
          {aviso}
        </p>
        <CampoSelecao
          rotulo={`Quando o banco disser “${nome}”`}
          valor={categoria}
          aoMudar={setCategoria}
          opcoes={categorias.filter((c) => !c.arquivada || c.id === entrada.categoriaId).map((c) => ({ valor: c.id, rotulo: c.nome }))}
        />
        <button type="submit" className="botao-secundario" aria-busy={ocupado || undefined}>
          {ocupado ? "Salvando…" : "Salvar"}
        </button>
      </form>
    </li>
  );
}

/** "Revisar importados" (Fase 2, Tarefa 5.2): o que veio do banco, agrupado, entra no orçamento quando o casal revisa. */
export function RevisarImportados() {
  const grupos = useARevisar();
  const categorias = useCategorias();
  const mapa = useMapaDoBanco();
  const [aviso, setAviso] = useState<string>();
  const titulo = useRef<HTMLHeadingElement>(null);
  // Revisado, o grupo some: o foco vai para o título da lista, em vez de cair no topo da página.
  const aoRevisar = (texto: string) => {
    setAviso(texto);
    requestAnimationFrame(() => titulo.current?.focus());
  };
  const carregando = grupos.isPending || categorias.isPending;
  const lista = grupos.data ?? [];

  return (
    <>
      <header className="cabecalho-pagina">
        <div className="cabecalho-titulo">
          <span className="subtitulo">
            <Link to="/lancamentos">Lançamentos</Link> · o que veio do banco só entra no orçamento depois de revisado
          </span>
          <h1 className="titulo">Revisar importados</h1>
        </div>
      </header>

      <p role="status" className={aviso ? "aviso-form" : "sr-only"}>
        {aviso}
      </p>

      <div className="duas-colunas">
        <section className="cartao coluna-principal" aria-labelledby="titulo-a-revisar">
          <h2 id="titulo-a-revisar" className="cartao-titulo" tabIndex={-1} ref={titulo}>
            A revisar
          </h2>
          {carregando && <p className="cartao-nota">Carregando…</p>}
          {grupos.isError && <p className="alerta-form">{grupos.error.message}</p>}
          {categorias.isError && <p className="alerta-form">{categorias.error.message}</p>}
          {!carregando && !grupos.isError && lista.length === 0 && (
            <p className="cartao-nota">
              Nada a revisar: tudo o que veio do banco já foi revisado. <Link to="/lancamentos">Ver os lançamentos</Link>
            </p>
          )}
          {categorias.data && lista.length > 0 && (
            <ul className="lista-itens">
              {lista.map((g) => (
                <GrupoDaRevisao key={g.ids.join(",")} grupo={g} categorias={categorias.data} aoRevisar={aoRevisar} />
              ))}
            </ul>
          )}
        </section>

        <aside className="coluna-lateral">
          <NovaCategoria />
          <section className="cartao" aria-labelledby="titulo-mapa">
            <h2 id="titulo-mapa" className="cartao-titulo">
              Categorias do banco
            </h2>
            <p className="cartao-nota" style={{ margin: 0 }}>
              O que você marcou com “sempre que o banco disser”. Só sugere para o que vier do banco daqui em diante; não muda o
              que já está na lista.
            </p>
            {mapa.isError && <p className="alerta-form">{mapa.error.message}</p>}
            {mapa.data && mapa.data.length === 0 && <p className="cartao-nota">Nenhuma ainda.</p>}
            {mapa.data && mapa.data.length > 0 && categorias.data && (
              <ul className="lista-itens">
                {mapa.data.map((m) => (
                  <LinhaDoMapa key={m.id} entrada={m} categorias={categorias.data} />
                ))}
              </ul>
            )}
          </section>
        </aside>
      </div>
    </>
  );
}
