import { useQuery } from "@tanstack/react-query";
import { api } from "./cliente";
import { chamarFormulario } from "./formulario";
import type { components } from "./schema";

export type ItemAConferir = components["schemas"]["ItemAConferirDto"];

export function useAConferir() {
  return useQuery({
    queryKey: ["a-conferir"],
    retry: false,
    queryFn: () => chamarFormulario(() => api.GET("/api/v1/a-conferir"), "Não foi possível carregar o que está a conferir."),
  });
}

/** Resolve um item. Repetir num item já resolvido não faz nada (a API responde 204). */
export const resolverItem = (id: string, opcao: string, contaId: string | null, categoriaId: string | null, movimentoId: string | null = null) =>
  chamarFormulario(
    () => api.POST("/api/v1/a-conferir/{id}/resolucao", { params: { path: { id } }, body: { opcao, contaId, categoriaId, movimentoId } }),
    "Não foi possível resolver. Tente de novo.",
    "Este item já não existe. Atualize a página.",
  );
