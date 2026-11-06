import { useState } from "react";
import { hojeIso } from "../api/lancamentos";
import { n } from "../api/formulario";
import { useAcoesDoOrcamento, useOrcamento, type OrcamentoDoMes, type TetoDaCategoria } from "../api/orcamento";
import { formatarReais, textoDeCentavos } from "../lib/dinheiro";
import { lerTeto } from "../lib/orcamento";
import { nomeDoMes, soNomeDoMes, somarMeses } from "../lib/mes";
import { Alerta } from "./Campo";
import { DivisaoDaRenda } from "./orcamento/DivisaoDaRenda";
import { LinhaDoTeto } from "./orcamento/LinhaDoTeto";

const PRIMEIRO_MES = "2020-01";

type Acoes = ReturnType<typeof useAcoesDoOrcamento>;

const mensagemDe = (e: unknown) => (e instanceof Error ? e.message : "Não foi possível salvar. Tente de novo.");

function ReceitaPrevista({ mes, orcamento, acoes }: { mes: string; orcamento: OrcamentoDoMes; acoes: Acoes }) {
  const atual = orcamento.receitaPrevistaCentavos == null ? null : n(orcamento.receitaPrevistaCentavos);
  const sugerida = n(orcamento.receitaSugeridaCentavos);
  const [texto, setTexto] = useState(atual === null ? "" : textoDeCentavos(atual));
  const [erro, setErro] = useState<string>();
  const [ocupado, setOcupado] = useState(false);
  const [tentativa, setTentativa] = useState(0);

  async function salvar(e: React.SyntheticEvent) {
    e.preventDefault();
    setTentativa((t) => t + 1);
    const lido = lerTeto(texto);
    if (lido.tipo === "vazio") return setErro("Informe a receita prevista do mês.");
    if (lido.tipo === "invalido") return setErro(lido.mensagem);
    setOcupado(true);
    setErro(undefined);
    try {
      await acoes.definirReceita(mes, lido.centavos);
      setTexto(textoDeCentavos(lido.centavos));
    } catch (x) {
      setErro(mensagemDe(x));
    } finally {
      setOcupado(false);
    }
  }

  async function remover() {
    setOcupado(true);
    setErro(undefined);
    try {
      await acoes.removerReceita(mes);
      setTexto("");
    } catch (x) {
      setErro(mensagemDe(x));
    } finally {
      setOcupado(false);
    }
  }

  return (
    <section className="cartao" aria-labelledby="titulo-receita">
      <h2 className="cartao-titulo" id="titulo-receita">
        Receita prevista
      </h2>
      <form className="receita-form" onSubmit={(e) => void salvar(e)} noValidate>
        <div className="campo">
          <label className="campo-rotulo" htmlFor="receita-prevista">
            Receita prevista de {nomeDoMes(mes)}
          </label>
          <input
            id="receita-prevista"
            className="entrada num"
            type="text"
            inputMode="decimal"
            autoComplete="off"
            placeholder="0,00"
            value={texto}
            aria-invalid={erro ? true : undefined}
            onChange={(e) => setTexto(e.target.value)}
          />
        </div>
        <button type="submit" className="botao-secundario" disabled={ocupado}>
          {ocupado ? "Salvando…" : "Salvar"}
        </button>
        {atual !== null && (
          <button type="button" className="botao-link" disabled={ocupado} onClick={() => void remover()}>
            Remover receita
          </button>
        )}
      </form>
      {sugerida > 0 && (
        <p className="subtitulo">
          No mês passado entraram <span className="num">{formatarReais(sugerida)}</span>{" "}
          <button type="button" className="botao-link" onClick={() => setTexto(textoDeCentavos(sugerida))}>
            Usar
          </button>
        </p>
      )}
      <Alerta mensagem={erro} tentativa={tentativa} />
    </section>
  );
}

function Grupo({ titulo, nota, categorias, mes, acoes }: { titulo: string; nota?: string; categorias: TetoDaCategoria[]; mes: string; acoes: Acoes }) {
  if (categorias.length === 0) return null;
  return (
    <tbody>
      <tr className="grupo-linha">
        <th colSpan={4} scope="colgroup">
          {titulo}
          {nota && <span className="grupo-nota"> · {nota}</span>}
        </th>
      </tr>
      {categorias.map((c) => (
        <LinhaDoTeto key={`${mes}-${c.categoriaId}`} mes={mes} categoria={c} acoes={acoes} />
      ))}
    </tbody>
  );
}

export function Orcamento() {
  const hoje = hojeIso().slice(0, 7);
  const ultimoMes = somarMeses(hoje, 24);
  const [mes, setMes] = useState(hoje);
  const orcamento = useOrcamento(mes);
  const acoes = useAcoesDoOrcamento();
  const [copiando, setCopiando] = useState(false);
  const [erroCopia, setErroCopia] = useState<string>();
  const [nadaACopiar, setNadaACopiar] = useState<string>();

  const dados = orcamento.data;
  const receita = dados?.receitaPrevistaCentavos == null ? null : n(dados.receitaPrevistaCentavos);
  const categorias = dados?.categorias ?? [];
  const semTetos = dados !== undefined && categorias.every((c) => c.tetoCentavos == null);
  const faltaLinha = categorias.some((c) => c.tetoCentavos == null && !c.arquivada);
  const podeCopiar = dados?.mesParaCopiar != null && faltaLinha;

  function mudarMes(f: (m: string) => string) {
    setMes(f);
    setErroCopia(undefined);
    setNadaACopiar(undefined);
  }

  async function copiar() {
    setCopiando(true);
    setErroCopia(undefined);
    setNadaACopiar(undefined);
    try {
      const r = await acoes.copiarTetos(mes);
      if (r.copiados === 0 && r.origem) setNadaACopiar(`Nada a copiar de ${soNomeDoMes(r.origem)}.`);
    } catch (x) {
      setErroCopia(mensagemDe(x));
    } finally {
      setCopiando(false);
    }
  }

  const rotuloCopia = dados?.mesParaCopiar ? `Copiar de ${soNomeDoMes(dados.mesParaCopiar)}` : "";
  const comPapel = categorias.filter((c) => c.papel != null);
  const gastos = categorias.filter((c) => c.papel == null);

  return (
    <>
      <header className="cabecalho-pagina">
        <div className="cabecalho-titulo">
          <div className="mes-navegacao">
            <button type="button" className="botao-icone" aria-label="Mês anterior" disabled={mes <= PRIMEIRO_MES} onClick={() => mudarMes((m) => somarMeses(m, -1))}>
              ‹
            </button>
            <span className="subtitulo" aria-live="polite">
              {nomeDoMes(mes)}
              {dados && (receita === null ? " · sem receita prevista" : ` · receita prevista ${formatarReais(receita)}`)}
            </span>
            <button type="button" className="botao-icone" aria-label="Próximo mês" disabled={mes >= ultimoMes} onClick={() => mudarMes((m) => somarMeses(m, 1))}>
              ›
            </button>
          </div>
          <h1 className="titulo">Orçamento</h1>
        </div>
        {podeCopiar && !semTetos && (
          <button type="button" className="botao-secundario" disabled={copiando} onClick={() => void copiar()}>
            {copiando ? "Copiando…" : rotuloCopia}
          </button>
        )}
      </header>

      {orcamento.isError && (
        <p role="alert" className="alerta-form">
          {orcamento.error.message}
        </p>
      )}
      {orcamento.isPending && <p className="subtitulo">Carregando…</p>}

      {dados && (
        <>
          {semTetos && dados.mesParaCopiar != null && (
            <section className="cartao destaque" aria-label="Mês sem tetos">
              <p className="aviso-sem-tetos">Este mês ainda não tem tetos.</p>
              <div>
                <button type="button" className="botao-primario" disabled={copiando} onClick={() => void copiar()}>
                  {copiando ? "Copiando…" : rotuloCopia}
                </button>
              </div>
            </section>
          )}
          {erroCopia && (
            <p role="alert" className="alerta-form">
              {erroCopia}
            </p>
          )}

          {nadaACopiar && (
            <p role="status" className="subtitulo">
              {nadaACopiar}
            </p>
          )}

          <ReceitaPrevista key={`${mes}-${receita ?? "sem"}`} mes={mes} orcamento={dados} acoes={acoes} />
          <DivisaoDaRenda orcamento={dados} />

          <section className="cartao-tabela" aria-labelledby="titulo-tetos">
            <div className="cartao-cabecalho">
              <h2 className="cartao-titulo" id="titulo-tetos">
                Teto por categoria
              </h2>
            </div>
            <div className="rolagem-x">
              <table className="tabela tabela-tetos">
                <thead>
                  <tr>
                    <th scope="col">Categoria</th>
                    <th scope="col">Teto</th>
                    <th scope="col" className="coluna-uso">
                      Usado
                    </th>
                    <th scope="col" className="celula-valor">
                      Restante
                    </th>
                  </tr>
                </thead>
                <Grupo titulo="Investir e dívidas" nota="não entram no Teto de gastos" categorias={comPapel} mes={mes} acoes={acoes} />
                <Grupo titulo="Gastos" categorias={gastos} mes={mes} acoes={acoes} />
              </table>
            </div>
          </section>
        </>
      )}
    </>
  );
}
