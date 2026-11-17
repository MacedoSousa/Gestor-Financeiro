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

/**
 * Resolve um item. Repetir a mesma opção num item já resolvido não faz nada (a API responde 204). Na diferença do grupo
 * (D25), `valorCentavos` é o valor que a tela mostrou: se a diferença mudou, a API não lança nada (409).
 */
export const resolverItem = (
  id: string,
  opcao: string,
  contaId: string | null,
  categoriaId: string | null,
  movimentoId: string | null = null,
  valorCentavos: number | string | null = null,
) =>
  chamarFormulario(
    () =>
      api.POST("/api/v1/a-conferir/{id}/resolucao", {
        params: { path: { id } },
        body: { opcao, contaId, categoriaId, movimentoId, valorCentavos },
      }),
    "Não foi possível resolver. Tente de novo.",
    "Este item já não existe. Atualize a página.",
  );
