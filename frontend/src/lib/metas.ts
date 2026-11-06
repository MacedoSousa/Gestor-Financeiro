export { mesCurto } from "./mes";

const PASSO = 50_000;
const LIMITE = 100_000_000_000;

/** Um clique do simulador: R$ 500 para cima ou para baixo, entre 0 e R$ 1 bilhão (em centavos). */
export function passoDoSimulador(atual: number, delta: 1 | -1): number {
  return Math.min(LIMITE, Math.max(0, atual + delta * PASSO));
}

const MENSAGEM_PERCENTUAL = "Use um percentual entre 0 e 5, com até 4 casas.";

/** Percentual ao mês digitado ("0,85"), de 0 a 5 com até 4 casas. */
export function lerPercentual(texto: string): { ok: true; valor: number } | { ok: false; mensagem: string } {
  const t = texto.trim();
  if (!/^\d+([.,]\d{1,4})?$/.test(t)) return { ok: false, mensagem: MENSAGEM_PERCENTUAL };
  const valor = Number(t.replace(",", "."));
  if (valor > 5) return { ok: false, mensagem: MENSAGEM_PERCENTUAL };
  return { ok: true, valor };
}

type ProjecaoDaMeta = {
  aporteMensalCentavos: number | string;
  [campo: string]: unknown;
};

export type ProjecaoExibida<P extends ProjecaoDaMeta> =
  | { tipo: "projecao"; projecao: P; atualizando: boolean }
  | { tipo: "pendente" }
  | { tipo: "erro" };

/**
 * Qual projeção o cartão mostra. No planejado, a do painel. Simulando, só a resposta do servidor que seja
 * do aporte exibido; resposta de outro aporte (placeholder dos cliques anteriores), ou ainda sem resposta, é "pendente";
 * falha da simulação é "erro". Nunca se mostra número de um aporte ao lado de outro.
 */
export function projecaoExibida<P extends ProjecaoDaMeta>(
  simulando: boolean,
  aporte: number,
  planejada: P,
  simulacao: { data?: P; isError: boolean; isFetching: boolean },
): ProjecaoExibida<P> {
  if (!simulando) return { tipo: "projecao", projecao: planejada, atualizando: false };
  if (simulacao.isError) return { tipo: "erro" };
  if (simulacao.data && Number(simulacao.data.aporteMensalCentavos) === aporte) {
    return { tipo: "projecao", projecao: simulacao.data, atualizando: simulacao.isFetching };
  }
  return { tipo: "pendente" };
}
