import { useQuery } from "@tanstack/react-query";
import { api } from "./cliente";
import { chamar, ErroDeApi, MENSAGEM_CONFIRMAR } from "./autenticacao";
import type { components } from "./schema";

export type Credencial = components["schemas"]["CredencialDto"];
export type PedidoPendente = components["schemas"]["PedidoPendenteDto"];

export function useCredenciais() {
  return useQuery({
    queryKey: ["credenciais"],
    retry: false,
    queryFn: () => chamar(() => api.GET("/api/v1/dispositivos/credenciais"), "Não foi possível carregar as formas de entrar."),
  });
}

/** O outro cônjuge pode pedir a qualquer momento: consulta a cada 10 s enquanto a tela está aberta. */
export function usePedidos() {
  return useQuery({
    queryKey: ["pedidos"],
    retry: false,
    refetchInterval: 10_000,
    queryFn: () => chamar(() => api.GET("/api/v1/dispositivos/pedidos"), "Não foi possível carregar os pedidos."),
  });
}

export const aprovarPedido = (id: string) =>
  chamar(() => api.POST("/api/v1/dispositivos/pedidos/{id}/aprovacao", { params: { path: { id } } }), "Não foi possível aprovar o pedido.");

export const destravarConjuge = () =>
  chamar(() => api.POST("/api/v1/dispositivos/conjuge/destravar"), "Não foi possível destravar a conta do cônjuge.");

export const removerCredencial = (id: string) =>
  chamar(() => api.DELETE("/api/v1/dispositivos/credenciais/{id}", { params: { path: { id } } }), "Não foi possível remover.");

export const autorizarNovaCredencial = () =>
  chamar(() => api.POST("/api/v1/dispositivos/credenciais/autorizacao"), "Não foi possível iniciar o cadastro da nova forma de entrar.");

export async function gerarCodigoDeRecuperacao(): Promise<string> {
  const r = await chamar(() => api.POST("/api/v1/casal/codigo-recuperacao"), "Não foi possível gerar o código.");
  return r.codigoRecuperacao;
}

export const exigeConfirmacao = (e: unknown) => e instanceof ErroDeApi && e.status === 403 && e.message === MENSAGEM_CONFIRMAR;

export const pedirDispositivo = (cpf: string) =>
  chamar(() => api.POST("/api/v1/auth/dispositivo/pedido", { body: { cpf } }), "Não foi possível pedir a aprovação. Tente de novo.");

export const resgatarPedido = () =>
  chamar(() => api.POST("/api/v1/auth/dispositivo/pedido/resgate"), "O pedido expirou ou já foi usado. Peça de novo.");

export const recuperarAcesso = (cpf: string, codigo: string) =>
  chamar(() => api.POST("/api/v1/auth/recuperacao", { body: { cpf, codigo } }), "Não foi possível entrar. Tente de novo.");

/** Só o estado (PENDENTE, APROVADO, EXPIRADO), a cada 10 s, enquanto `ativo`. */
export function useEstadoDoPedido(ativo: boolean) {
  return useQuery({
    queryKey: ["pedido-estado"],
    enabled: ativo,
    retry: false,
    gcTime: 0,
    staleTime: 0,
    // Só segue consultando enquanto PENDENTE: APROVADO é resgatado e EXPIRADO não muda mais.
    refetchInterval: (consulta) => (consulta.state.data === undefined || consulta.state.data === "PENDENTE" ? 10_000 : false),
    queryFn: async () => (await chamar(() => api.GET("/api/v1/auth/dispositivo/pedido"))).estado,
  });
}
