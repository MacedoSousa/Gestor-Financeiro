import { useQuery } from "@tanstack/react-query";
import { api } from "./cliente";
import { chamarFormulario } from "./formulario";
import type { components } from "./schema";

export type Conta = components["schemas"]["ContaDto"];
export type NovaConta = components["schemas"]["NovaContaDto"];
export type Saldo = components["schemas"]["SaldoDaContaDto"];

export const TIPOS_DE_CONTA = [
  { valor: "CORRENTE", rotulo: "Conta corrente" },
  { valor: "POUPANCA", rotulo: "Poupança" },
  { valor: "INVESTIMENTO", rotulo: "Investimento" },
  { valor: "CARTAO", rotulo: "Cartão de crédito" },
  { valor: "DINHEIRO", rotulo: "Dinheiro" },
] as const;

export const rotuloDoTipo = (tipo: string) => TIPOS_DE_CONTA.find((t) => t.valor === tipo)?.rotulo ?? tipo;

export function useContas() {
  return useQuery({
    queryKey: ["contas"],
    retry: false,
    queryFn: () => chamarFormulario(() => api.GET("/api/v1/contas"), "Não foi possível carregar as contas."),
  });
}

export function useSaldos() {
  return useQuery({
    queryKey: ["saldos"],
    retry: false,
    queryFn: () => chamarFormulario(() => api.GET("/api/v1/saldos"), "Não foi possível carregar os saldos."),
  });
}

export const criarConta = (corpo: NovaConta) => chamarFormulario(() => api.POST("/api/v1/contas", { body: corpo }));

export const atualizarConta = (id: string, corpo: NovaConta) =>
  chamarFormulario(() => api.PUT("/api/v1/contas/{id}", { params: { path: { id } }, body: corpo }));
