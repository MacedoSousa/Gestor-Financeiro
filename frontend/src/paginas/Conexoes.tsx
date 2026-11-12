import { useId, useState, type FormEvent } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { ErroDeApi } from "../api/autenticacao";
import { n } from "../api/formulario";
import { useAcoesDeConexoes, useConexoes, type Conexao, type ItemDaConexao, type Slot } from "../api/conexoes";
import {
  avisoDoSlot,
  BANCO_MAXIMO,
  estadoDoItem,
  ITEM_ID_MAXIMO,
  rotuloDoSlot,
  separarConexoes,
  textoDoConsentimento,
  validarCadastro,
  type ErrosDoCadastro,
} from "../lib/conexoes";
import { Alerta, Campo } from "./Campo";

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

function EstadoDoBanco({ item }: { item: ItemDaConexao }) {
  const aparencia = estadoDoItem(item.estado, item.motivo);
  const consentimento = textoDoConsentimento(item.consentimentoVenceEm);
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
    </div>
  );
}

/** Escolha explícita do slot (D20, D23): nada vem marcado; slot em uso ou sem chaves fica desabilitado, com o motivo. */
function EscolhaDoSlot({ slots, valor, aoMudar, erro }: { slots: Slot[]; valor: number | null; aoMudar: (s: number) => void; erro?: string }) {
  const id = useId();
  return (
    <fieldset className="escolha" aria-describedby={erro ? `${id}-erro` : `${id}-nota`}>
      <legend className="campo-rotulo">Em qual slot estão as suas chaves?</legend>
      <span id={`${id}-nota`} className="cartao-nota">
        Cada cônjuge grava as próprias chaves do Meu Pluggy num slot do servidor. Escolha o seu.
      </span>
      {slots.map((s) => {
        const numero = n(s.slot);
        const aviso = avisoDoSlot({ slot: numero, configurado: s.configurado, emUso: s.emUso });
        return (
          <label key={numero} className="campo-check opcao-slot">
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
              <span className="sr-only"> ({rotuloDoSlot(numero)})</span>
              {aviso && (
                <span id={`${id}-aviso-${numero}`} className="cartao-nota" style={{ display: "block" }}>
                  {aviso}
                </span>
              )}
            </span>
          </label>
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
function NovaConexao({ slots, aoConcluir }: { slots: Slot[]; aoConcluir: (banco: string) => void }) {
  const acoes = useAcoesDeConexoes();
  const { erro, tentativa, ocupado, tentar } = useTentativa();
  const [slot, setSlot] = useState<number | null>(null);
  const [itemId, setItemId] = useState("");
  const [banco, setBanco] = useState("");
  const [erros, setErros] = useState<ErrosDoCadastro>({});
  const nenhumSlot = slots.every((s) => avisoDoSlot({ slot: n(s.slot), configurado: s.configurado, emUso: s.emUso }) !== null);

  async function enviar(e: FormEvent) {
    e.preventDefault();
    const encontrados = validarCadastro({ slot, itemId, banco });
    setErros(encontrados);
    if (Object.keys(encontrados).length > 0 || slot === null) return;
    const nome = banco.trim();
    if (await tentar(async () => void (await acoes.conectar(slot, itemId, banco)))) aoConcluir(nome);
  }

  return (
    <form className="bloco-form" noValidate onSubmit={(e) => void enviar(e)} aria-labelledby="titulo-sua-conexao">
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
      <Campo rotulo="Banco" valor={banco} aoMudar={setBanco} erro={erros.banco} placeholder="Itaú" maxLength={BANCO_MAXIMO} />
      <Alerta mensagem={erro} tentativa={tentativa} />
      <div>
        <button type="submit" className="botao-primario" disabled={ocupado || nenhumSlot}>
          {ocupado ? "Consultando o banco…" : "Conectar"}
        </button>
      </div>
    </form>
  );
}

/** Mais um banco na própria conexão. */
function NovoBanco({ conexao, aoConcluir, aoCancelar }: { conexao: Conexao; aoConcluir: (banco: string) => void; aoCancelar: () => void }) {
  const acoes = useAcoesDeConexoes();
  const { erro, tentativa, ocupado, tentar } = useTentativa();
  const [itemId, setItemId] = useState("");
  const [banco, setBanco] = useState("");
  const [erros, setErros] = useState<ErrosDoCadastro>({});

  async function enviar(e: FormEvent) {
    e.preventDefault();
    const encontrados = validarCadastro({ itemId, banco });
    setErros(encontrados);
    if (Object.keys(encontrados).length > 0) return;
    const nome = banco.trim();
    if (await tentar(async () => void (await acoes.acrescentarBanco(conexao.id, itemId, banco)))) aoConcluir(nome);
  }

  return (
    <form className="confirmacao" noValidate onSubmit={(e) => void enviar(e)} aria-label="Acrescentar banco">
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
      <Campo rotulo="Banco" valor={banco} aoMudar={setBanco} erro={erros.banco} placeholder="Nubank" maxLength={BANCO_MAXIMO} />
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

type Confirmando = { tipo: "banco"; item: ItemDaConexao } | { tipo: "conexao" } | null;

/** A conexão de quem está na sessão: bancos, acrescentar, tirar e apagar (só o titular; D21). */
function SuaConexao({ conexao, aoAvisar }: { conexao: Conexao; aoAvisar: (texto: string) => void }) {
  const acoes = useAcoesDeConexoes();
  const { erro, tentativa, ocupado, tentar } = useTentativa();
  const [acrescentando, setAcrescentando] = useState(false);
  const [confirmando, setConfirmando] = useState<Confirmando>(null);

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
        Chaves no <span className="num">{rotuloDoSlot(n(conexao.slot))}</span>
        {conexao.slotConfigurado ? "" : " · os arquivos estão vazios no servidor"}
      </p>
      {conexao.items.length === 0 ? (
        <p className="cartao-nota">Nenhum banco nesta conexão. Acrescente um para começar.</p>
      ) : (
        <ul className="lista-itens">
          {conexao.items.map((item) => (
            <li key={item.id} className="item-linha">
              <EstadoDoBanco item={item} />
              <button
                type="button"
                className="botao-secundario"
                disabled={ocupado}
                aria-label={`Tirar ${item.banco} da conexão`}
                onClick={() => setConfirmando({ tipo: "banco", item })}
              >
                Tirar
              </button>
            </li>
          ))}
        </ul>
      )}

      {confirmando && (
        <div className="confirmacao" role="group" aria-labelledby="texto-confirmar-conexao">
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
            <button type="button" className="botao-secundario" disabled={ocupado} onClick={() => setConfirmando(null)}>
              Cancelar
            </button>
          </div>
        </div>
      )}

      {acrescentando ? (
        <NovoBanco
          conexao={conexao}
          aoCancelar={() => setAcrescentando(false)}
          aoConcluir={(banco) => {
            setAcrescentando(false);
            aoAvisar(`${banco} entrou na sua conexão.`);
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
        Só {conexao.apelido} altera esta conexão. Chaves no <span className="num">{rotuloDoSlot(n(conexao.slot))}</span>.
      </p>
      {conexao.items.length === 0 ? (
        <p className="cartao-nota">Nenhum banco nesta conexão.</p>
      ) : (
        <ul className="lista-itens">
          {conexao.items.map((item) => (
            <li key={item.id} className="item-linha">
              <EstadoDoBanco item={item} />
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
  const { sua, outra } = separarConexoes(conexoes.data?.conexoes ?? []);

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
            <h2 id="titulo-sua-conexao" className="cartao-rotulo" style={{ margin: 0 }}>
              Sua conexão
            </h2>
            {sua ? (
              <SuaConexao conexao={sua} aoAvisar={setAviso} />
            ) : (
              <NovaConexao slots={conexoes.data.slots} aoConcluir={(banco) => setAviso(`Conexão criada. ${banco} foi aceito pelo banco.`)} />
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
