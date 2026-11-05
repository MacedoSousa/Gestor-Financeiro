import { useState, type FormEvent } from "react";
import { Link, useNavigate } from "react-router";
import { useMutation } from "@tanstack/react-query";
import { primeiroAcesso, type ErroDeApi } from "../api/autenticacao";
import { Alerta, Campo, PaginaPublica } from "./Campo";
import { mascararCpf } from "../lib/cpf";

export function PrimeiroAcesso() {
  const navegar = useNavigate();
  const [cpf, setCpf] = useState("");
  const [senha, setSenha] = useState("");
  const enviar = useMutation<void, ErroDeApi, { cpf: string; senha: string }>({
    mutationFn: (v) => primeiroAcesso(v.cpf, v.senha),
    onSuccess: () => {
      setSenha("");
      void navegar("/registrar");
    },
  });

  function aoEnviar(e: FormEvent) {
    e.preventDefault();
    enviar.mutate({ cpf, senha });
  }

  return (
    <PaginaPublica titulo="Primeiro acesso" subtitulo="Use o CPF e a senha que você criou no cadastro. Depois você cadastra a passkey deste aparelho.">
      <Alerta mensagem={enviar.error?.message} tentativa={enviar.submittedAt} />
      <form className="cartao" onSubmit={aoEnviar} noValidate>
        <Campo rotulo="CPF" valor={cpf} aoMudar={(v) => setCpf(mascararCpf(v))} inputMode="numeric" autoComplete="username" />
        <Campo rotulo="Senha" tipo="password" valor={senha} aoMudar={setSenha} autoComplete="current-password" />
        <div>
          <button type="submit" className="botao-primario" disabled={enviar.isPending}>
            {enviar.isPending ? "Conferindo…" : "Continuar"}
          </button>
        </div>
      </form>
      <nav className="links-acesso" aria-label="Voltar">
        <Link to="/entrar">Voltar para entrar</Link>
      </nav>
    </PaginaPublica>
  );
}
