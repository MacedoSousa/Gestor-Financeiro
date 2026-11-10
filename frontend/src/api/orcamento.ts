import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "./cliente";
import { chamarFormulario } from "./formulario";
import type { components } from "./schema";

export type OrcamentoDoMes = components["schemas"]["OrcamentoDoMesDto"];
export type TetoDaCategoria = components["schemas"]["TetoDaCategoriaDto"];

export function useOrcamento(mes: string) {
  return useQuery({
    queryKey: ["orcamento", mes],
    retry: false,
    queryFn: () => chamarFormulario(() => api.GET("/api/v1/orcamento", { params: { query: { mes } } }), "Não foi possível carregar o orçamento."),
  });
}

export const definirTeto = (mes: string, categoriaId: string, centavos: number) =>
  chamarFormulario(
    () => api.PUT("/api/v1/orcamento/tetos/{mes}/{categoriaId}", { params: { path: { mes, categoriaId } }, body: { valorCentavos: centavos } }),
    undefined,
    "Categoria não encontrada. Atualize a página.",
  );

export const removerTeto = (mes: string, categoriaId: string) =>
  chamarFormulario(
    () => api.DELETE("/api/v1/orcamento/tetos/{mes}/{categoriaId}", { params: { path: { mes, categoriaId } } }),
    "Não foi possível remover o teto. Tente de novo.",
    "Este teto já não existe. Atualize a página.",
  );

export const definirReceita = (mes: string, centavos: number) =>
  chamarFormulario(() => api.PUT("/api/v1/orcamento/receita/{mes}", { params: { path: { mes } }, body: { valorCentavos: centavos } }));

export const removerReceita = (mes: string) =>
  chamarFormulario(
    () => api.DELETE("/api/v1/orcamento/receita/{mes}", { params: { path: { mes } } }),
    "Não foi possível remover a receita. Tente de novo.",
    "Este mês já não tem receita prevista. Atualize a página.",
  );

export const copiarTetos = (mes: string) =>
  chamarFormulario(() => api.POST("/api/v1/orcamento/{mes}/copia", { params: { path: { mes } } }), "Não foi possível copiar os tetos. Tente de novo.");

/** Toda mutação do orçamento invalida as consultas dele; o servidor é quem recalcula fatias e folga. */
export function useAcoesDoOrcamento() {
  const qc = useQueryClient();
  const depois = async <T,>(chamada: Promise<T>): Promise<T> => {
    const r = await chamada;
    await qc.invalidateQueries({ queryKey: ["orcamento"] });
    await qc.invalidateQueries({ queryKey: ["visao-geral"] });
    return r;
  };
  return {
    definirTeto: (mes: string, categoriaId: string, centavos: number) => depois(definirTeto(mes, categoriaId, centavos)),
    removerTeto: (mes: string, categoriaId: string) => depois(removerTeto(mes, categoriaId)),
    definirReceita: (mes: string, centavos: number) => depois(definirReceita(mes, centavos)),
    removerReceita: (mes: string) => depois(removerReceita(mes)),
    copiarTetos: (mes: string) => depois(copiarTetos(mes)),
  };
}
