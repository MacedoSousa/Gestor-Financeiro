import { useEffect, useRef, useState, type FormEvent } from "react";
import { useNavigate } from "react-router";
import { useQueryClient } from "@tanstack/react-query";
import { confirmarComPasskey, confirmarComTotp, ErroDeApi, useEu } from "../api/autenticacao";
import {
  aprovarPedido,
  autorizarNovaCredencial,
  destravarConjuge,
  exigeConfirmacao,
  gerarCodigoDeRecuperacao,
  removerCredencial,
  useCredenciais,
  usePedidos,
} from "../api/dispositivos";
import { Alerta, Campo } from "./Campo";
import { CodigoDeRecuperacao } from "./CodigoDeRecuperacao";

const formatarData = (iso: string) => new Date(iso).toLocaleDateString("pt-BR");
const rotuloDoTipo = (tipo: string) => (tipo.toUpperCase() === "PASSKEY" ? "Passkey" : "Aplicativo autenticador");

type Acao = () => Promise<void>;

type DialogoProps = {
  temPasskey: boolean;
  temTotp: boolean;
  aoConfirmar: () => void;
  aoCancelar: () => void;
};

/** Confirmação recente (passkey ou código TOTP) antes de repetir a ação sensível. */
function ConfirmarIdentidade({ temPasskey, temTotp, aoConfirmar, aoCancelar }: DialogoProps) {
  const ref = useRef<HTMLDialogElement>(null);
  const [codigo, setCodigo] = useState("");
  const [erro, setErro] = useState<string>();
  const [ocupado, setOcupado] = useState(false);

  useEffect(() => {
    const d = ref.current;
    if (d && !d.open) d.showModal();
  }, []);

  async function confirmar(acao: () => Promise<void>) {
    setErro(undefined);
    setOcupado(true);
    try {
      await acao();
      setCodigo("");
      aoConfirmar();
    } catch (e) {
      setErro(e instanceof ErroDeApi ? e.message : "Não foi possível confirmar. Tente de novo.");
      setOcupado(false);
    }
  }

  const mostrarTotp = temTotp || !temPasskey;
  return (
    <dialog ref={ref} className="dialogo" aria-labelledby="titulo-confirmar" onCancel={aoCancelar}>
      <h2 id="titulo-confirmar" className="dialogo-titulo">
        Confirme sua identidade
      </h2>
      <p className="cartao-nota">Esta ação é sensível. Confirme que é você para continuar.</p>
      <Alerta mensagem={erro} />
      {temPasskey && (
        <div>
          <button type="button" className="botao-primario" disabled={ocupado} onClick={() => void confirmar(confirmarComPasskey)}>
            {ocupado ? "Aguardando o aparelho…" : "Confirmar com passkey"}
          </button>
        </div>
      )}
      {mostrarTotp && (
        <form
          className="campo"
          noValidate
          onSubmit={(e: FormEvent) => {
            e.preventDefault();
            void confirmar(() => confirmarComTotp(codigo.replace(/\s/g, "")));
          }}
        >
          <Campo
            rotulo="Código do aplicativo (6 dígitos)"
            valor={codigo}
            aoMudar={(v) => setCodigo(v.replace(/\D/g, "").slice(0, 6))}
            inputMode="numeric"
            autoComplete="one-time-code"
            maxLength={6}
          />
          <div>
            <button type="submit" className={temPasskey ? "botao-secundario" : "botao-primario"} disabled={ocupado}>
              Confirmar com código
            </button>
          </div>
        </form>
      )}
      <div>
        <button type="button" className="botao-secundario" onClick={aoCancelar}>
          Cancelar
        </button>
      </div>
    </dialog>
  );
}

export function Dispositivos() {
  const navegar = useNavigate();
  const consultas = useQueryClient();
  const eu = useEu();
  const credenciais = useCredenciais();
  const pedidos = usePedidos();

  const [erro, setErro] = useState<string>();
  const [tentativaDeErro, setTentativaDeErro] = useState(0);
  const [aviso, setAviso] = useState<string>();
  const [confirmando, setConfirmando] = useState<Acao | null>(null);
  const [novoCodigo, setNovoCodigo] = useState<string | null>(null);
  const [ocupado, setOcupado] = useState(false);

  /** Falha comum: mostra a mensagem. 401 invalida a sessão (o Layout leva a /entrar, uma única vez). */
  function tratar(e: unknown) {
    if (e instanceof ErroDeApi && e.status === 401) void consultas.invalidateQueries({ queryKey: ["eu"] });
    setErro(e instanceof ErroDeApi ? e.message : "Não foi possível concluir. Tente de novo.");
    setTentativaDeErro((n) => n + 1);
  }

  /** Executa a ação; se a API pedir confirmação, abre o diálogo e repete a ação uma única vez. */
  async function tentar(acao: Acao) {
    setErro(undefined);
    setAviso(undefined);
    setOcupado(true);
    try {
      await acao();
    } catch (e) {
      if (exigeConfirmacao(e)) setConfirmando(() => acao);
      else tratar(e);
    } finally {
      setOcupado(false);
    }
  }

  async function repetir() {
    const acao = confirmando;
    setConfirmando(null);
    if (!acao) return;
    setOcupado(true);
    try {
      await acao();
    } catch (e) {
      tratar(e);
    } finally {
      setOcupado(false);
    }
  }

  const lista = credenciais.data ?? [];
  const temPasskey = lista.some((c) => c.tipo.toUpperCase() === "PASSKEY");
  const temTotp = lista.some((c) => c.tipo.toUpperCase() === "TOTP");
  const conjuge = eu.data?.apelidoDoConjuge ?? "o cônjuge";

  if (novoCodigo) {
    return <CodigoDeRecuperacao embutido titulo="Novo código de recuperação" codigo={novoCodigo} aoConcluir={() => setNovoCodigo(null)} />;
  }

  return (
    <>
      <header style={{ display: "flex", flexDirection: "column", gap: 6 }}>
        <h1 className="titulo">Dispositivos</h1>
        <span className="subtitulo">Formas de entrar, celulares novos e acesso do casal.</span>
      </header>

      <Alerta mensagem={erro} tentativa={tentativaDeErro} />
      {aviso && (
        <p role="status" className="aviso-form">
          {aviso}
        </p>
      )}

      <section className="cartao" aria-labelledby="titulo-credenciais">
        <h2 id="titulo-credenciais" className="cartao-rotulo" style={{ margin: 0 }}>
          Formas de entrar
        </h2>
        {credenciais.isPending && <p className="cartao-nota">Carregando…</p>}
        {credenciais.isError && <p className="cartao-nota">{credenciais.error.message}</p>}
        {credenciais.data && (
          <ul className="lista-itens">
            {lista.map((c) => (
              <li key={c.id} className="item-linha">
                <div className="item-texto">
                  <span style={{ fontWeight: 500 }}>{c.apelido}</span>
                  <span className="cartao-nota">
                    {rotuloDoTipo(c.tipo)} · criada em {formatarData(c.criadaEm)}
                    {c.ultimoUsoEm ? ` · último uso em ${formatarData(c.ultimoUsoEm)}` : ""}
                  </span>
                </div>
                <button
                  type="button"
                  className="botao-secundario"
                  disabled={ocupado}
                  aria-label={`Remover ${c.apelido}`}
                  onClick={() =>
                    void tentar(async () => {
                      await removerCredencial(c.id);
                      await consultas.invalidateQueries({ queryKey: ["credenciais"] });
                      setAviso(`“${c.apelido}” removida.`);
                    })
                  }
                >
                  Remover
                </button>
              </li>
            ))}
          </ul>
        )}
        <div>
          <button
            type="button"
            className="botao-secundario"
            disabled={ocupado}
            onClick={() =>
              void tentar(async () => {
                await autorizarNovaCredencial();
                void navegar("/registrar");
              })
            }
          >
            Adicionar outra forma de entrar
          </button>
        </div>
      </section>

      <section className="cartao" aria-labelledby="titulo-pedidos">
        <h2 id="titulo-pedidos" className="cartao-rotulo" style={{ margin: 0 }}>
          Pedidos de celular novo
        </h2>
        {pedidos.isError && <p className="cartao-nota">{pedidos.error.message}</p>}
        {pedidos.data && pedidos.data.length === 0 && <p className="cartao-nota">Nenhum pedido pendente.</p>}
        {pedidos.data && pedidos.data.length > 0 && (
          <ul className="lista-itens">
            {pedidos.data.map((p) => (
              <li key={p.id} className="item-linha">
                <div className="item-texto">
                  <span className="num codigo-pedido-pequeno">
                    {p.codigo}
                  </span>
                  <span className="cartao-nota">Confira se o código é o mesmo que aparece no celular de {conjuge}.</span>
                </div>
                <button
                  type="button"
                  className="botao-secundario"
                  disabled={ocupado}
                  onClick={() =>
                    void tentar(async () => {
                      await aprovarPedido(p.id);
                      await consultas.invalidateQueries({ queryKey: ["pedidos"] });
                      setAviso("Pedido aprovado. O celular novo já pode continuar.");
                    })
                  }
                >
                  Aprovar
                </button>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className="cartao" aria-labelledby="titulo-casal">
        <h2 id="titulo-casal" className="cartao-rotulo" style={{ margin: 0 }}>
          Acesso do casal
        </h2>
        <div className="acoes-linha">
          <button
            type="button"
            className="botao-secundario"
            disabled={ocupado}
            onClick={() =>
              void tentar(async () => {
                await destravarConjuge();
                setAviso(`Conta de ${conjuge} destravada.`);
              })
            }
          >
            Destravar conta do cônjuge
          </button>
          <button
            type="button"
            className="botao-secundario"
            disabled={ocupado}
            onClick={() =>
              void tentar(async () => {
                setNovoCodigo(await gerarCodigoDeRecuperacao());
              })
            }
          >
            Gerar novo código de recuperação
          </button>
        </div>
        <span className="cartao-nota">O código novo invalida o anterior. Ele só aparece uma vez.</span>
      </section>

      {confirmando && (
        <ConfirmarIdentidade
          temPasskey={temPasskey}
          temTotp={temTotp}
          aoConfirmar={() => void repetir()}
          aoCancelar={() => setConfirmando(null)}
        />
      )}
    </>
  );
}
