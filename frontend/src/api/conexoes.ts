import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "./cliente";
import { chamarFormulario } from "./formulario";
import type { components } from "./schema";

export type Conexoes = components["schemas"]["ConexoesDto"];
export type Conexao = components["schemas"]["ConexaoDto"];
export type ItemDaConexao = components["schemas"]["ItemDto"];
export type Slot = components["schemas"]["SlotDto"];
export type Banco = components["schemas"]["BancoDto"];
type CorpoDoBanco = { codigoBanco: string; banco: string | null };

const NAO_EXISTE = "Esta conexão já não existe. Atualize a página.";

export function useConexoes() {
  return useQuery({
    queryKey: ["conexoes"],
    retry: false,
    queryFn: () => chamarFormulario(() => api.GET("/api/v1/integracoes/conexoes"), "Não foi possível carregar as conexões."),
  });
}

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
  };
}
