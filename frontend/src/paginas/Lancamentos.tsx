import { useEffect, useState } from "react";
import { Link } from "react-router";
import { useEu } from "../api/autenticacao";
import { useContas } from "../api/contas";
import { n } from "../api/formulario";
import { hojeIso, useCategorias, useExtrato, type FiltroDoExtrato, type Lancamento } from "../api/lancamentos";
import { formatarReais } from "../lib/dinheiro";
import { contasParaLancar, edicaoEmContaLigada } from "../lib/lancamentos";
import { nomeDoMes, somarMeses } from "../lib/mes";
import { EditarLancamento } from "./lancamentos/EditarLancamento";
import { LancamentoRapido } from "./lancamentos/LancamentoRapido";

type Titular = FiltroDoExtrato["titular"];

const ORIGENS: Record<string, { rotulo: string; classe: string }> = {
  MANUAL: { rotulo: "Manual", classe: "selo-manual" },
  OPEN_FINANCE: { rotulo: "Open Finance", classe: "selo-open-finance" },
  WHATSAPP: { rotulo: "WhatsApp", classe: "selo-whatsapp" },
  SISTEMA: { rotulo: "Sistema", classe: "selo-manual" },
};

const diaEMes = (iso: string) => `${iso.slice(8, 10)}/${iso.slice(5, 7)}`;

function Valor({ l }: { l: Lancamento }) {
  const centavos = n(l.valorCentavos);
  const sinal = centavos < 0 ? "−" : "+";
  const classe = l.natureza === "TRANSFERENCIA" ? "valor-neutro" : centavos > 0 ? "valor-positivo" : "";
  return (
    <td className={`num celula-valor ${classe}`}>
      {sinal}
      {formatarReais(Math.abs(centavos))}
    </td>
  );
}

function Categoria({ l }: { l: Lancamento }) {
  if (l.natureza === "TRANSFERENCIA") return <td className="texto-fraco">Transferência</td>;
  // Veio do banco e o casal ainda não conferiu: fora do orçamento até revisar (D14).
  if (l.revisao === "A_REVISAR")
    return (
      <td>
        <span className="tom-alerta sem-quebra">A revisar</span>
        {l.categoria && <span className="texto-fraco"> · {l.categoria.nome}?</span>}
      </td>
    );
  if (!l.categoria) return <td className="texto-fraco">—</td>;
  return (
    <td className="texto-2">
      {l.categoria.nome}
      {l.categoria.arquivada && <span className="texto-fraco"> (arquivada)</span>}
    </td>
  );
}

export function Lancamentos() {
  const eu = useEu();
  const contas = useContas();
  const categorias = useCategorias();
  const [mes, setMes] = useState(() => hojeIso().slice(0, 7));
  const [titular, setTitular] = useState<Titular>();
  const [origem, setOrigem] = useState<"MANUAL" | "OPEN_FINANCE">();
  const [busca, setBusca] = useState("");
  const [q, setQ] = useState("");
  const [editando, setEditando] = useState<Lancamento | null>(null);

  // Espera a pessoa parar de digitar antes de buscar.
  useEffect(() => {
    const t = setTimeout(() => setQ(busca.trim()), 300);
    return () => clearTimeout(t);
  }, [busca]);

  const extrato = useExtrato({ mes, titular, origem, q });
  const alternarOrigem = (o: "MANUAL" | "OPEN_FINANCE") => setOrigem((atual) => (atual === o ? undefined : o));
  const total = extrato.data ? n(extrato.data.total) : null;
  const filtrosDeTitular: ReadonlyArray<{ valor: Titular; rotulo: string }> = [
    { valor: undefined, rotulo: "Todos" },
    { valor: "eu", rotulo: eu.data?.apelido ?? "Eu" },
    { valor: "conjuge", rotulo: eu.data?.apelidoDoConjuge || "Cônjuge" },
    { valor: "conjunta", rotulo: "Conjunta" },
  ];

  return (
    <>
      <header className="cabecalho-pagina">
        <div className="cabecalho-titulo">
          <div className="mes-navegacao">
            <button type="button" className="botao-icone" aria-label="Mês anterior" onClick={() => setMes((m) => somarMeses(m, -1))}>
              ‹
            </button>
            <span className="subtitulo" aria-live="polite">
              {nomeDoMes(mes)}
              {total !== null && ` · ${total} ${total === 1 ? "lançamento" : "lançamentos"}`}
            </span>
            <button type="button" className="botao-icone" aria-label="Próximo mês" onClick={() => setMes((m) => somarMeses(m, 1))}>
              ›
            </button>
          </div>
          <h1 className="titulo">Lançamentos</h1>
        </div>
        <div className="filtros" role="group" aria-label="Filtros">
          {filtrosDeTitular.map((f) => (
            <button key={f.rotulo} type="button" className="filtro" aria-pressed={titular === f.valor} onClick={() => setTitular(f.valor)}>
              {f.rotulo}
            </button>
          ))}
          <span className="filtro-divisor" aria-hidden="true" />
          <button type="button" className="filtro" aria-pressed={origem === "OPEN_FINANCE"} onClick={() => alternarOrigem("OPEN_FINANCE")}>
            Open Finance
          </button>
          <button type="button" className="filtro" disabled title="Em breve">
            WhatsApp
          </button>
          <button type="button" className="filtro" aria-pressed={origem === "MANUAL"} onClick={() => alternarOrigem("MANUAL")}>
            Manual
          </button>
        </div>
      </header>

      <div className="duas-colunas">
        <section className="cartao-tabela" aria-label="Extrato">
          <div className="busca">
            <label className="campo-rotulo" htmlFor="busca-extrato">
              Buscar no mês
            </label>
            <input
              id="busca-extrato"
              className="entrada"
              type="search"
              value={busca}
              autoComplete="off"
              placeholder="descrição ou contraparte"
              onChange={(e) => setBusca(e.target.value)}
            />
          </div>
          {extrato.isError && (
            <p role="alert" className="alerta-form margem">
              {extrato.error.message}
            </p>
          )}
          <div className="rolagem-x">
            <table className="tabela">
              <thead>
                <tr>
                  <th scope="col">Data</th>
                  <th scope="col">Descrição</th>
                  <th scope="col">Categoria</th>
                  <th scope="col">Quem</th>
                  <th scope="col">Origem</th>
                  <th scope="col" className="celula-valor">
                    Valor
                  </th>
                </tr>
              </thead>
              <tbody>
                {extrato.data?.lancamentos.map((l) => {
                  const origem = ORIGENS[l.origem] ?? { rotulo: l.origem, classe: "selo-manual" };
                  return (
                    <tr key={l.id} className="linha-clicavel" onClick={() => setEditando(l)}>
                      <td className="num texto-suave">{diaEMes(l.data)}</td>
                      <td>
                        <button type="button" className="botao-linha" onClick={(e) => (e.stopPropagation(), setEditando(l))}>
                          {l.descricao}
                        </button>
                        {l.confirmacao === "A_CONFIRMAR" && (
                          <span className="selo selo-a-confirmar" title="Pendente no banco: pode mudar ou sumir até ser lançada.">
                            a confirmar
                          </span>
                        )}
                        {l.parcela != null && (
                          <span className="texto-fraco nota-linha">
                            {" "}
                            parcela {l.parcela}/{l.parcelas}
                          </span>
                        )}
                        <span className="texto-fraco nota-linha"> · {l.conta.apelido}</span>
                      </td>
                      <Categoria l={l} />
                      <td>{l.quem}</td>
                      <td>
                        <span className={`selo ${origem.classe}`}>{origem.rotulo}</span>
                      </td>
                      <Valor l={l} />
                    </tr>
                  );
                })}
              </tbody>
            </table>
            {extrato.data && extrato.data.lancamentos.length === 0 && (
              <p className="cartao-nota margem">{q ? "Nada encontrado neste mês." : "Nenhum lançamento neste mês."}</p>
            )}
            {extrato.isPending && <p className="cartao-nota margem">Carregando…</p>}
          </div>
        </section>

        <aside className="coluna-lateral">
          {contas.isError && <p className="alerta-form">{contas.error.message}</p>}
          {categorias.isError && <p className="alerta-form">{categorias.error.message}</p>}
          {contas.data && contasParaLancar(contas.data).length === 0 && (
            <section className="cartao">
              <h2 className="cartao-titulo">Lançamento rápido</h2>
              <p className="cartao-nota">
                {contas.data.some((c) => !c.arquivada && c.ligada)
                  ? "As contas que vêm do banco recebem os lançamentos sozinhas. Para lançar à mão, cadastre uma conta manual (dinheiro, por exemplo). "
                  : "Cadastre uma conta antes de lançar. "}
                <Link to="/contas">Ir para contas e cartões</Link>
              </p>
            </section>
          )}
          {contas.data && contasParaLancar(contas.data).length > 0 && categorias.data && (
            <LancamentoRapido contas={contasParaLancar(contas.data)} categorias={categorias.data} />
          )}
        </aside>
      </div>

      {editando && categorias.data && (
        <EditarLancamento
          key={editando.id}
          lancamento={editando}
          categorias={categorias.data}
          emContaLigada={edicaoEmContaLigada(editando, contas.data)}
          aoFechar={() => setEditando(null)}
        />
      )}
    </>
  );
}
