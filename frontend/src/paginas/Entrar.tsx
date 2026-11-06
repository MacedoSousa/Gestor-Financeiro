import { useState, type FormEvent } from "react";
import { Link, useNavigate } from "react-router";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { entrarComPasskey, entrarComTotp, type ErroDeApi } from "../api/autenticacao";
import { Alerta, Campo, PaginaPublica } from "./Campo";
import { mascararCpf } from "../lib/cpf";

export function Entrar() {
  const navegar = useNavigate();
  const consultas = useQueryClient();
  const [cpf, setCpf] = useState("");
  const [codigo, setCodigo] = useState("");

  const aoEntrar = () => {
    consultas.removeQueries({ queryKey: ["eu"] });
    void navegar("/", { replace: true });
  };
  const passkey = useMutation<void, ErroDeApi>({ mutationFn: entrarComPasskey, onSuccess: aoEntrar });
  const totp = useMutation<void, ErroDeApi, { cpf: string; codigo: string }>({
    mutationFn: (v) => entrarComTotp(v.cpf, v.codigo),
    onSuccess: aoEntrar,
  });

  function enviarTotp(e: FormEvent) {
    e.preventDefault();
    passkey.reset();
    totp.mutate({ cpf, codigo: codigo.replace(/\s/g, "") });
  }

  return (
    <PaginaPublica titulo="Entrar" subtitulo="Acesso restrito ao casal.">
      <Alerta mensagem={passkey.error?.message ?? totp.error?.message} tentativa={passkey.submittedAt + totp.submittedAt} />

      <section className="cartao" aria-label="Passkey">
        <button
          type="button"
          className="botao-primario"
          disabled={passkey.isPending}
          onClick={() => {
            totp.reset();
            passkey.mutate();
          }}
        >
          {passkey.isPending ? "Aguardando o aparelho…" : "Entrar com passkey"}
        </button>
        <span className="cartao-nota">Use a digital, o rosto ou o PIN deste aparelho.</span>
      </section>

      <form className="cartao" onSubmit={enviarTotp} noValidate aria-labelledby="titulo-totp">
        <h2 id="titulo-totp" className="cartao-rotulo" style={{ margin: 0 }}>
          Usar código do aplicativo
        </h2>
        <Campo rotulo="CPF" valor={cpf} aoMudar={(v) => setCpf(mascararCpf(v))} inputMode="numeric" autoComplete="username" />
        <Campo
          rotulo="Código de 6 dígitos"
          valor={codigo}
          aoMudar={(v) => setCodigo(v.replace(/\D/g, "").slice(0, 6))}
          inputMode="numeric"
          autoComplete="one-time-code"
          maxLength={6}
        />
        <div>
          <button type="submit" className="botao-secundario" disabled={totp.isPending}>
            {totp.isPending ? "Entrando…" : "Entrar com código"}
          </button>
        </div>
      </form>

      <nav className="links-acesso" aria-label="Outras formas de acesso">
        <Link to="/primeiro-acesso">Primeiro acesso</Link>
        <Link to="/novo-dispositivo">Celular novo</Link>
      </nav>
    </PaginaPublica>
  );
}
