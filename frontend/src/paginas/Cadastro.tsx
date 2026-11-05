import { useEffect, useRef, useState, type FormEvent } from "react";
import { Navigate, useNavigate } from "react-router";
import { useQueryClient } from "@tanstack/react-query";
import { useCadastrarCasal, useEstadoDoCadastro } from "../api/cadastro";
import { CodigoDeRecuperacao } from "./CodigoDeRecuperacao";
import { Campo } from "./Campo";
import { mascararCpf } from "../lib/cpf";

type Conjuge = { nomeCompleto: string; cpf: string; senha: string; confirmacao: string };
const vazio: Conjuge = { nomeCompleto: "", cpf: "", senha: "", confirmacao: "" };

/** Mesma regra e mensagem da API (NFC, 12 a 128): erro de digitação não gasta o limite de tentativas. */
const mensagemSenha = "A senha precisa ter de 12 a 128 caracteres.";
function senhaValida(senha: string) {
  const tamanho = senha.normalize("NFC").length;
  return tamanho >= 12 && tamanho <= 128;
}

export function Cadastro() {
  const estado = useEstadoDoCadastro();
  const cadastrar = useCadastrarCasal();
  const consultas = useQueryClient();
  const navegar = useNavigate();

  const [nomeFamilia, setNomeFamilia] = useState("");
  const [codigoInstalacao, setCodigoInstalacao] = useState("");
  const [c1, setC1] = useState<Conjuge>(vazio);
  const [c2, setC2] = useState<Conjuge>(vazio);
  const [confirmacaoErrada, setConfirmacaoErrada] = useState<{ c1?: string; c2?: string }>({});
  const [senhaInvalida, setSenhaInvalida] = useState<{ c1?: string; c2?: string }>({});

  const alertaRef = useRef<HTMLParagraphElement>(null);
  const erroAtual = cadastrar.error;
  useEffect(() => {
    if (!erroAtual) return;
    (alertaRef.current ?? document.querySelector<HTMLElement>('[aria-invalid="true"]'))?.focus();
  }, [erroAtual]);

  if (cadastrar.data) {
    return (
      <CodigoDeRecuperacao
        codigo={cadastrar.data.codigoRecuperacao}
        aoConcluir={() => {
          cadastrar.reset();
          // Sem isto, o "/" ainda veria disponivel: true em cache e voltaria para cá por um instante.
          consultas.setQueryData(["cadastro"], { disponivel: false });
          void consultas.invalidateQueries({ queryKey: ["cadastro"] });
          void navegar("/");
        }}
      />
    );
  }

  if (estado.isPending) {
    return (
      <div className="pagina-centro">
        <p role="status" className="subtitulo">
          Carregando…
        </p>
      </div>
    );
  }
  if (estado.isError) {
    return (
      <div className="pagina-centro">
        <p role="alert" className="alerta-form">
          Não foi possível consultar o cadastro.
        </p>
      </div>
    );
  }
  // Durante o envio, um refetch pode já ver o casal criado; sair agora perderia o código de recuperação.
  if (!estado.data.disponivel && !cadastrar.isPending) return <Navigate to="/" replace />;

  const erroDe = cadastrar.error?.status === 400 ? cadastrar.error.errors : {};
  const erro = (chave: string) => erroDe[chave]?.[0];
  const camposDesenhados = new Set([
    "nomeFamilia",
    "codigoInstalacao",
    ...[1, 2].flatMap((n) => ["nomeCompleto", "cpf", "senha"].map((c) => `conjuge${n}.${c}`)),
  ]);
  const algumCampoMapeado = Object.keys(erroDe).some((k) => camposDesenhados.has(k));
  const alerta =
    cadastrar.error && cadastrar.error.status !== 400
      ? cadastrar.error.message
      : cadastrar.error && !algumCampoMapeado
        ? "Revise os dados do formulário."
        : undefined;

  function enviar(e: FormEvent) {
    e.preventDefault();
    const divergencia = {
      c1: c1.senha !== c1.confirmacao ? "A confirmação não confere com a senha." : undefined,
      c2: c2.senha !== c2.confirmacao ? "A confirmação não confere com a senha." : undefined,
    };
    const tamanho = {
      c1: senhaValida(c1.senha) ? undefined : mensagemSenha,
      c2: senhaValida(c2.senha) ? undefined : mensagemSenha,
    };
    setConfirmacaoErrada(divergencia);
    setSenhaInvalida(tamanho);
    if (divergencia.c1 || divergencia.c2 || tamanho.c1 || tamanho.c2) {
      // O foco vai para o primeiro campo com erro depois que o React aplicar aria-invalid.
      requestAnimationFrame(() => document.querySelector<HTMLElement>('[aria-invalid="true"]')?.focus());
      return;
    }
    cadastrar.mutate(
      {
        codigoInstalacao,
        nomeFamilia,
        conjuge1: { nomeCompleto: c1.nomeCompleto, cpf: c1.cpf, senha: c1.senha },
        conjuge2: { nomeCompleto: c2.nomeCompleto, cpf: c2.cpf, senha: c2.senha },
      },
      {
        // Senhas não ficam no estado do componente enquanto o código é mostrado.
        onSuccess: () => {
          setC1((c) => ({ ...c, senha: "", confirmacao: "" }));
          setC2((c) => ({ ...c, senha: "", confirmacao: "" }));
        },
      },
    );
  }

  const cartaoConjuge = (
    numero: 1 | 2,
    valor: Conjuge,
    mudar: (c: Conjuge) => void,
    divergencia?: string,
    senhaCurta?: string,
  ) => {
    const chave = `conjuge${numero}`;
    return (
      <section className="cartao" aria-label={`Cônjuge ${numero}`}>
        <h2 className="cartao-rotulo" style={{ margin: 0 }}>
          Cônjuge {numero}
        </h2>
        <Campo
          rotulo="Nome completo"
          valor={valor.nomeCompleto}
          autoComplete="off"
          aoMudar={(v) => mudar({ ...valor, nomeCompleto: v })}
          erro={erro(`${chave}.nomeCompleto`)}
        />
        <Campo
          rotulo="CPF"
          valor={valor.cpf}
          inputMode="numeric"
          autoComplete="off"
          aoMudar={(v) => mudar({ ...valor, cpf: mascararCpf(v) })}
          erro={erro(`${chave}.cpf`)}
        />
        <Campo
          rotulo="Senha"
          tipo="password"
          valor={valor.senha}
          autoComplete="new-password"
          nota="Mínimo de 12 caracteres. Ela protege a chave dos dados — não é recuperável."
          aoMudar={(v) => mudar({ ...valor, senha: v })}
          erro={senhaCurta ?? erro(`${chave}.senha`)}
        />
        <Campo
          rotulo="Confirmar senha"
          tipo="password"
          valor={valor.confirmacao}
          autoComplete="new-password"
          aoMudar={(v) => mudar({ ...valor, confirmacao: v })}
          erro={divergencia}
        />
      </section>
    );
  };

  return (
    <form className="pagina-centro" onSubmit={enviar} noValidate>
      <div className="marca">
        <div className="marca-icone" aria-hidden="true">
          GF
        </div>
        <div style={{ fontWeight: 600, fontSize: 15 }}>Gestor Financeiro</div>
      </div>
      <header style={{ display: "flex", flexDirection: "column", gap: 6 }}>
        <h1 className="titulo">Cadastro do casal</h1>
        <span className="subtitulo">Os dois juntos, uma vez só. Depois disso esta tela some.</span>
      </header>

      {alerta && (
        <p role="alert" className="alerta-form" tabIndex={-1} ref={alertaRef}>
          {alerta}
        </p>
      )}

      <section className="cartao" aria-label="Família">
        <h2 className="cartao-rotulo" style={{ margin: 0 }}>
          Família
        </h2>
        <Campo rotulo="Nome da família" valor={nomeFamilia} aoMudar={setNomeFamilia} autoComplete="off" erro={erro("nomeFamilia")} />
        <Campo
          rotulo="Código de instalação"
          valor={codigoInstalacao}
          aoMudar={setCodigoInstalacao}
          autoComplete="off"
          nota="Impresso no servidor por --codigo-instalacao."
          erro={erro("codigoInstalacao")}
        />
      </section>

      <div className="grade-form">
        {cartaoConjuge(1, c1, setC1, confirmacaoErrada.c1, senhaInvalida.c1)}
        {cartaoConjuge(2, c2, setC2, confirmacaoErrada.c2, senhaInvalida.c2)}
      </div>

      <div>
        <button type="submit" className="botao-primario" disabled={cadastrar.isPending}>
          {cadastrar.isPending ? "Protegendo as chaves…" : "Criar cadastro"}
        </button>
      </div>
    </form>
  );
}
