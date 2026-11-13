import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { api } from "./cliente";
import { chamarFormulario } from "./formulario";
import type { components } from "./schema";

export type Lancamento = components["schemas"]["LancamentoDto"];
export type Categoria = components["schemas"]["CategoriaDto"];
export type NovoLancamento = components["schemas"]["NovoLancamentoDto"];
export type NovaTransferencia = components["schemas"]["NovaTransferenciaDto"];
export type EdicaoDeLancamento = components["schemas"]["EditarLancamentoDto"];
export type Escopo = "este" | "proximos";

export type FiltroDoExtrato = {
  mes: string;
  titular?: "conjunta" | "eu" | "conjuge";
  origem?: "MANUAL" | "OPEN_FINANCE";
  q?: string;
};

export function useCategorias() {
  return useQuery({
    queryKey: ["categorias"],
    retry: false,
    queryFn: () => chamarFormulario(() => api.GET("/api/v1/categorias"), "Não foi possível carregar as categorias."),
  });
}

/** O texto da busca vai só para a própria API (que decifra no servidor); nunca fica guardado no navegador. */
export function useExtrato(filtro: FiltroDoExtrato) {
  return useQuery({
    queryKey: ["lancamentos", filtro],
    retry: false,
    gcTime: 60_000,
    placeholderData: keepPreviousData,
    queryFn: () =>
      chamarFormulario(
        () => api.GET("/api/v1/lancamentos", { params: { query: { ...filtro, q: filtro.q || undefined } } }),
        "Não foi possível carregar os lançamentos.",
      ),
  });
}

export const criarLancamento = (corpo: NovoLancamento) => chamarFormulario(() => api.POST("/api/v1/lancamentos", { body: corpo }));

export const criarTransferencia = (corpo: NovaTransferencia) =>
  chamarFormulario(() => api.POST("/api/v1/transferencias", { body: corpo }), undefined, "Conta não encontrada. Atualize a página.");

export const editarLancamento = (id: string, escopo: Escopo, corpo: EdicaoDeLancamento) =>
  chamarFormulario(() => api.PUT("/api/v1/lancamentos/{id}", { params: { path: { id }, query: { escopo } }, body: corpo }));

export const apagarLancamento = (id: string, escopo: Escopo) =>
  chamarFormulario(
    () => api.DELETE("/api/v1/lancamentos/{id}", { params: { path: { id }, query: { escopo } } }),
    "Não foi possível apagar. Tente de novo.",
  );

/** Hoje no fuso do navegador, AAAA-MM-DD. */
export function hojeIso(): string {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
}
