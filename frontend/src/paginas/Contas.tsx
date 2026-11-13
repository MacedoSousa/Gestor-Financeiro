import { useState, type FormEvent } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { ErroDeApi, useEu } from "../api/autenticacao";
import { atualizarConta, criarConta, rotuloDoTipo, TIPOS_DE_CONTA, useContas, useSaldos, type Conta, type NovaConta } from "../api/contas";
import { n } from "../api/formulario";
import { hojeIso } from "../api/lancamentos";
import { centavosDeTexto, formatarReais, textoDeCentavos } from "../lib/dinheiro";
import { Alerta, Campo, CampoSelecao } from "./Campo";
import { seloDaConta } from "../lib/nascimento";

const formatarData = (iso: string) => `${iso.slice(8, 10)}/${iso.slice(5, 7)}/${iso.slice(0, 4)}`;

type Formulario = {
  tipo: string;
  apelido: string;
  instituicao: string;
  titular: string;
  agencia: string;
  numero: string;
  saldoInicial: string;
  saldoNegativo: boolean;
  saldoInicialEm: string;
  diaFechamento: string;
  diaVencimento: string;
  limite: string;
  arquivada: boolean;
};

const novoFormulario = (vazio: boolean): Formulario => ({
  tipo: "CORRENTE",
  apelido: vazio ? "Itaú conjunta" : "",
  instituicao: vazio ? "Itaú" : "",
  titular: "",
  agencia: "",
  numero: "",
  saldoInicial: "",
  saldoNegativo: false,
  saldoInicialEm: hojeIso(),
  diaFechamento: "",
  diaVencimento: "",
  limite: "",
  arquivada: false,
});

/** Agência e número nunca voltam ao formulário: ficam em branco e, em branco, a API mantém o que está guardado. */
const formularioDe = (c: Conta): Formulario => ({
  tipo: c.tipo,
  apelido: c.apelido,
  instituicao: c.instituicao ?? "",
  titular: c.titular?.usuarioId ?? "",
  agencia: "",
  numero: "",
  saldoInicial: textoDeCentavos(n(c.saldoInicialCentavos)),
  saldoNegativo: n(c.saldoInicialCentavos) < 0,
  saldoInicialEm: c.saldoInicialEm,
  diaFechamento: c.diaFechamento != null ? String(c.diaFechamento) : "",
  diaVencimento: c.diaVencimento != null ? String(c.diaVencimento) : "",
  limite: c.limiteCentavos != null ? textoDeCentavos(n(c.limiteCentavos)) : "",
  arquivada: c.arquivada,
});

const dia = (texto: string) => (/^\d{1,2}$/.test(texto) && Number(texto) >= 1 && Number(texto) <= 31 ? Number(texto) : null);

function ContaNaLista({ conta, saldo, aoEditar }: { conta: Conta; saldo?: { saldoCentavos: number | string; faturaAberta?: Fatura | null; faturaFechada?: Fatura | null }; aoEditar: () => void }) {
  const dados = [conta.instituicao, conta.agenciaMascarada && `Ag. ${conta.agenciaMascarada}`, conta.numeroMascarado && `C/C ${conta.numeroMascarado}`]
    .filter(Boolean)
    .join(" · ");
  return (
    <li className="item-linha">
      <div className="item-texto">
        <span style={{ fontWeight: 500 }}>
          {conta.apelido}
          {conta.arquivada && <span className="texto-fraco"> (arquivada)</span>}
          {seloDaConta(conta) && (
            <>
              {" "}
              <span className="selo selo-open-finance selo-conta">{seloDaConta(conta)}</span>
            </>
          )}
        </span>
        <span className="cartao-nota">
          {rotuloDoTipo(conta.tipo)} · {conta.titular?.apelido ?? "Conjunta"}
          {dados && ` · ${dados}`}
        </span>
        {saldo?.faturaAberta && <FaturaLinha rotulo="Fatura aberta" fatura={saldo.faturaAberta} />}
        {saldo?.faturaFechada && <FaturaLinha rotulo="Fatura fechada" fatura={saldo.faturaFechada} />}
      </div>
      <div className="item-acoes">
        {saldo && <span className="num saldo">{formatarReais(n(saldo.saldoCentavos))}</span>}
        <button type="button" className="botao-secundario" aria-label={`Editar ${conta.apelido}`} onClick={aoEditar}>
          Editar
        </button>
      </div>
    </li>
  );
}

type Fatura = { vencimento: string; fechamento: string; totalCentavos: number | string };

function FaturaLinha({ rotulo, fatura }: { rotulo: string; fatura: Fatura }) {
  return (
    <span className="cartao-nota">
      {rotulo}: vence {formatarData(fatura.vencimento)} (fecha {formatarData(fatura.fechamento)}) ·{" "}
      <span className="num">{formatarReais(n(fatura.totalCentavos))}</span>
    </span>
  );
}

export function Contas() {
  const consultas = useQueryClient();
  const eu = useEu();
  const contas = useContas();
  const saldos = useSaldos();
  const vazio = contas.data?.length === 0;
  const [editando, setEditando] = useState<string | null>(null);
  const [form, setForm] = useState<Formulario | null>(null);
  const [enviando, setEnviando] = useState(false);
  const [erro, setErro] = useState<string>();
  const [tentativa, setTentativa] = useState(0);
  const [aviso, setAviso] = useState<string>();

  // O formulário nasce quando a lista chega (vazia → pré-preenchido com a conta conjunta Itaú).
  const f = form ?? novoFormulario(vazio);
  const mudar = <K extends keyof Formulario>(campo: K) => (valor: Formulario[K]) => setForm({ ...f, [campo]: valor });
  const cartao = f.tipo === "CARTAO";

  const titulares = [
    { valor: "", rotulo: "Conjunta" },
    ...(eu.data ? [{ valor: eu.data.usuarioId, rotulo: eu.data.apelido }] : []),
    ...(eu.data?.usuarioIdDoConjuge ? [{ valor: eu.data.usuarioIdDoConjuge, rotulo: eu.data.apelidoDoConjuge }] : []),
  ];

  function falhar(mensagem: string) {
    setErro(mensagem);
    setTentativa((t) => t + 1);
  }

  function abrir(conta: Conta | null) {
    setEditando(conta?.id ?? null);
    setForm(conta ? formularioDe(conta) : novoFormulario(false));
    setErro(undefined);
    setAviso(undefined);
  }

  async function salvar(e: FormEvent) {
    e.preventDefault();
    setErro(undefined);
    setAviso(undefined);
    const saldo = f.saldoInicial.trim() === "" ? 0 : centavosDeTexto(f.saldoInicial);
    if (saldo === null) return falhar("Saldo inicial inválido: use o formato 1.234,56.");
    const limite = f.limite.trim() === "" ? null : centavosDeTexto(f.limite);
    if (cartao && f.limite.trim() !== "" && limite === null) return falhar("Limite inválido: use o formato 1.234,56.");
    if (cartao && (dia(f.diaFechamento) === null || dia(f.diaVencimento) === null))
      return falhar("Informe os dias de fechamento e de vencimento (1 a 31).");

    const corpo: NovaConta = {
      tipo: f.tipo,
      apelido: f.apelido.trim(),
      instituicao: f.instituicao.trim() || null,
      titularUsuarioId: f.titular || null,
      // Em branco: na criação não informa; na edição mantém o que está guardado.
      agencia: f.agencia.trim() || null,
      numero: f.numero.trim() || null,
      saldoInicialCentavos: f.saldoNegativo ? -saldo : saldo,
      saldoInicialEm: f.saldoInicialEm,
      diaFechamento: cartao ? dia(f.diaFechamento) : null,
      diaVencimento: cartao ? dia(f.diaVencimento) : null,
      limiteCentavos: cartao ? limite : null,
      arquivada: editando ? f.arquivada : null,
    };

    setEnviando(true);
    try {
      if (editando) await atualizarConta(editando, corpo);
      else await criarConta(corpo);
      setAviso(editando ? "Conta atualizada." : "Conta criada.");
      setEditando(null);
      setForm(novoFormulario(false));
      await Promise.all(["contas", "saldos", "lancamentos", "visao-geral"].map((k) => consultas.invalidateQueries({ queryKey: [k] })));
    } catch (err) {
      if (err instanceof ErroDeApi && err.status === 401) void consultas.invalidateQueries({ queryKey: ["eu"] });
      falhar(err instanceof ErroDeApi ? err.message : "Não foi possível salvar. Tente de novo.");
    } finally {
      setEnviando(false);
    }
  }

  const saldoPorConta = new Map((saldos.data ?? []).map((s) => [s.contaId, s]));

  return (
    <>
      <header style={{ display: "flex", flexDirection: "column", gap: 6 }}>
        <h1 className="titulo">Contas e cartões</h1>
        <span className="subtitulo">Saldos de hoje e faturas dos cartões.</span>
      </header>

      {aviso && (
        <p role="status" className="aviso-form">
          {aviso}
        </p>
      )}

      <div className="duas-colunas">
        <section className="cartao coluna-principal" aria-labelledby="titulo-lista-contas">
          <h2 id="titulo-lista-contas" className="cartao-titulo">
            Suas contas
          </h2>
          {(contas.isPending || saldos.isPending) && <p className="cartao-nota">Carregando…</p>}
          {contas.isError && <p className="alerta-form">{contas.error.message}</p>}
          {saldos.isError && <p className="alerta-form">{saldos.error.message}</p>}
          {vazio && <p className="cartao-nota">Nenhuma conta ainda. Comece pela conta conjunta Itaú.</p>}
          {contas.data && contas.data.length > 0 && (
            <ul className="lista-itens">
              {contas.data.map((c) => (
                <ContaNaLista key={c.id} conta={c} saldo={saldoPorConta.get(c.id)} aoEditar={() => abrir(c)} />
              ))}
            </ul>
          )}
        </section>

        <aside className="coluna-lateral">
          <form className="cartao" onSubmit={(e) => void salvar(e)} noValidate aria-labelledby="titulo-form-conta">
            <h2 id="titulo-form-conta" className="cartao-titulo">
              {editando ? "Editar conta" : vazio ? "Comece pela conta conjunta Itaú" : "Nova conta ou cartão"}
            </h2>
            <Alerta mensagem={erro} tentativa={tentativa} />
            <CampoSelecao rotulo="Tipo" valor={f.tipo} aoMudar={mudar("tipo")} opcoes={TIPOS_DE_CONTA} />
            <Campo rotulo="Nome da conta" valor={f.apelido} aoMudar={mudar("apelido")} maxLength={60} autoComplete="off" />
            <Campo rotulo="Instituição (opcional)" valor={f.instituicao} aoMudar={mudar("instituicao")} maxLength={60} autoComplete="off" />
            <CampoSelecao rotulo="Titular" valor={f.titular} aoMudar={mudar("titular")} opcoes={titulares} />
            {!cartao && (
              <>
                <Campo
                  rotulo="Agência (opcional)"
                  valor={f.agencia}
                  aoMudar={mudar("agencia")}
                  autoComplete="off"
                  inputMode="numeric"
                  nota={editando ? "Em branco mantém a agência guardada." : undefined}
                />
                <Campo
                  rotulo="Número da conta (opcional)"
                  valor={f.numero}
                  aoMudar={mudar("numero")}
                  autoComplete="off"
                  nota={editando ? "Em branco mantém o número guardado." : "Guardado cifrado; depois só aparece mascarado."}
                />
              </>
            )}
            <Campo
              rotulo={cartao ? "Fatura em aberto na data (opcional)" : "Saldo inicial"}
              valor={f.saldoInicial}
              aoMudar={mudar("saldoInicial")}
              inputMode="decimal"
              classeEntrada="num"
              autoComplete="off"
              placeholder="0,00"
            />
            <label className="campo-check">
              <input type="checkbox" checked={f.saldoNegativo} onChange={(e) => mudar("saldoNegativo")(e.target.checked)} />
              {cartao ? "É dívida (saldo negativo)" : "Saldo negativo"}
            </label>
            <Campo rotulo="Data do saldo inicial" tipo="date" valor={f.saldoInicialEm} aoMudar={mudar("saldoInicialEm")} />
            {cartao && (
              <>
                <Campo rotulo="Dia do fechamento" valor={f.diaFechamento} aoMudar={mudar("diaFechamento")} inputMode="numeric" maxLength={2} />
                <Campo rotulo="Dia do vencimento" valor={f.diaVencimento} aoMudar={mudar("diaVencimento")} inputMode="numeric" maxLength={2} />
                <Campo rotulo="Limite (opcional)" valor={f.limite} aoMudar={mudar("limite")} inputMode="decimal" classeEntrada="num" autoComplete="off" />
              </>
            )}
            {editando && (
              <label className="campo-check">
                <input type="checkbox" checked={f.arquivada} onChange={(e) => mudar("arquivada")(e.target.checked)} />
                Arquivar (some dos formulários, continua no extrato)
              </label>
            )}
            <div className="acoes-linha">
              <button type="submit" className="botao-primario" disabled={enviando}>
                {enviando ? "Salvando…" : editando ? "Salvar" : "Criar conta"}
              </button>
              {editando && (
                <button type="button" className="botao-secundario" onClick={() => abrir(null)}>
                  Cancelar
                </button>
              )}
            </div>
          </form>
        </aside>
      </div>
    </>
  );
}
