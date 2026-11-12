import { useQuery, useQueryClient } from "@tanstack/react-query";
import { ErroDeApi } from "./autenticacao";
import { api } from "./cliente";
import { chamarFormulario } from "./formulario";
import type { components } from "./schema";

export type Descoberta = components["schemas"]["DescobertaDto"];
export type ContaEncontrada = components["schemas"]["ContaEncontradaDto"];
export type ContaNaoImportada = components["schemas"]["ContaNaoImportadaDto"];
export type PedidoDeNascimento = components["schemas"]["NascimentoDto"];
export type Inclusao = components["schemas"]["InclusaoDto"];

const APELIDO_REPETIDO = "Já existe uma conta com esse nome.";

/** 409 de apelido repetido: a API manda a sugestão e qual conta do banco (idExterno) esbarrou. */
export class ErroDeApelido extends ErroDeApi {
  readonly sugestao: string;
  readonly idExterno: string | null;
  constructor(sugestao: string, idExterno: string | null) {
    super(409, `${APELIDO_REPETIDO} Sugestão: ${sugestao}.`);
    this.sugestao = sugestao;
    this.idExterno = idExterno;
  }
}

type Resposta = { error?: unknown; response: Response };

/** Como `chamarFormulario`, mas o 409 de apelido repetido vira `ErroDeApelido` (com a sugestão), para a tela marcar a linha. */
async function comSugestao(chamada: () => Promise<Resposta>, generica: string): Promise<void> {
  let r: Resposta | undefined;
  await chamarFormulario(async () => {
    r = await chamada();
    return { ...r, data: undefined };
  }, generica).catch((e: unknown) => {
    const corpo = r?.error as { title?: unknown; sugestao?: unknown; idExterno?: unknown } | undefined;
    if (r?.response.status === 409 && corpo?.title === APELIDO_REPETIDO && typeof corpo.sugestao === "string")
      throw new ErroDeApelido(corpo.sugestao, typeof corpo.idExterno === "string" ? corpo.idExterno : null);
    throw e;
  });
}

export const chaveDaDescoberta = (itemId: string) => ["descoberta", itemId] as const;

/** O que o banco tem e o sistema ainda não. Só lê; nunca guardado no navegador além do cache da consulta. */
export function useDescoberta(itemId: string) {
  return useQuery({
    queryKey: chaveDaDescoberta(itemId),
    retry: false,
    queryFn: () =>
      chamarFormulario(
        () => api.GET("/api/v1/integracoes/items/{id}/descoberta", { params: { path: { id: itemId } } }),
        "Não foi possível ler as contas do banco. Tente de novo.",
        "Este banco já não está na sua conexão. Volte para Conexões.",
      ),
  });
}

/** Confirmar e incluir recarregam a descoberta, as contas e as conexões: o que nasceu sai da lista e aparece em Contas. */
export function useAcoesDeNascimento(itemId: string) {
  const qc = useQueryClient();
  const recarregar = () =>
    Promise.all([
      qc.invalidateQueries({ queryKey: chaveDaDescoberta(itemId) }),
      qc.invalidateQueries({ queryKey: ["contas"] }),
      qc.invalidateQueries({ queryKey: ["conexoes"] }),
    ]);
  return {
    confirmar: async (pedido: PedidoDeNascimento) => {
      await comSugestao(
        () => api.POST("/api/v1/integracoes/items/{id}/nascimento", { params: { path: { id: itemId } }, body: pedido }),
        "Não foi possível criar as contas. Tente de novo.",
      );
      await recarregar();
    },
    incluir: async (vinculoId: string, corpo: Inclusao) => {
      await comSugestao(
        () => api.POST("/api/v1/integracoes/vinculos/{id}/inclusao", { params: { path: { id: vinculoId } }, body: corpo }),
        "Não foi possível incluir a conta. Tente de novo.",
      );
      await recarregar();
    },
  };
}
