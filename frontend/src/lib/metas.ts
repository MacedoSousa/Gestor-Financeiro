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
