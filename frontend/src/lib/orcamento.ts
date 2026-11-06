import type { OrcamentoDoMes } from "../api/orcamento";
import { n } from "../api/formulario";
import { centavosDeTexto } from "./dinheiro";

export type Segmento = { fatia: "investir" | "gastos" | "dividas" | "folga"; centavos: number; percentual: number };

const LIMITE_CENTAVOS = 100_000_000_000;
const MENSAGEM_LIMITE = "Use um valor entre R$ 0,00 e R$ 1.000.000.000,00.";

const umaCasa = (x: number) => Math.round(x * 10) / 10;

/** Segmentos da barra, na ordem Investir, Teto de gastos, Dívidas, Folga. Vazio sem receita prevista. */
export function segmentosDaBarra(o: OrcamentoDoMes): Segmento[] {
  const receita = o.receitaPrevistaCentavos == null ? 0 : n(o.receitaPrevistaCentavos);
  if (receita <= 0) return [];
  const { investir, tetoDeGastos, dividas } = o.fatias;
  const fatias: Array<[Segmento["fatia"], number | string | null, number | string | null]> = [
    ["investir", investir.tetoCentavos, investir.percentual],
    ["gastos", tetoDeGastos.tetoCentavos, tetoDeGastos.percentual],
    ["dividas", dividas.tetoCentavos, dividas.percentual],
  ];
  const segmentos: Segmento[] = [];
  for (const [fatia, teto, percentual] of fatias) {
    const centavos = teto == null ? 0 : n(teto);
    if (centavos > 0) segmentos.push({ fatia, centavos, percentual: percentual == null ? umaCasa((centavos / receita) * 100) : n(percentual) });
  }
  const folga = o.folgaCentavos == null ? 0 : n(o.folgaCentavos);
  if (folga > 0) segmentos.push({ fatia: "folga", centavos: folga, percentual: umaCasa((folga / receita) * 100) });
  return segmentos;
}

export type TetoLido = { tipo: "vazio" } | { tipo: "valor"; centavos: number } | { tipo: "invalido"; mensagem: string };

/** Texto do campo de teto: vazio (volta ao valor anterior), um valor de 0 ao limite, ou inválido. */
export function lerTeto(texto: string): TetoLido {
  if (texto.trim() === "") return { tipo: "vazio" };
  const centavos = centavosDeTexto(texto);
  if (centavos === null || centavos > LIMITE_CENTAVOS) return { tipo: "invalido", mensagem: MENSAGEM_LIMITE };
  return { tipo: "valor", centavos };
}
