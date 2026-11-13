import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "./cliente";
import { chamarFormulario } from "./formulario";
import type { components } from "./schema";

export type Conexoes = components["schemas"]["ConexoesDto"];
export type Conexao = components["schemas"]["ConexaoDto"];
export type ItemDaConexao = components["schemas"]["ItemDto"];
export type Slot = components["schemas"]["SlotDto"];
export type Banco = components["schemas"]["BancoDto"];
export type ContaDoItem = components["schemas"]["ContaDoItemDto"];
export type ResultadoDaSincronizacao = components["schemas"]["SincronizacaoDto"];
export type GrupoNoBanco = components["schemas"]["GrupoNoBancoDto"];
export type PreviaDoItem = components["schemas"]["PreviaDoItemDto"];
type CorpoDoBanco = { codigoBanco: string; banco: string | null };

const NAO_EXISTE = "Esta conexão já não existe. Atualize a página.";

export function useConexoes() {
  return useQuery({
    queryKey: ["conexoes"],
    retry: false,
    queryFn: () => chamarFormulario(() => api.GET("/api/v1/integracoes/conexoes"), "Não foi possível carregar as conexões."),
  });
}

/** Grupos de investimento e o valor no banco na última sincronização (D21); só informa, nada é lançado. */
export function useGruposDeInvestimento() {
  return useQuery({
    queryKey: ["grupos-de-investimento"],
    retry: false,
    queryFn: () =>
      chamarFormulario(() => api.GET("/api/v1/integracoes/grupos-de-investimento"), "Não foi possível conferir os investimentos com o banco."),
  });
}

/** Confere o item no banco antes de salvar; nada é gravado. Conexão nova pelo slot; banco novo pela conexão. */
export const previaDoItem = (alvo: { slot: number } | { conexaoId: string }, itemId: string) =>
  chamarFormulario(
    () =>
      api.POST("/api/v1/integracoes/conexoes/previa", {
        body: { slot: "slot" in alvo ? alvo.slot : null, conexaoId: "conexaoId" in alvo ? alvo.conexaoId : null, itemId: itemId.trim() },
      }),
    "Não foi possível consultar o banco. Tente de novo.",
    NAO_EXISTE,
  );

const criar = (slot: number, itemId: string, b: CorpoDoBanco) =>
  chamarFormulario(() => api.POST("/api/v1/integracoes/conexoes", { body: { slot, itemId, ...b } }), "Não foi possível conectar. Tente de novo.");
const acrescentar = (id: string, itemId: string, b: CorpoDoBanco) =>
  chamarFormulario(
    () => api.POST("/api/v1/integracoes/conexoes/{id}/items", { params: { path: { id } }, body: { itemId, ...b } }),
    "Não foi possível acrescentar o banco. Tente de novo.",
    NAO_EXISTE,
  );
const escolher = (item: string, b: CorpoDoBanco) =>
  chamarFormulario(
    () => api.PUT("/api/v1/integracoes/items/{id}/banco", { params: { path: { id: item } }, body: b }),
    "Não foi possível guardar o banco. Tente de novo.",
    "Este banco já não está na conexão. Atualize a página.",
  );
const tirar = (id: string, item: string) =>
  chamarFormulario(
    () => api.DELETE("/api/v1/integracoes/conexoes/{id}/items/{item}", { params: { path: { id, item } } }),
    "Não foi possível tirar o banco. Tente de novo.",
    "Este banco já não está na conexão. Atualize a página.",
  );
const apagar = (id: string) =>
  chamarFormulario(
    () => api.DELETE("/api/v1/integracoes/conexoes/{id}", { params: { path: { id } } }),
    "Não foi possível apagar a conexão. Tente de novo.",
    NAO_EXISTE,
  );

/**
 * Importa agora as contas e os cartões nascidos do item (Tarefa 4.5). Recarrega o que a importação muda: conexões (histórico
 * e diferenças), contas, saldos, extrato e visão geral.
 */
export async function sincronizarItem(qc: ReturnType<typeof useQueryClient>, item: string): Promise<ResultadoDaSincronizacao> {
  try {
    return await chamarFormulario(
      () => api.POST("/api/v1/integracoes/items/{id}/sincronizacao", { params: { path: { id: item } } }),
      "Não foi possível sincronizar agora. Tente de novo mais tarde.",
      "Este banco já não está na conexão. Atualize a página.",
    );
  } finally {
    await Promise.all(
      ["conexoes", "contas", "saldos", "lancamentos", "visao-geral", "grupos-de-investimento"].map((chave) => qc.invalidateQueries({ queryKey: [chave] })),
    );
  }
}

/** Toda mutação recarrega a lista: o estado de cada banco vem do servidor. */
export function useAcoesDeConexoes() {
  const qc = useQueryClient();
  const depois = async <T,>(chamada: Promise<T>): Promise<T> => {
    const r = await chamada;
    await qc.invalidateQueries({ queryKey: ["conexoes"] });
    return r;
  };
  return {
    conectar: (slot: number, itemId: string, banco: CorpoDoBanco) => depois(criar(slot, itemId.trim(), banco)),
    acrescentarBanco: (id: string, itemId: string, banco: CorpoDoBanco) => depois(acrescentar(id, itemId.trim(), banco)),
    escolherBanco: (item: string, banco: CorpoDoBanco) => depois(escolher(item, banco)),
    tirarBanco: (id: string, item: string) => depois(tirar(id, item)),
    apagarConexao: (id: string) => depois(apagar(id)),
    sincronizar: (item: string) => sincronizarItem(qc, item),
  };
}
