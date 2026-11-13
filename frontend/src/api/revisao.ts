import { useQuery } from "@tanstack/react-query";
import { api } from "./cliente";
import { chamarFormulario } from "./formulario";
import type { components } from "./schema";
import type { Aplicar } from "../lib/revisao";

export type GrupoARevisar = components["schemas"]["GrupoARevisarDto"];
export type CategoriaExterna = components["schemas"]["CategoriaExternaDto"];

export function useARevisar() {
  return useQuery({
    queryKey: ["a-revisar"],
    retry: false,
    queryFn: () => chamarFormulario(() => api.GET("/api/v1/lancamentos/a-revisar"), "Não foi possível carregar o que veio do banco."),
  });
}

export function useMapaDoBanco() {
  return useQuery({
    queryKey: ["categorias-externas"],
    retry: false,
    queryFn: () =>
      chamarFormulario(() => api.GET("/api/v1/lancamentos/categorias-externas"), "Não foi possível carregar as categorias do banco."),
  });
}

/** O lote inteiro ou nada. Com "sempre que o banco disser", vai junto o código que o botão mostrou. */
export const revisarLote = (ids: readonly string[], categoriaId: string, aplicar: Aplicar, categoriaDoBanco: string | null) =>
  chamarFormulario(
    () =>
      api.POST("/api/v1/lancamentos/revisao", {
        body: { ids: [...ids], categoriaId, aplicar, categoriaDoBanco: aplicar === "SEMPRE_DO_BANCO" ? categoriaDoBanco : null },
      }),
    "Não foi possível revisar. Tente de novo.",
    "Algum lançamento deste grupo já não existe. Atualize a página.",
  );

export const apontarCategoriaDoBanco = (id: string, categoriaId: string) =>
  chamarFormulario(
    () => api.PUT("/api/v1/lancamentos/categorias-externas/{id}", { params: { path: { id } }, body: { categoriaId } }),
    "Não foi possível salvar. Tente de novo.",
  );

export const criarCategoria = (nome: string, tipo: "DESPESA" | "RECEITA") =>
  chamarFormulario(() => api.POST("/api/v1/categorias", { body: { nome, tipo } }), "Não foi possível criar a categoria. Tente de novo.");
