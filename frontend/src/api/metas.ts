import { keepPreviousData, useQuery, useQueryClient } from "@tanstack/react-query";
import { ErroDeApi } from "./autenticacao";
import { api } from "./cliente";
import { chamarFormulario } from "./formulario";
import type { components } from "./schema";

export type PainelDeMetas = components["schemas"]["PainelDeMetasDto"];
export type Meta = components["schemas"]["MetaDto"];
export type NovaMeta = components["schemas"]["NovaMetaDto"];
export type Projecao = components["schemas"]["ProjecaoDto"];

export function usePainelDeMetas() {
  return useQuery({
    queryKey: ["metas"],
    retry: false,
    queryFn: () => chamarFormulario(() => api.GET("/api/v1/metas"), "Não foi possível carregar as metas."),
  });
}

/** Só o aporte entra na chave: cliques rápidos viram chaves diferentes e vale a mais recente. */
export function useSimulacao(metaId: string, aporte: number, ativa: boolean) {
  return useQuery({
    queryKey: ["simulacao", metaId, aporte],
    enabled: ativa,
    retry: false,
    placeholderData: keepPreviousData,
    queryFn: () =>
      chamarFormulario(
        () => api.GET("/api/v1/metas/{id}/simulacao", { params: { path: { id: metaId }, query: { aporte } } }),
        "Não foi possível simular.",
      ),
  });
}

const criar = (corpo: NovaMeta) => chamarFormulario(() => api.POST("/api/v1/metas", { body: corpo }));
const editar = (id: string, corpo: NovaMeta) =>
  chamarFormulario(() => api.PUT("/api/v1/metas/{id}", { params: { path: { id } }, body: corpo }), undefined, "Esta meta já não existe. Atualize a página.");
const apagar = (id: string) =>
  chamarFormulario(() => api.DELETE("/api/v1/metas/{id}", { params: { path: { id } } }), "Não foi possível apagar a meta. Tente de novo.", "Esta meta já não existe. Atualize a página.");

/** Toda mutação invalida o painel; o servidor recalcula guardado e projeções. */
export function useAcoesDeMetas() {
  const qc = useQueryClient();
  const depois = async <T,>(chamada: Promise<T>): Promise<T> => {
    const r = await chamada;
    await qc.invalidateQueries({ queryKey: ["metas"] });
    await qc.invalidateQueries({ queryKey: ["simulacao"] });
    return r;
  };
  return {
    criarMeta: (corpo: NovaMeta) => depois(criar(corpo)),
    editarMeta: (id: string, corpo: NovaMeta) => depois(editar(id, corpo)),
    apagarMeta: (id: string) => depois(apagar(id)),
  };
}

export type ContaDeInvestimento = PainelDeMetas["contas"][number];
export type Conciliacao = components["schemas"]["ConciliacaoDto"];

/** 409 (saldo mudou) também recarrega o painel: a tela mostra o saldo novo para conferir de novo. */
export function useConciliacao() {
  const qc = useQueryClient();
  const recarregar = () =>
    Promise.all(["metas", "saldos", "lancamentos"].map((k) => qc.invalidateQueries({ queryKey: [k] })));
  return async (contaId: string, saldoInformadoCentavos: number, saldoExibidoCentavos: number): Promise<Conciliacao> => {
    try {
      const r = await chamarFormulario(
        () =>
          api.POST("/api/v1/contas/{id}/conciliacao", {
            params: { path: { id: contaId } },
            body: { saldoInformadoCentavos, saldoExibidoCentavos },
          }),
        "Não foi possível atualizar o saldo. Tente de novo.",
        "Esta conta já não existe. Atualize a página.",
      );
      await recarregar();
      return r;
    } catch (e) {
      if (e instanceof ErroDeApi && e.status === 409) await recarregar();
      throw e;
    }
  };
}
