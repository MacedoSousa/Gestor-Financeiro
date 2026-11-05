import { useEffect, useState } from "react";
import { Link, useNavigate } from "react-router";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import QRCode from "qrcode";
import { confirmarTotp, criarPasskey, registrarTotp, type ErroDeApi, type Totp } from "../api/autenticacao";
import { Alerta, Campo, PaginaPublica } from "./Campo";

function segredoDa(uri: string) {
  try {
    return new URL(uri).searchParams.get("secret") ?? "";
  } catch {
    return "";
  }
}

/** Segredo em grupos de 4 para digitar à mão. */
const agrupar = (s: string) => s.replace(/(.{4})/g, "$1 ").trim();

export function RegistrarAcesso() {
  const navegar = useNavigate();
  const consultas = useQueryClient();
  const [apelido, setApelido] = useState("");
  const [apelidoVazio, setApelidoVazio] = useState(false);
  const [codigo, setCodigo] = useState("");
  const [qr, setQr] = useState("");

  const concluir = () => {
    consultas.removeQueries({ queryKey: ["eu"] });
    void navegar("/", { replace: true });
  };

  const passkey = useMutation<void, ErroDeApi, string>({ mutationFn: criarPasskey, onSuccess: concluir });
  const gerarTotp = useMutation<Totp, ErroDeApi, string>({ mutationFn: registrarTotp });
  const confirmar = useMutation<void, ErroDeApi, { id: string; codigo: string }>({
    mutationFn: (v) => confirmarTotp(v.id, v.codigo),
    onSuccess: () => {
      // O segredo só vive no estado desta tela.
      gerarTotp.reset();
      setQr("");
      concluir();
    },
  });

  // O segredo TOTP não fica no cache de mutações ao sair da tela.
  const { reset: limparTotp } = gerarTotp;
  useEffect(() => () => limparTotp(), [limparTotp]);

  const uri = gerarTotp.data?.otpauthUri;
  useEffect(() => {
    if (!uri) return;
    let ativo = true;
    void QRCode.toDataURL(uri, { margin: 2, width: 200 }).then((url) => ativo && setQr(url));
    return () => {
      ativo = false;
    };
  }, [uri]);

  function comApelido(acao: (a: string) => void) {
    if (!apelido.trim()) {
      setApelidoVazio(true);
      requestAnimationFrame(() => document.querySelector<HTMLElement>('[aria-invalid="true"]')?.focus());
      return;
    }
    setApelidoVazio(false);
    acao(apelido.trim());
  }

  const sessaoPerdida = [passkey.error, gerarTotp.error].some((e) => e?.status === 401);
  const alerta = passkey.error?.message ?? gerarTotp.error?.message ?? confirmar.error?.message;

  return (
    <PaginaPublica titulo="Cadastrar este aparelho" subtitulo="Escolha como você vai entrar daqui para frente.">
      <Alerta mensagem={alerta} tentativa={passkey.submittedAt + gerarTotp.submittedAt + confirmar.submittedAt} />
      {sessaoPerdida && (
        <nav className="links-acesso" aria-label="Recomeçar">
          <Link to="/entrar">Voltar para entrar</Link>
        </nav>
      )}

      <section className="cartao" aria-label="Passkey">
        <Campo
          rotulo="Nome deste aparelho"
          valor={apelido}
          aoMudar={setApelido}
          autoComplete="off"
          nota="Por exemplo: iPhone da Ana"
          erro={apelidoVazio ? "Informe um nome para este aparelho." : undefined}
        />
        <div>
          <button
            type="button"
            className="botao-primario"
            disabled={passkey.isPending}
            onClick={() => comApelido((a) => passkey.mutate(a))}
          >
            {passkey.isPending ? "Aguardando o aparelho…" : "Criar passkey"}
          </button>
        </div>
      </section>

      <section className="cartao" aria-labelledby="titulo-totp">
        <h2 id="titulo-totp" className="cartao-rotulo" style={{ margin: 0 }}>
          Usar aplicativo autenticador
        </h2>
        {!gerarTotp.data ? (
          <div>
            <button
              type="button"
              className="botao-secundario"
              disabled={gerarTotp.isPending}
              onClick={() => comApelido((a) => gerarTotp.mutate(a))}
            >
              {gerarTotp.isPending ? "Gerando…" : "Usar aplicativo autenticador"}
            </button>
          </div>
        ) : (
          <>
            <p className="cartao-nota">Leia o QR no aplicativo ou digite o segredo à mão, depois informe o código de 6 dígitos.</p>
            {qr && <img className="qr" src={qr} width={200} height={200} alt="QR code para o aplicativo autenticador" />}
            <p className="cartao-nota">
              Segredo: <span className="num segredo-totp">{agrupar(segredoDa(gerarTotp.data.otpauthUri))}</span>
            </p>
            <form
              className="campo"
              noValidate
              onSubmit={(e) => {
                e.preventDefault();
                confirmar.mutate({ id: gerarTotp.data.credencialId, codigo: codigo.replace(/\s/g, "") });
              }}
            >
              <Campo
                rotulo="Código de 6 dígitos"
                valor={codigo}
                aoMudar={(v) => setCodigo(v.replace(/\D/g, "").slice(0, 6))}
                inputMode="numeric"
                autoComplete="one-time-code"
                maxLength={6}
              />
              <div>
                <button type="submit" className="botao-secundario" disabled={confirmar.isPending}>
                  {confirmar.isPending ? "Conferindo…" : "Confirmar código"}
                </button>
              </div>
            </form>
          </>
        )}
      </section>
    </PaginaPublica>
  );
}
