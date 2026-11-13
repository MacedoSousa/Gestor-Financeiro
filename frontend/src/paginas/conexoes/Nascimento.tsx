import { useEffect, useId, useRef, useState, type FormEvent } from "react";
import { Link, useParams } from "react-router";
import { useQueryClient } from "@tanstack/react-query";
import { ErroDeApi, useEu } from "../../api/autenticacao";
import { useContas, rotuloDoTipo } from "../../api/contas";
import { usePainelDeMetas } from "../../api/metas";
import { ErroDeApelido, useAcoesDeNascimento, useDescoberta, type ContaNaoImportada, type Descoberta } from "../../api/nascimento";
import type { ResultadoDaSincronizacao } from "../../api/conexoes";
import { textoDaSincronizacao } from "../../lib/conexoes";
import { formatarReais } from "../../lib/dinheiro";
import {
  APELIDO_MAXIMO,
  contasCompativeis,
  gruposDistintos,
  linhasDeAplicacao,
  linhasIniciais,
  montarPedido,
  resumoDaDescoberta,
  trocarTitular,
  validarConfirmacao,
  type Acao,
  type ErroDaLinha,
  type LinhaDeAplicacao,
  type LinhaDeConta,
} from "../../lib/nascimento";
import { Alerta, Campo, CampoSelecao } from "../Campo";

const ACOES: ReadonlyArray<{ valor: Acao; rotulo: string }> = [
  { valor: "CRIAR", rotulo: "Criar conta nova" },
  { valor: "LIGAR", rotulo: "Ligar a uma conta que já existe" },
  { valor: "NAO_IMPORTAR", rotulo: "Não importar" },
];

type Titular = { valor: string; rotulo: string };

/** Erro do servidor na tela; 401 invalida a sessão (o Layout leva a /entrar). */
function useErroDoServidor() {
  const consultas = useQueryClient();
  const [erro, setErro] = useState<string>();
  const [tentativa, setTentativa] = useState(0);
  const falhar = (e: unknown) => {
    if (e instanceof ErroDeApi && e.status === 401) void consultas.invalidateQueries({ queryKey: ["eu"] });
    setErro(e instanceof ErroDeApi ? e.message : "Não foi possível concluir. Tente de novo.");
    setTentativa((t) => t + 1);
  };
  return { erro, tentativa, falhar, limpar: () => setErro(undefined) };
}

/** Os dias do cartão (fechamento e vencimento), lado a lado. */
function DiasDoCartao({ linha, erro, aoMudar }: {
  linha: { diaFechamento: string; diaVencimento: string };
  erro?: ErroDaLinha;
  aoMudar: (campo: "diaFechamento" | "diaVencimento", v: string) => void;
}) {
  const no = (campo: ErroDaLinha["campo"]) => (erro?.campo === campo ? erro.mensagem : undefined);
  return (
    <div className="grade-dias">
      <Campo rotulo="Dia de fechamento" valor={linha.diaFechamento} aoMudar={(v) => aoMudar("diaFechamento", v)} erro={no("diaFechamento")} inputMode="numeric" maxLength={2} classeEntrada="num" />
      <Campo rotulo="Dia de vencimento" valor={linha.diaVencimento} aoMudar={(v) => aoMudar("diaVencimento", v)} erro={no("diaVencimento")} inputMode="numeric" maxLength={2} classeEntrada="num" />
    </div>
  );
}

function ContaEncontrada({
  linha,
  banco,
  titulares,
  nomes,
  contas,
  erro,
  ocupado,
  aoMudar,
}: {
  linha: LinhaDeConta;
  banco: string;
  titulares: Titular[];
  nomes: Record<string, string>;
  contas: ReturnType<typeof useContas>["data"];
  erro?: ErroDaLinha;
  ocupado: boolean;
  aoMudar: (linha: LinhaDeConta) => void;
}) {
  const id = useId();
  const cartao = linha.tipo === "CARTAO";
  const no = (campo: ErroDaLinha["campo"]) => (erro?.campo === campo ? erro.mensagem : undefined);
  const compativeis = contasCompativeis(linha.tipo, contas ?? []);
  const mudar = <K extends keyof LinhaDeConta>(campo: K, valor: LinhaDeConta[K]) => aoMudar({ ...linha, [campo]: valor });

  return (
    <fieldset className="conta-encontrada" disabled={ocupado}>
      <legend className="conta-encontrada-titulo">
        {linha.nomeNoBanco} <span className="texto-suave">· {rotuloDoTipo(linha.tipo)}</span>
      </legend>
      {cartao && linha.limiteCentavos != null && (
        <span className="cartao-nota">
          Limite no banco: <span className="num">{formatarReais(linha.limiteCentavos)}</span>
        </span>
      )}

      <div className="opcoes-acao" role="radiogroup" aria-label={`O que fazer com ${linha.nomeNoBanco}`}>
        {ACOES.map((a) => (
          <label key={a.valor} className="campo-check">
            <input type="radio" name={`${id}-acao`} value={a.valor} checked={linha.acao === a.valor} onChange={() => mudar("acao", a.valor)} />
            <span>{a.rotulo}</span>
          </label>
        ))}
      </div>

      {linha.acao === "CRIAR" && (
        <div className="grade-form">
          <Campo rotulo="Apelido" valor={linha.apelido} aoMudar={(v) => mudar("apelido", v)} erro={no("apelido")} maxLength={APELIDO_MAXIMO} autoComplete="off" />
          <CampoSelecao rotulo="Titular" valor={linha.titular} aoMudar={(v) => aoMudar(trocarTitular(linha, v, banco, nomes))} opcoes={titulares} />
          {cartao && <DiasDoCartao linha={linha} erro={erro} aoMudar={(campo, v) => mudar(campo, v)} />}
        </div>
      )}

      {linha.acao === "LIGAR" &&
        (compativeis.length === 0 ? (
          <p className="cartao-nota">
            Nenhuma conta {cartao ? "de cartão" : "corrente ou poupança"} disponível para receber esta. Crie uma conta nova.
          </p>
        ) : (
          <CampoSelecao
            rotulo="Conta que recebe"
            valor={linha.contaExistenteId}
            aoMudar={(v) => mudar("contaExistenteId", v)}
            opcoes={[{ valor: "", rotulo: "Escolha a conta" }, ...compativeis.map((c) => ({ valor: c.id, rotulo: c.apelido }))]}
            nota="Ela passa a vir do banco: os lançamentos chegam sozinhos."
            erro={no("contaExistenteId")}
          />
        ))}

      {linha.acao === "NAO_IMPORTAR" && (
        <p className="cartao-nota">Fica registrada como não importada e não é perguntada de novo. Dá para incluir depois, nesta tela.</p>
      )}

    </fieldset>
  );
}

function Investimentos({
  linhas,
  metaPorGrupo,
  metas,
  erros,
  ocupado,
  aoMudarLinha,
  aoMudarMeta,
}: {
  linhas: LinhaDeAplicacao[];
  metaPorGrupo: Record<string, string>;
  metas: { id: string; nome: string }[];
  erros: Record<string, ErroDaLinha>;
  ocupado: boolean;
  aoMudarLinha: (linha: LinhaDeAplicacao) => void;
  aoMudarMeta: (chave: string, metaId: string) => void;
}) {
  const grupos = gruposDistintos(linhas);
  return (
    <fieldset className="escolha" disabled={ocupado}>
      <legend className="cartao-rotulo">Investimentos</legend>
      <p className="cartao-nota" style={{ margin: 0 }}>
        Cada grupo vira uma conta de investimento, com o saldo somado das aplicações. Deixe o grupo vazio para decidir depois.
      </p>
      <ul className="lista-itens">
        {linhas.map((a) => (
          <li key={a.idExterno} className="item-linha aplicacao-linha">
            <div className="item-texto">
              <span style={{ fontWeight: 500 }}>{a.nome}</span>
              <span className="cartao-nota">
                {a.tipo} ·{" "}
                {a.saldoCentavos == null ? (
                  "o banco não informou o valor"
                ) : (
                  <span className="num">{formatarReais(a.saldoCentavos)}</span>
                )}
              </span>
            </div>
            <Campo rotulo={`Grupo de ${a.nome}`} valor={a.grupo} aoMudar={(v) => aoMudarLinha({ ...a, grupo: v })} erro={erros[a.idExterno]?.mensagem} maxLength={APELIDO_MAXIMO + 10} autoComplete="off" />
          </li>
        ))}
      </ul>
      {grupos.length > 0 && metas.length > 0 && (
        <div className="grade-form">
          {grupos.map((g) => (
            <CampoSelecao
              key={g.chave}
              rotulo={`Meta de ${g.nome}`}
              valor={metaPorGrupo[g.chave] ?? ""}
              aoMudar={(v) => aoMudarMeta(g.chave, v)}
              opcoes={[{ valor: "", rotulo: "Sem meta" }, ...metas.map((m) => ({ valor: m.id, rotulo: m.nome }))]}
            />
          ))}
        </div>
      )}
    </fieldset>
  );
}

/** Uma conta "não importada": incluir abre um formulário curto, com o apelido sugerido e, no cartão, os dias. */
function NaoImportada({ conta, banco, titulares, nomes, aoIncluir }: {
  conta: ContaNaoImportada;
  banco: string;
  titulares: Titular[];
  nomes: Record<string, string>;
  aoIncluir: (conta: ContaNaoImportada, linha: LinhaDeConta) => Promise<boolean>;
}) {
  const [aberta, setAberta] = useState(false);
  const [ocupado, setOcupado] = useState(false);
  const formulario = useRef<HTMLFormElement>(null);
  const botao = useRef<HTMLButtonElement>(null);
  const [voltar, setVoltar] = useState(false);
  const rascunho = (): LinhaDeConta => ({
    idExterno: conta.idExterno, tipo: conta.tipo, nomeNoBanco: conta.nomeNoBanco, acao: "CRIAR", apelido: conta.apelidoSugerido,
    titular: titulares[1]?.valor ?? "", contaExistenteId: "", diaFechamento: "", diaVencimento: "", limiteCentavos: null,
  });
  const [linha, setLinha] = useState<LinhaDeConta>(rascunho);
  const erro = validarConfirmacao([linha], [])[linha.idExterno];
  const [mostrarErro, setMostrarErro] = useState(false);
  const no = (campo: ErroDaLinha["campo"]) => (mostrarErro && erro?.campo === campo ? erro.mensagem : undefined);

  // Ao abrir, o foco vai ao apelido; ao fechar, volta ao botão "Incluir" (2.4.3).
  useEffect(() => {
    if (aberta) formulario.current?.querySelector<HTMLInputElement>("input")?.focus();
    else if (voltar) botao.current?.focus();
  }, [aberta, voltar]);

  /** Fecha o formulário; cancelar volta ao rascunho sugerido, sem erro aparecendo antes do próximo envio. */
  function fechar() {
    setLinha(rascunho());
    setMostrarErro(false);
    setVoltar(true);
    setAberta(false);
  }

  async function incluir(e: FormEvent) {
    e.preventDefault();
    setMostrarErro(true);
    if (erro) {
      requestAnimationFrame(() => formulario.current?.querySelector<HTMLElement>('[aria-invalid="true"]')?.focus());
      return;
    }
    setOcupado(true);
    const ok = await aoIncluir(conta, linha);
    setOcupado(false);
    if (ok) fechar();
  }

  return (
    <li className="item-linha">
      <div className="item-texto">
        <span style={{ fontWeight: 500 }}>{conta.nomeNoBanco}</span>
        <span className="cartao-nota">{rotuloDoTipo(conta.tipo)} · não importada</span>
      </div>
      {!aberta ? (
        <button ref={botao} type="button" className="botao-secundario" aria-label={`Incluir: ${conta.nomeNoBanco}`} onClick={() => setAberta(true)}>
          Incluir
        </button>
      ) : (
        <form ref={formulario} className="bloco-form inclusao" noValidate onSubmit={(e) => void incluir(e)} aria-label={`Incluir ${conta.nomeNoBanco}`}>
          <div className="grade-form">
            <Campo rotulo="Apelido" valor={linha.apelido} aoMudar={(v) => setLinha({ ...linha, apelido: v })} maxLength={APELIDO_MAXIMO} erro={no("apelido")} autoComplete="off" />
            <CampoSelecao rotulo="Titular" valor={linha.titular} aoMudar={(v) => setLinha(trocarTitular(linha, v, banco, nomes))} opcoes={titulares} />
            {conta.tipo === "CARTAO" && <DiasDoCartao linha={linha} erro={mostrarErro ? erro : undefined} aoMudar={(campo, v) => setLinha({ ...linha, [campo]: v })} />}
          </div>
          <div className="acoes-linha">
            <button type="submit" className="botao-secundario" disabled={ocupado}>
              {ocupado ? "Incluindo…" : "Incluir conta"}
            </button>
            <button type="button" className="botao-secundario" disabled={ocupado} onClick={fechar}>
              Cancelar
            </button>
          </div>
        </form>
      )}
    </li>
  );
}

function Carregando() {
  return (
    <div className="esqueleto" aria-hidden="true">
      <span />
      <span />
      <span />
    </div>
  );
}

/** A confirmação em si: estado do formulário nasce da descoberta e é refeito quando ela muda (depois de confirmar). */
/** O que a primeira sincronização trouxe; se ela falhou agora, onde tentar de novo. */
const depoisDeNascer = (r: ResultadoDaSincronizacao | null) =>
  r ? textoDaSincronizacao(r) : "O histórico não veio agora; em Conexões, use Sincronizar agora.";

function Confirmacao({ itemId, descoberta, aoAvisar }: { itemId: string; descoberta: Descoberta; aoAvisar: (texto: string) => void }) {
  const eu = useEu();
  const contas = useContas();
  const metas = usePainelDeMetas();
  const acoes = useAcoesDeNascimento(itemId);
  const servidor = useErroDoServidor();
  const [linhas, setLinhas] = useState(() => linhasIniciais(descoberta));
  const [aplicacoes, setAplicacoes] = useState(() => linhasDeAplicacao(descoberta));
  const [metaPorGrupo, setMetaPorGrupo] = useState<Record<string, string>>({});
  const [erros, setErros] = useState<Record<string, ErroDaLinha>>({});
  const [ocupado, setOcupado] = useState(false);
  const form = useRef<HTMLFormElement>(null);

  const nomes: Record<string, string> = {};
  if (eu.data) nomes[eu.data.usuarioId] = eu.data.apelido;
  if (eu.data?.usuarioIdDoConjuge) nomes[eu.data.usuarioIdDoConjuge] = eu.data.apelidoDoConjuge ?? "";
  const titulares: Titular[] = [{ valor: "", rotulo: "Conjunta" }, ...Object.entries(nomes).map(([valor, rotulo]) => ({ valor, rotulo }))];
  const jaVem = descoberta.contas.filter((c) => c.jaVemDe);
  const nada = linhas.length === 0 && aplicacoes.length === 0;

  useEffect(() => {
    if (Object.keys(erros).length > 0)
      requestAnimationFrame(() => form.current?.querySelector<HTMLElement>('input[aria-invalid="true"], select[aria-invalid="true"]')?.focus());
  }, [erros]);

  async function confirmar(e: FormEvent) {
    e.preventDefault();
    servidor.limpar();
    const encontrados = validarConfirmacao(linhas, aplicacoes);
    setErros(encontrados);
    if (Object.keys(encontrados).length > 0) return;
    setOcupado(true);
    try {
      const sincronizacao = await acoes.confirmar(montarPedido(linhas, aplicacoes, metaPorGrupo));
      aoAvisar(`Pronto. As contas confirmadas aparecem em Contas com o selo do banco. ${depoisDeNascer(sincronizacao)}`);
    } catch (erro) {
      // O 409 aponta a posição da conta no pedido, que é a ordem de `linhas` (montarPedido não reordena).
      const alvo = erro instanceof ErroDeApelido && erro.linha !== null ? linhas[erro.linha] : undefined;
      if (erro instanceof ErroDeApelido && alvo) {
        setLinhas((ls) => ls.map((l) => (l.idExterno === alvo.idExterno ? { ...l, apelido: erro.sugestao } : l)));
        setErros({ [alvo.idExterno]: { campo: "apelido", mensagem: `Já existe uma conta com esse nome. Sugerimos "${erro.sugestao}"; confira e confirme de novo.` } });
      } else servidor.falhar(erro);
    } finally {
      setOcupado(false);
    }
  }

  async function incluir(conta: ContaNaoImportada, linha: LinhaDeConta): Promise<boolean> {
    servidor.limpar();
    const cartao = linha.tipo === "CARTAO";
    try {
      const sincronizacao = await acoes.incluir(conta.vinculoId, {
        acao: "CRIAR",
        contaExistenteId: null,
        apelido: linha.apelido.trim(),
        titularUsuarioId: linha.titular || null,
        diaFechamento: cartao ? Number(linha.diaFechamento) : null,
        diaVencimento: cartao ? Number(linha.diaVencimento) : null,
      });
      aoAvisar(`${linha.apelido.trim()} foi incluída e aparece em Contas. ${depoisDeNascer(sincronizacao)}`);
      return true;
    } catch (erro) {
      servidor.falhar(erro instanceof ErroDeApelido ? new ErroDeApi(409, `Já existe uma conta com esse nome. Sugestão: ${erro.sugestao}.`) : erro);
      return false;
    }
  }

  return (
    <>
      <Alerta mensagem={servidor.erro} tentativa={servidor.tentativa} />

      {nada ? (
        <section className="cartao" aria-labelledby="titulo-encontradas">
          <h2 id="titulo-encontradas" className="cartao-rotulo" style={{ margin: 0 }}>
            Tudo em dia
          </h2>
          <p className="cartao-nota" style={{ margin: 0 }}>
            As contas deste banco já estão no sistema. <Link to="/contas">Ver as contas</Link>
          </p>
        </section>
      ) : (
        <form ref={form} className="cartao bloco-form" noValidate onSubmit={(e) => void confirmar(e)} aria-labelledby="titulo-encontradas">
          <h2 id="titulo-encontradas" className="cartao-rotulo" style={{ margin: 0 }}>
            Encontradas no banco
          </h2>
          {linhas.map((l) => (
            <ContaEncontrada
              key={l.idExterno}
              linha={l}
              banco={descoberta.banco}
              titulares={titulares}
              nomes={nomes}
              contas={contas.data}
              erro={erros[l.idExterno]}
              ocupado={ocupado}
              aoMudar={(nova) => setLinhas((ls) => ls.map((x) => (x.idExterno === nova.idExterno ? nova : x)))}
            />
          ))}
          {aplicacoes.length > 0 && (
            <Investimentos
              linhas={aplicacoes}
              metaPorGrupo={metaPorGrupo}
              metas={(metas.data?.metas ?? []).map((m) => ({ id: m.id, nome: m.nome }))}
              erros={erros}
              ocupado={ocupado}
              aoMudarLinha={(nova) => setAplicacoes((as) => as.map((x) => (x.idExterno === nova.idExterno ? nova : x)))}
              aoMudarMeta={(chave, metaId) => setMetaPorGrupo((m) => ({ ...m, [chave]: metaId }))}
            />
          )}
          <div className="acoes-linha">
            <button type="submit" className="botao-primario" disabled={ocupado}>
              {ocupado ? "Criando…" : "Confirmar"}
            </button>
            <Link to="/conexoes" className="botao-secundario link-botao">
              Decidir depois
            </Link>
          </div>
        </form>
      )}

      {jaVem.length > 0 && (
        <section className="cartao" aria-labelledby="titulo-ja-vem">
          <h2 id="titulo-ja-vem" className="cartao-rotulo" style={{ margin: 0 }}>
            Já vêm de outra conexão
          </h2>
          <ul className="lista-itens">
            {jaVem.map((c) => (
              <li key={c.idExterno} className="item-linha">
                <div className="item-texto">
                  <span style={{ fontWeight: 500 }}>{c.nomeNoBanco}</span>
                  <span className="cartao-nota">
                    {rotuloDoTipo(c.tipo)} · Esta conta já vem da conexão de {c.jaVemDe}. Ela fica de fora daqui.
                  </span>
                </div>
              </li>
            ))}
          </ul>
        </section>
      )}

      {descoberta.naoImportadas.length > 0 && (
        <section className="cartao" aria-labelledby="titulo-nao-importadas">
          <h2 id="titulo-nao-importadas" className="cartao-rotulo" style={{ margin: 0 }}>
            Não importadas
          </h2>
          <ul className="lista-itens">
            {descoberta.naoImportadas.map((c) => (
              <NaoImportada key={c.vinculoId} conta={c} banco={descoberta.banco} titulares={titulares} nomes={nomes} aoIncluir={incluir} />
            ))}
          </ul>
        </section>
      )}
    </>
  );
}

/** Confirmação das contas que vêm de um banco (Fase 2, etapa 3): nada é criado sem confirmação. Só o titular da conexão. */
export function Nascimento() {
  const { id = "" } = useParams();
  const descoberta = useDescoberta(id);
  const [aviso, setAviso] = useState<string>();
  const banco = descoberta.data?.banco;

  return (
    <>
      <header style={{ display: "flex", flexDirection: "column", gap: 6 }}>
        <Link to="/conexoes" className="link-voltar">
          ← Conexões
        </Link>
        <h1 className="titulo">{banco ? `Contas do ${banco}` : "Contas do banco"}</h1>
        <span className="subtitulo">
          {descoberta.data ? resumoDaDescoberta(descoberta.data) : descoberta.isError ? "Não foi possível ler as contas do banco." : "Lendo o que o banco tem…"}
        </span>
      </header>

      <p role="status" className={aviso ? "aviso-form" : "sr-only"}>
        {aviso ?? ""}
      </p>

      {descoberta.isPending && (
        <section className="cartao" aria-busy="true" aria-label="Lendo as contas do banco">
          <Carregando />
        </section>
      )}

      {descoberta.isError && (
        <section className="cartao erro-secao" role="alert">
          <span>{descoberta.error.message}</span>
          <div className="acoes-linha">
            {/* 409 (banco ainda não escolhido) não se resolve tentando de novo: só voltando a Conexões. */}
            {!(descoberta.error instanceof ErroDeApi && descoberta.error.status === 409) && (
              <button type="button" className="botao-secundario" onClick={() => void descoberta.refetch()}>
                Tentar de novo
              </button>
            )}
            <Link to="/conexoes" className="botao-secundario link-botao">
              Voltar para Conexões
            </Link>
          </div>
        </section>
      )}

      {descoberta.data && (
        <Confirmacao
          key={JSON.stringify([descoberta.data.contas.map((c) => c.idExterno), descoberta.data.aplicacoes.map((a) => a.idExterno), descoberta.data.naoImportadas.map((c) => c.vinculoId)])}
          itemId={id}
          descoberta={descoberta.data}
          aoAvisar={setAviso}
        />
      )}
    </>
  );
}

