import { useEffect, useId, useRef, useState, type FormEvent, type RefObject } from "react";
import { Link } from "react-router";
import { useQueryClient } from "@tanstack/react-query";
import { ErroDeApi } from "../api/autenticacao";
import { n } from "../api/formulario";
import { useAcoesDeConexoes, useConexoes, type Banco, type Conexao, type ItemDaConexao, type Slot } from "../api/conexoes";
import {
  avisoDoSlot,
  BANCO_MAXIMO,
  ESCOLHA_DE_BANCO_VAZIA,
  estadoDoItem,
  ITEM_ID_MAXIMO,
  nomeDoBancoEscolhido,
  OUTRO_BANCO,
  pedidoDoBanco,
  separarConexoes,
  textoDoConsentimento,
  validarBanco,
  validarCadastro,
  type ErrosDoBanco,
  type ErrosDoCadastro,
  type EscolhaDeBanco,
} from "../lib/conexoes";
import { Alerta, Campo, CampoSelecao } from "./Campo";

const NOTA_DO_ITEM = "No Meu Pluggy, abra o banco conectado e copie o id do item. Ele fica guardado cifrado e não aparece de novo.";

/** Ação com mensagem de erro do servidor; 401 invalida a sessão (o Layout leva a /entrar). */
function useTentativa() {
  const consultas = useQueryClient();
  const [erro, setErro] = useState<string>();
  const [tentativa, setTentativa] = useState(0);
  const [ocupado, setOcupado] = useState(false);

  async function tentar(acao: () => Promise<void>): Promise<boolean> {
    setErro(undefined);
    setOcupado(true);
    try {
      await acao();
      return true;
    } catch (e) {
      if (e instanceof ErroDeApi && e.status === 401) void consultas.invalidateQueries({ queryKey: ["eu"] });
      setErro(e instanceof ErroDeApi ? e.message : "Não foi possível concluir. Tente de novo.");
      setTentativa((t) => t + 1);
      return false;
    } finally {
      setOcupado(false);
    }
  }

  return { erro, tentativa, ocupado, tentar };
}

/** Depois de validar no navegador: o foco vai ao primeiro campo com erro (rádio do slot, id do item, banco ou código). */
function focarPrimeiroErro(form: RefObject<HTMLFormElement | null>) {
  requestAnimationFrame(() =>
    form.current
      ?.querySelector<HTMLElement>(
        'fieldset[aria-invalid="true"] input:not(:disabled), input[aria-invalid="true"], select[aria-invalid="true"]',
      )
      ?.focus(),
  );
}

/**
 * Banco do item pela lista fixa de códigos (D18); "Outro" abre o código de 3 dígitos e o nome. O código separa contas de
 * bancos diferentes com a mesma agência e conta.
 */
function EscolhaDoBanco({
  bancos,
  valor,
  aoMudar,
  erros,
}: {
  bancos: readonly Banco[];
  valor: EscolhaDeBanco;
  aoMudar: (e: EscolhaDeBanco) => void;
  erros: ErrosDoBanco;
}) {
  const outro = valor.escolha === OUTRO_BANCO;
  const opcoes = [
    { valor: "", rotulo: "Escolha o banco" },
    ...bancos.map((b) => ({ valor: b.codigo, rotulo: `${b.nome} (${b.codigo})` })),
    { valor: OUTRO_BANCO, rotulo: "Outro" },
  ];
  return (
    <>
      <CampoSelecao
        rotulo="Banco"
        valor={valor.escolha}
        aoMudar={(escolha) => aoMudar({ ...valor, escolha })}
        opcoes={opcoes}
        erro={outro ? undefined : erros.codigoBanco}
      />
      {outro && (
        <>
          <Campo
            rotulo="Código do banco (3 dígitos)"
            valor={valor.codigoOutro}
            aoMudar={(codigoOutro) => aoMudar({ ...valor, codigoOutro })}
            erro={erros.codigoBanco}
            inputMode="numeric"
            autoComplete="off"
            maxLength={3}
            classeEntrada="num"
          />
          <Campo
            rotulo="Nome do banco"
            valor={valor.nomeOutro}
            aoMudar={(nomeOutro) => aoMudar({ ...valor, nomeOutro })}
            erro={erros.banco}
            maxLength={BANCO_MAXIMO}
          />
        </>
      )}
    </>
  );
}

/** "Chaves no slot 1 (arquivos pluggy_1_*)": só o nome dos arquivos em fonte mono. */
function ChavesNoSlot({ slot }: { slot: number }) {
  return (
    <>
      slot {slot} (arquivos <span className="num">pluggy_{slot}_*</span>)
    </>
  );
}

function EstadoDoBanco({ item, semBanco }: { item: ItemDaConexao; semBanco: string }) {
  const aparencia = estadoDoItem(item.estado, item.motivo);
  const consentimento = textoDoConsentimento(item.consentimentoVenceEm, item.estado);
  const classeDoPonto = aparencia.tom === "normal" ? "ponto" : `ponto ${aparencia.tom}`;
  return (
    <div className="item-texto">
      <span style={{ fontWeight: 500 }}>{item.banco}</span>
      <span className="status">
        <span className={classeDoPonto} aria-hidden="true" />
        {aparencia.rotulo}
        {consentimento ? <span className="texto-suave"> · {consentimento}</span> : null}
      </span>
      {aparencia.explicacao && <span className="cartao-nota">{aparencia.explicacao}</span>}
      {!item.codigoBanco && <span className="cartao-nota">{semBanco}</span>}
    </div>
  );
}

/** Escolha explícita do slot (D20, D23): nada vem marcado; slot em uso ou sem chaves fica desabilitado, com o motivo. */
function EscolhaDoSlot({ slots, valor, aoMudar, erro }: { slots: Slot[]; valor: number | null; aoMudar: (s: number) => void; erro?: string }) {
  const id = useId();
  return (
    <fieldset className="escolha" aria-invalid={erro ? true : undefined} aria-describedby={erro ? `${id}-nota ${id}-erro` : `${id}-nota`}>
      <legend className="campo-rotulo">Em qual slot estão as suas chaves?</legend>
      <span id={`${id}-nota`} className="cartao-nota">
        Cada cônjuge grava as próprias chaves do Meu Pluggy num slot do servidor. Escolha o seu.
      </span>
      {slots.map((s) => {
        const numero = n(s.slot);
        const aviso = avisoDoSlot({ slot: numero, configurado: s.configurado, emUso: s.emUso });
        return (
          <div key={numero}>
            <label className="campo-check opcao-slot">
              <input
                type="radio"
                name={`${id}-slot`}
                value={numero}
                checked={valor === numero}
                disabled={aviso !== null}
                aria-describedby={aviso ? `${id}-aviso-${numero}` : undefined}
                onChange={() => aoMudar(numero)}
              />
              <span>
                Estas chaves estão nos arquivos <span className="num">pluggy_{numero}_*</span>
                <span className="sr-only"> (slot {numero})</span>
              </span>
            </label>
            {aviso && (
              <span id={`${id}-aviso-${numero}`} className="cartao-nota aviso-do-slot">
                {aviso}
              </span>
            )}
          </div>
        );
      })}
      {erro && (
        <span id={`${id}-erro`} className="campo-erro">
          {erro}
        </span>
      )}
    </fieldset>
  );
}

/** Cadastro da própria conexão com o primeiro banco. O servidor consulta o banco antes de gravar. */
function NovaConexao({ slots, bancos, aoConcluir }: { slots: Slot[]; bancos: readonly Banco[]; aoConcluir: (aviso: string) => void }) {
  const acoes = useAcoesDeConexoes();
  const { erro, tentativa, ocupado, tentar } = useTentativa();
  const form = useRef<HTMLFormElement>(null);
  const [slot, setSlot] = useState<number | null>(null);
  const [itemId, setItemId] = useState("");
  const [banco, setBanco] = useState<EscolhaDeBanco>(ESCOLHA_DE_BANCO_VAZIA);
  const [erros, setErros] = useState<ErrosDoCadastro>({});
  const nenhumSlot = slots.every((s) => avisoDoSlot({ slot: n(s.slot), configurado: s.configurado, emUso: s.emUso }) !== null);

  async function enviar(e: FormEvent) {
    e.preventDefault();
    const encontrados = validarCadastro({ slot, itemId, banco });
    setErros(encontrados);
    if (Object.keys(encontrados).length > 0 || slot === null) {
      focarPrimeiroErro(form);
      return;
    }
    const nome = nomeDoBancoEscolhido(banco, bancos);
    if (await tentar(async () => void (await acoes.conectar(slot, itemId, pedidoDoBanco(banco)))))
      aoConcluir(`Conexão criada. ${nome} foi aceito pelo banco.`);
  }

  return (
    <form ref={form} className="bloco-form" noValidate onSubmit={(e) => void enviar(e)} aria-labelledby="titulo-sua-conexao">
      {nenhumSlot && (
        <p className="faixa-aviso">
          Nenhum slot está livre com as chaves gravadas. Quem administra o servidor grava as chaves pelo terminal (passo 7 do
          gerar-segredos.sh).
        </p>
      )}
      <EscolhaDoSlot slots={slots} valor={slot} aoMudar={setSlot} erro={erros.slot} />
      <Campo
        rotulo="Id do item no Meu Pluggy"
        valor={itemId}
        aoMudar={setItemId}
        nota={NOTA_DO_ITEM}
        erro={erros.itemId}
        autoComplete="off"
        maxLength={ITEM_ID_MAXIMO}
        classeEntrada="num"
      />
      <EscolhaDoBanco bancos={bancos} valor={banco} aoMudar={setBanco} erros={erros} />
      <Alerta mensagem={erro} tentativa={tentativa} />
      <div>
        <button type="submit" className="botao-primario" disabled={ocupado || nenhumSlot}>
          {ocupado ? "Consultando o banco…" : "Conectar"}
        </button>
      </div>
    </form>
  );
}

/** Mais um banco na própria conexão. Ao abrir, o foco vai para o primeiro campo. */
function NovoBanco({
  conexao,
  bancos,
  aoConcluir,
  aoCancelar,
}: {
  conexao: Conexao;
  bancos: readonly Banco[];
  aoConcluir: (aviso: string) => void;
  aoCancelar: () => void;
}) {
  const acoes = useAcoesDeConexoes();
  const { erro, tentativa, ocupado, tentar } = useTentativa();
  const form = useRef<HTMLFormElement>(null);
  const [itemId, setItemId] = useState("");
  const [banco, setBanco] = useState<EscolhaDeBanco>(ESCOLHA_DE_BANCO_VAZIA);
  const [erros, setErros] = useState<ErrosDoCadastro>({});

  useEffect(() => {
    form.current?.querySelector<HTMLInputElement>("input")?.focus();
  }, []);

  async function enviar(e: FormEvent) {
    e.preventDefault();
    const encontrados = validarCadastro({ itemId, banco });
    setErros(encontrados);
    if (Object.keys(encontrados).length > 0) {
      focarPrimeiroErro(form);
      return;
    }
    const nome = nomeDoBancoEscolhido(banco, bancos);
    if (await tentar(async () => void (await acoes.acrescentarBanco(conexao.id, itemId, pedidoDoBanco(banco)))))
      aoConcluir(`${nome} entrou na sua conexão.`);
  }

  return (
    <form
      ref={form}
      className="confirmacao"
      noValidate
      onSubmit={(e) => void enviar(e)}
      onKeyDown={(e) => {
        if (e.key === "Escape" && !ocupado) aoCancelar();
      }}
      aria-label="Acrescentar banco"
    >
      <Campo
        rotulo="Id do item no Meu Pluggy"
        valor={itemId}
        aoMudar={setItemId}
        nota={NOTA_DO_ITEM}
        erro={erros.itemId}
        autoComplete="off"
        maxLength={ITEM_ID_MAXIMO}
        classeEntrada="num"
      />
      <EscolhaDoBanco bancos={bancos} valor={banco} aoMudar={setBanco} erros={erros} />
      <Alerta mensagem={erro} tentativa={tentativa} />
      <div className="acoes-linha">
        <button type="submit" className="botao-primario" disabled={ocupado}>
          {ocupado ? "Consultando o banco…" : "Acrescentar"}
        </button>
        <button type="button" className="botao-secundario" disabled={ocupado} onClick={aoCancelar}>
          Cancelar
        </button>
      </div>
    </form>
  );
}

/** Banco de um item cadastrado antes do código (D18): o titular escolhe uma vez; nada muda na Pluggy. */
function EscolherBanco({
  item,
  bancos,
  aoConcluir,
  aoCancelar,
}: {
  item: ItemDaConexao;
  bancos: readonly Banco[];
  aoConcluir: (aviso: string) => void;
  aoCancelar: () => void;
}) {
  const acoes = useAcoesDeConexoes();
  const { erro, tentativa, ocupado, tentar } = useTentativa();
  const form = useRef<HTMLFormElement>(null);
  const [banco, setBanco] = useState<EscolhaDeBanco>(ESCOLHA_DE_BANCO_VAZIA);
  const [erros, setErros] = useState<ErrosDoBanco>({});

  useEffect(() => {
    form.current?.querySelector<HTMLSelectElement>("select")?.focus();
  }, []);

  async function enviar(e: FormEvent) {
    e.preventDefault();
    const encontrados = validarBanco(banco);
    setErros(encontrados);
    if (Object.keys(encontrados).length > 0) {
      focarPrimeiroErro(form);
      return;
    }
    const nome = nomeDoBancoEscolhido(banco, bancos);
    if (await tentar(async () => void (await acoes.escolherBanco(item.id, pedidoDoBanco(banco)))))
      aoConcluir(`Banco escolhido: ${nome}. As contas dele já podem ser vistas.`);
  }

  return (
    <form
      ref={form}
      className="confirmacao"
      noValidate
      onSubmit={(e) => void enviar(e)}
      onKeyDown={(e) => {
        if (e.key === "Escape" && !ocupado) aoCancelar();
      }}
      aria-label={`Escolher o banco de ${item.banco}`}
    >
      <p className="confirmacao-texto">
        Qual é o banco de “{item.banco}”? O código separa contas de bancos diferentes com a mesma agência e conta. Escolhido uma
        vez, não muda.
      </p>
      <EscolhaDoBanco bancos={bancos} valor={banco} aoMudar={setBanco} erros={erros} />
      <Alerta mensagem={erro} tentativa={tentativa} />
      <div className="acoes-linha">
        <button type="submit" className="botao-primario" disabled={ocupado}>
          {ocupado ? "Guardando…" : "Guardar o banco"}
        </button>
        <button type="button" className="botao-secundario" disabled={ocupado} onClick={aoCancelar}>
          Cancelar
        </button>
      </div>
    </form>
  );
}

type Confirmando = { tipo: "banco"; item: ItemDaConexao } | { tipo: "conexao" } | null;

/**
 * A conexão de quem está na sessão: bancos, acrescentar, tirar e apagar (só o titular; D21). A confirmação recebe o foco
 * quando aparece e fecha com Esc; concluir ou cancelar devolve o foco ao título da seção (`aoVoltar`).
 */
function SuaConexao({
  conexao,
  bancos,
  aoAvisar,
  aoVoltar,
}: {
  conexao: Conexao;
  bancos: readonly Banco[];
  aoAvisar: (texto: string) => void;
  aoVoltar: () => void;
}) {
  const acoes = useAcoesDeConexoes();
  const { erro, tentativa, ocupado, tentar } = useTentativa();
  const [acrescentando, setAcrescentando] = useState(false);
  const [escolhendo, setEscolhendo] = useState<ItemDaConexao | null>(null);
  const [confirmando, setConfirmando] = useState<Confirmando>(null);
  const caixa = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (confirmando) caixa.current?.focus();
  }, [confirmando]);

  function cancelar() {
    setConfirmando(null);
    aoVoltar();
  }

  async function confirmar() {
    const alvo = confirmando;
    if (!alvo) return;
    const ok = await tentar(async () => {
      if (alvo.tipo === "banco") await acoes.tirarBanco(conexao.id, alvo.item.id);
      else await acoes.apagarConexao(conexao.id);
    });
    if (!ok) return;
    setConfirmando(null);
    aoAvisar(alvo.tipo === "banco" ? `${alvo.item.banco} saiu da sua conexão.` : "Sua conexão foi apagada. O slot ficou livre.");
  }

  return (
    <>
      <p className="cartao-nota" style={{ margin: 0 }}>
        Chaves no <ChavesNoSlot slot={n(conexao.slot)} />
        {conexao.slotConfigurado ? "" : " · os arquivos estão vazios no servidor"}
      </p>
      {conexao.items.length === 0 ? (
        <p className="cartao-nota">Nenhum banco nesta conexão. Acrescente um para começar.</p>
      ) : (
        <ul className="lista-itens">
          {conexao.items.map((item) => (
            <li key={item.id} className="item-linha">
              <EstadoDoBanco item={item} semBanco="Escolha o banco para ver as contas dele." />
              <div className="acoes-linha">
                {item.codigoBanco ? (
                  <Link
                    to={`/conexoes/items/${item.id}/nascimento`}
                    className="botao-secundario link-botao"
                    aria-label={`Contas do banco: ${item.banco}`}
                  >
                    Contas do banco
                  </Link>
                ) : (
                  <button
                    type="button"
                    className="botao-secundario"
                    disabled={ocupado || escolhendo !== null}
                    aria-label={`Escolher o banco: ${item.banco}`}
                    onClick={() => {
                      setAcrescentando(false);
                      setConfirmando(null);
                      setEscolhendo(item);
                    }}
                  >
                    Escolher o banco
                  </button>
                )}
                <button
                  type="button"
                  className="botao-secundario"
                  disabled={ocupado}
                  aria-label={`Tirar ${item.banco} da conexão`}
                  onClick={() => setConfirmando({ tipo: "banco", item })}
                >
                  Tirar
                </button>
              </div>
            </li>
          ))}
        </ul>
      )}

      {confirmando && (
        <div
          ref={caixa}
          className="confirmacao"
          role="group"
          tabIndex={-1}
          aria-labelledby="texto-confirmar-conexao"
          onKeyDown={(e) => {
            if (e.key === "Escape" && !ocupado) cancelar();
          }}
        >
          <p id="texto-confirmar-conexao" className="confirmacao-texto">
            {confirmando.tipo === "banco"
              ? `Tirar ${confirmando.item.banco} da sua conexão? O sistema para de ler este banco; o que já entrou fica.`
              : "Apagar a sua conexão? Os bancos dela param de ser lidos; o que já entrou fica, e o slot fica livre."}
          </p>
          <Alerta mensagem={erro} tentativa={tentativa} />
          <div className="acoes-linha">
            <button type="button" className="botao-perigo" disabled={ocupado} onClick={() => void confirmar()}>
              {confirmando.tipo === "banco" ? "Tirar o banco" : "Apagar a conexão"}
            </button>
            <button type="button" className="botao-secundario" disabled={ocupado} onClick={cancelar}>
              Cancelar
            </button>
          </div>
        </div>
      )}

      {escolhendo && (
        <EscolherBanco
          key={escolhendo.id}
          item={escolhendo}
          bancos={bancos}
          aoCancelar={() => {
            setEscolhendo(null);
            aoVoltar();
          }}
          aoConcluir={(aviso) => {
            setEscolhendo(null);
            aoAvisar(aviso);
          }}
        />
      )}

      {escolhendo ? null : acrescentando ? (
        <NovoBanco
          conexao={conexao}
          bancos={bancos}
          aoCancelar={() => {
            setAcrescentando(false);
            aoVoltar();
          }}
          aoConcluir={(aviso) => {
            setAcrescentando(false);
            aoAvisar(aviso);
          }}
        />
      ) : (
        !confirmando && (
          <div className="acoes-linha">
            <button type="button" className="botao-secundario" disabled={ocupado} onClick={() => setAcrescentando(true)}>
              Acrescentar outro banco
            </button>
            <button type="button" className="botao-perigo" disabled={ocupado} onClick={() => setConfirmando({ tipo: "conexao" })}>
              Apagar minha conexão
            </button>
          </div>
        )
      )}
    </>
  );
}

/** A conexão do outro cônjuge: só leitura (D21). */
function ConexaoDoConjuge({ conexao }: { conexao: Conexao | null }) {
  if (!conexao) return <p className="cartao-nota">Ainda sem conexão. Cada um cadastra a própria, nesta mesma tela.</p>;
  return (
    <>
      <p className="cartao-nota" style={{ margin: 0 }}>
        Só {conexao.apelido} altera esta conexão. Chaves no <ChavesNoSlot slot={n(conexao.slot)} />.
      </p>
      {conexao.items.length === 0 ? (
        <p className="cartao-nota">Nenhum banco nesta conexão.</p>
      ) : (
        <ul className="lista-itens">
          {conexao.items.map((item) => (
            <li key={item.id} className="item-linha">
              <EstadoDoBanco item={item} semBanco={`Falta ${conexao.apelido} escolher o banco.`} />
            </li>
          ))}
        </ul>
      )}
    </>
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

export function Conexoes() {
  const conexoes = useConexoes();
  const [aviso, setAviso] = useState<string>();
  const [voltas, setVoltas] = useState(0);
  const titulo = useRef<HTMLHeadingElement>(null);
  const { sua, outra } = separarConexoes(conexoes.data?.conexoes ?? []);

  // Depois de concluir ou cancelar, o que tinha o foco some: ele volta ao título da seção (2.4.3).
  useEffect(() => {
    if (voltas > 0) titulo.current?.focus();
  }, [voltas]);

  const voltar = () => setVoltas((v) => v + 1);
  const avisar = (texto: string) => {
    setAviso(texto);
    voltar();
  };

  return (
    <>
      <header style={{ display: "flex", flexDirection: "column", gap: 6 }}>
        <h1 className="titulo">Conexões</h1>
        <span className="subtitulo">Open Finance pelo Meu Pluggy, só leitura. Cada um conecta os próprios bancos; o outro acompanha.</span>
      </header>

      <p role="status" className={aviso ? "aviso-form" : "sr-only"}>
        {aviso ?? ""}
      </p>

      {conexoes.isPending && (
        <section className="cartao" aria-busy="true" aria-label="Carregando as conexões">
          <Carregando />
        </section>
      )}

      {conexoes.isError && (
        <section className="cartao erro-secao" role="alert">
          <span>{conexoes.error.message}</span>
          <button type="button" className="botao-secundario" onClick={() => void conexoes.refetch()}>
            Tentar de novo
          </button>
        </section>
      )}

      {conexoes.data && (
        <>
          <section className="cartao" aria-labelledby="titulo-sua-conexao">
            <h2 id="titulo-sua-conexao" ref={titulo} tabIndex={-1} className="cartao-rotulo" style={{ margin: 0 }}>
              Sua conexão
            </h2>
            {sua ? (
              <SuaConexao conexao={sua} bancos={conexoes.data.bancos} aoAvisar={avisar} aoVoltar={voltar} />
            ) : (
              <NovaConexao slots={conexoes.data.slots} bancos={conexoes.data.bancos} aoConcluir={avisar} />
            )}
          </section>

          <section className="cartao" aria-labelledby="titulo-conexao-conjuge">
            <h2 id="titulo-conexao-conjuge" className="cartao-rotulo" style={{ margin: 0 }}>
              {outra ? `Conexão de ${outra.apelido}` : "Conexão do cônjuge"}
            </h2>
            <ConexaoDoConjuge conexao={outra} />
          </section>
        </>
      )}
    </>
  );
}
