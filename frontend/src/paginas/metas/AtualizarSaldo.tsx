import { useEffect, useRef, useState, type FormEvent } from "react";
import { ErroDeApi } from "../../api/autenticacao";
import { n } from "../../api/formulario";
import { useConciliacao, type ContaDeInvestimento } from "../../api/metas";
import { previaDaConciliacao } from "../../lib/conciliacao";
import { centavosDeTexto, formatarReais } from "../../lib/dinheiro";
import { Alerta, Campo } from "../Campo";

type Props = { conta: ContaDeInvestimento; aoFechar: () => void };

/** Concilia o saldo da conta com o do banco. O saldo exibido enviado é o que está na tela agora. */
export function AtualizarSaldo({ conta, aoFechar }: Props) {
  const ref = useRef<HTMLDialogElement>(null);
  const conciliar = useConciliacao();
  const atual = n(conta.saldoCentavos);
  // Saldo congelado ao abrir: um refetch do painel não troca o número sob uma prévia já lida. Só um 409 o renova.
  const [exibido, setExibido] = useState(atual);
  const [renovar, setRenovar] = useState(false);
  if (renovar && atual !== exibido) {
    setExibido(atual);
    setRenovar(false);
  }
  const [texto, setTexto] = useState("");
  const [enviando, setEnviando] = useState(false);
  const [erro, setErro] = useState<string>();
  const [tentativa, setTentativa] = useState(0);

  useEffect(() => {
    const d = ref.current;
    if (d && !d.open) d.showModal();
  }, []);

  const informado = centavosDeTexto(texto);
  const previa = informado === null ? null : previaDaConciliacao(informado, exibido);

  async function confirmar(e: FormEvent) {
    e.preventDefault();
    if (informado === null || previa?.resultado !== "LANCADO") return;
    setErro(undefined);
    setEnviando(true);
    try {
      await conciliar(conta.contaId, informado, exibido);
      aoFechar();
    } catch (x) {
      if (x instanceof ErroDeApi && x.status === 409) setRenovar(true);
      setErro(x instanceof ErroDeApi ? x.message : "Não foi possível atualizar o saldo. Tente de novo.");
      setTentativa((t) => t + 1);
      setEnviando(false);
    }
  }

  return (
    <dialog ref={ref} className="dialogo" aria-labelledby="titulo-saldo" onCancel={(e) => (enviando ? e.preventDefault() : aoFechar())}>
      <h2 id="titulo-saldo" className="dialogo-titulo">
        Atualizar saldo de {conta.apelido}
      </h2>
      <p className="num">Saldo no sistema: {formatarReais(exibido)}</p>
      <Alerta mensagem={erro} tentativa={tentativa} />
      <form className="bloco-form" onSubmit={(e) => void confirmar(e)} noValidate>
        <Campo rotulo="Saldo no banco hoje" valor={texto} aoMudar={setTexto} inputMode="decimal" classeEntrada="num" autoComplete="off" />
        {texto.trim() !== "" && informado === null && <p className="campo-erro">Informe o saldo como 1.234,56.</p>}
        {previa && (
          <p role="status" className="num">
            {previa.mensagem}
          </p>
        )}
        <div className="acoes-linha">
          <button type="submit" className="botao-primario" disabled={enviando || previa?.resultado !== "LANCADO"}>
            {enviando ? "Confirmando…" : "Confirmar"}
          </button>
          <button type="button" className="botao-secundario" disabled={enviando} onClick={aoFechar}>
            Cancelar
          </button>
        </div>
      </form>
    </dialog>
  );
}
