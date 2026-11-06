import { useEffect, useRef, useState, type FormEvent } from "react";
import { Link, useNavigate } from "react-router";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import type { ErroDeApi } from "../api/autenticacao";
import { pedirDispositivo, recuperarAcesso, resgatarPedido, useEstadoDoPedido } from "../api/dispositivos";
import { Alerta, Campo, PaginaPublica } from "./Campo";
import { CodigoDeRecuperacao } from "./CodigoDeRecuperacao";
import { mascararCpf } from "../lib/cpf";

export function NovoDispositivo() {
  const navegar = useNavigate();
  const consultas = useQueryClient();
  const [modo, setModo] = useState<"aprovacao" | "recuperacao">("aprovacao");
  const [cpf, setCpf] = useState("");
  const [codigoRecuperacao, setCodigoRecuperacao] = useState("");

  const pedir = useMutation<{ codigo: string }, ErroDeApi, string>({ mutationFn: pedirDispositivo });
  const recuperar = useMutation<{ codigoRecuperacao: string }, ErroDeApi, { cpf: string; codigo: string }>({
    mutationFn: (v) => recuperarAcesso(v.cpf, v.codigo),
    onSuccess: () => setCodigoRecuperacao(""),
  });
  const resgatar = useMutation<void, ErroDeApi>({ mutationFn: resgatarPedido, onSuccess: () => void navegar("/registrar") });

  // Só o pedido atual decide: um erro de resgate antigo não liga nem desliga a consulta.
  const estado = useEstadoDoPedido(pedir.isSuccess);

  // Segredos (código de recuperação) não ficam no cache ao sair da tela.
  const { reset: limparRecuperacao } = recuperar;
  useEffect(() => () => limparRecuperacao(), [limparRecuperacao]);

  // Resgata uma única vez: o pedido é de uso único.
  const resgatou = useRef(false);
  useEffect(() => {
    if (estado.data === "APROVADO" && !resgatou.current) {
      resgatou.current = true;
      resgatar.mutate();
    }
  }, [estado.data, resgatar]);

  function pedirDeNovo() {
    // O estado APROVADO antigo não pode disparar outro resgate para o pedido novo.
    consultas.removeQueries({ queryKey: ["pedido-estado"] });
    resgatou.current = false;
    resgatar.reset();
    pedir.reset();
  }

  if (recuperar.data) {
    return (
      <CodigoDeRecuperacao
        titulo="Novo código de recuperação"
        codigo={recuperar.data.codigoRecuperacao}
        aoConcluir={() => {
          recuperar.reset();
          void navegar("/registrar");
        }}
      />
    );
  }

  function enviarPedido(e: FormEvent) {
    e.preventDefault();
    pedir.mutate(cpf);
  }
  function enviarRecuperacao(e: FormEvent) {
    e.preventDefault();
    recuperar.mutate({ cpf, codigo: codigoRecuperacao });
  }

  const expirado = estado.data === "EXPIRADO";

  if (pedir.data && modo === "aprovacao") {
    return (
      <PaginaPublica titulo="Celular novo" subtitulo="Aguardando a aprovação do outro cônjuge.">
        <Alerta mensagem={resgatar.error?.message ?? estado.error?.message} />
        <section className="cartao destaque" aria-labelledby="titulo-pedido">
          <h2 id="titulo-pedido" className="cartao-rotulo" style={{ margin: 0 }}>
            Código do pedido
          </h2>
          <p className="codigo-pedido num">
            {pedir.data.codigo}
          </p>
          <p className="cartao-nota">Peça para o outro cônjuge aprovar em Dispositivos. O código vale por 10 minutos.</p>
          {expirado || resgatar.isError ? (
            <div>
              <button type="button" className="botao-secundario" onClick={pedirDeNovo}>
                Pedir de novo
              </button>
            </div>
          ) : (
            <p role="status" className="cartao-nota">
              Conferindo a cada 10 segundos…
            </p>
          )}
        </section>
        <nav className="links-acesso" aria-label="Voltar">
          <Link to="/entrar">Voltar para entrar</Link>
        </nav>
      </PaginaPublica>
    );
  }

  return (
    <PaginaPublica
      titulo="Celular novo"
      subtitulo={
        modo === "aprovacao"
          ? "Informe seu CPF. O outro cônjuge aprova a entrada deste aparelho."
          : "Use o código de recuperação impresso no cadastro. Ele será trocado por um novo."
      }
    >
      <Alerta mensagem={pedir.error?.message ?? recuperar.error?.message} tentativa={pedir.submittedAt + recuperar.submittedAt} />
      {modo === "aprovacao" ? (
        <form className="cartao" onSubmit={enviarPedido} noValidate>
          <Campo rotulo="CPF" valor={cpf} aoMudar={(v) => setCpf(mascararCpf(v))} inputMode="numeric" autoComplete="username" />
          <div>
            <button type="submit" className="botao-primario" disabled={pedir.isPending}>
              {pedir.isPending ? "Pedindo…" : "Pedir aprovação"}
            </button>
          </div>
        </form>
      ) : (
        <form className="cartao" onSubmit={enviarRecuperacao} noValidate>
          <Campo rotulo="CPF" valor={cpf} aoMudar={(v) => setCpf(mascararCpf(v))} inputMode="numeric" autoComplete="username" />
          <Campo rotulo="Código de recuperação" valor={codigoRecuperacao} aoMudar={setCodigoRecuperacao} autoComplete="off" />
          <div>
            <button type="submit" className="botao-primario" disabled={recuperar.isPending}>
              {recuperar.isPending ? "Conferindo…" : "Usar código de recuperação"}
            </button>
          </div>
        </form>
      )}
      <nav className="links-acesso" aria-label="Outras opções">
        <button
          type="button"
          className="botao-link"
          onClick={() => {
            pedir.reset();
            recuperar.reset();
            setModo(modo === "aprovacao" ? "recuperacao" : "aprovacao");
          }}
        >
          {modo === "aprovacao" ? "Tenho o código de recuperação" : "Prefiro pedir aprovação do cônjuge"}
        </button>
        <Link to="/entrar">Voltar para entrar</Link>
      </nav>
    </PaginaPublica>
  );
}
