import { useQuery } from "@tanstack/react-query";
import { api } from "./cliente";
import { chamarFormulario } from "./formulario";
import type { components } from "./schema";

export type VisaoGeral = components["schemas"]["VisaoGeralDto"];

/** Uma seção do painel: ou `dados`, ou `erro` (as outras seções continuam). Lida pelo caminho, nunca pelo nome gerado do genérico. */
export type Secao<T> = { dados: T | null; erro: string | null };
type DadosDe<S> = S extends { dados: infer D } ? NonNullable<D> : never;

export type AindaDaParaGastar = DadosDe<VisaoGeral["aindaDaParaGastar"]>;
export type SaldoDasContas = DadosDe<VisaoGeral["saldoDasContas"]>;
export type AportePlanejado = DadosDe<VisaoGeral["aportePlanejado"]>;
export type Patrimonio = DadosDe<VisaoGeral["patrimonio"]>;
export type AvisoDeFatura = DadosDe<VisaoGeral["avisosDeFatura"]>[number];
export type PatrimonioEm12Meses = DadosDe<VisaoGeral["patrimonioEm12Meses"]>;
export type MetasDoPainel = DadosDe<VisaoGeral["metas"]>;
export type OrcamentoDoPainel = DadosDe<VisaoGeral["orcamento"]>;
export type Vencimento = DadosDe<VisaoGeral["vencimentos"]>[number];
export type UltimoLancamento = DadosDe<VisaoGeral["ultimosLancamentos"]>[number];

/** Sempre refaz ao entrar na tela: o painel junta dados de vários módulos e muda a cada lançamento. */
export function useVisaoGeral() {
  return useQuery({
    queryKey: ["visao-geral"],
    retry: false,
    refetchOnMount: "always",
    queryFn: () =>
      chamarFormulario(
        () => api.GET("/api/v1/visao-geral"),
        "Não foi possível carregar a visão geral.",
        "Não foi possível carregar a visão geral.",
      ),
  });
}
