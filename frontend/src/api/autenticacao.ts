import { useQuery } from "@tanstack/react-query";
import { startAuthentication, startRegistration } from "@simplewebauthn/browser";
import { api } from "./cliente";
import type { components } from "./schema";

export type Eu = components["schemas"]["EuDto"];
export type Totp = components["schemas"]["TotpGeradoDto"];
type OpcoesDeRegistro = components["schemas"]["OpcoesDeRegistroDePasskeyDto"];
type Assercao = components["schemas"]["RespostaDeLoginComPasskeyDto"];

export const MENSAGEM_CONFIRMAR = "Confirme sua identidade para continuar.";
const SEM_CONEXAO = "Sem conexão com o servidor. Tente de novo.";
const MUITAS_TENTATIVAS = "Muitas tentativas. Aguarde alguns minutos e tente de novo.";
const GENERICA = "Não foi possível concluir. Tente de novo.";

/** Erro de chamada à API: a mensagem já é a que a tela pode mostrar (fixa da API ou genérica). */
export class ErroDeApi extends Error {
  readonly status: number;
  constructor(status: number, mensagem: string) {
    super(mensagem);
    this.status = status;
  }
}

type Resposta<T> = { data?: T; error?: unknown; response: Response };

/** Executa a chamada, devolve os dados e traduz falhas nas mensagens fixas da API ou em genéricas. */
export async function chamar<T>(chamada: () => Promise<Resposta<T>>, generica = GENERICA): Promise<T> {
  let r: Resposta<T>;
  try {
    r = await chamada();
  } catch {
    throw new ErroDeApi(0, SEM_CONEXAO);
  }
  if (r.response.ok) return r.data as T;
  const status = r.response.status;
  if (status === 429) throw new ErroDeApi(status, MUITAS_TENTATIVAS);
  const titulo = (r.error as { title?: unknown } | undefined)?.title;
  if ([401, 403, 409, 423].includes(status) && typeof titulo === "string" && titulo) throw new ErroDeApi(status, titulo);
  throw new ErroDeApi(status, generica);
}

/** Falha do navegador ao falar com o autenticador (cancelou, sem suporte…): nunca expõe a mensagem interna. */
async function comAutenticador<T>(acao: () => Promise<T>, mensagem: string): Promise<T> {
  try {
    return await acao();
  } catch {
    throw new ErroDeApi(0, mensagem);
  }
}

export function useEu(habilitada = true) {
  return useQuery<Eu, ErroDeApi>({
    queryKey: ["eu"],
    enabled: habilitada,
    retry: false,
    queryFn: async () => {
      let r;
      try {
        r = await api.GET("/api/v1/auth/eu");
      } catch {
        throw new ErroDeApi(0, SEM_CONEXAO);
      }
      if (r.data) return r.data;
      throw new ErroDeApi(r.response.status, "Não foi possível consultar a sessão.");
    },
  });
}

export function sair() {
  return chamar(() => api.POST("/api/v1/auth/sair"));
}

export function primeiroAcesso(cpf: string, senha: string) {
  return chamar(() => api.POST("/api/v1/auth/primeiro-acesso", { body: { cpf, senha } }), "Não foi possível entrar. Tente de novo.");
}

export function entrarComTotp(cpf: string, codigo: string) {
  return chamar(() => api.POST("/api/v1/auth/totp", { body: { cpf, codigo } }), "Não foi possível entrar. Tente de novo.");
}

function paraAssercao(r: Awaited<ReturnType<typeof startAuthentication>>): Assercao {
  return {
    id: r.id,
    rawId: r.rawId,
    type: r.type,
    response: {
      clientDataJSON: r.response.clientDataJSON,
      authenticatorData: r.response.authenticatorData,
      signature: r.response.signature,
      userHandle: r.response.userHandle ?? null,
    },
  };
}

export async function entrarComPasskey() {
  const op = await chamar(() => api.POST("/api/v1/auth/passkey/opcoes"));
  const resposta = await comAutenticador(
    () =>
      startAuthentication({
        optionsJSON: {
          challenge: op.challenge,
          timeout: Number(op.timeout),
          rpId: op.rpId,
          userVerification: "required",
        },
      }),
    "Não foi possível usar a passkey. Tente de novo.",
  );
  await chamar(() => api.POST("/api/v1/auth/passkey", { body: paraAssercao(resposta) }), "Não foi possível entrar. Tente de novo.");
}

/** Confirmação recente (5 min) para ações sensíveis: passkey. */
export async function confirmarComPasskey() {
  const op = await chamar(() => api.POST("/api/v1/auth/confirmar/opcoes"));
  const resposta = await comAutenticador(
    () =>
      startAuthentication({
        optionsJSON: {
          challenge: op.challenge,
          timeout: Number(op.timeout),
          rpId: op.rpId,
          allowCredentials: op.allowCredentials.map((c) => ({ id: c.id, type: "public-key" as const })),
          userVerification: "required",
        },
      }),
    "Não foi possível usar a passkey. Tente de novo.",
  );
  await chamar(() => api.POST("/api/v1/auth/confirmar", { body: { codigoTotp: null, passkey: paraAssercao(resposta) } }));
}

export function confirmarComTotp(codigo: string) {
  return chamar(() => api.POST("/api/v1/auth/confirmar", { body: { codigoTotp: codigo, passkey: null } }));
}

export async function criarPasskey(apelido: string) {
  const op: OpcoesDeRegistro = await chamar(() => api.POST("/api/v1/auth/passkey/registro/opcoes"));
  const resposta = await comAutenticador(
    () =>
      startRegistration({
        optionsJSON: {
          rp: op.rp,
          user: op.user,
          challenge: op.challenge,
          pubKeyCredParams: op.pubKeyCredParams.map((p) => ({ type: "public-key" as const, alg: Number(p.alg) })),
          timeout: Number(op.timeout),
          excludeCredentials: op.excludeCredentials.map((c) => ({ id: c.id, type: "public-key" as const })),
          authenticatorSelection: {
            residentKey: "required",
            requireResidentKey: true,
            userVerification: "required",
          },
          attestation: "none",
        },
      }),
    "Não foi possível criar a passkey neste aparelho. Tente de novo.",
  );
  await chamar(() =>
    api.POST("/api/v1/auth/passkey/registro", {
      body: {
        apelido,
        resposta: {
          id: resposta.id,
          rawId: resposta.rawId,
          type: resposta.type,
          response: {
            clientDataJSON: resposta.response.clientDataJSON,
            attestationObject: resposta.response.attestationObject,
            transports: resposta.response.transports ?? null,
          },
        },
      },
    }),
  );
}

export function registrarTotp(apelido: string): Promise<Totp> {
  return chamar(() => api.POST("/api/v1/auth/totp/registro", { body: { apelido } }));
}

export function confirmarTotp(credencialId: string, codigo: string) {
  return chamar(() => api.POST("/api/v1/auth/totp/confirmacao", { body: { credencialId, codigo } }));
}
