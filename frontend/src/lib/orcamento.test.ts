import { describe, expect, it } from "vitest";
import type { OrcamentoDoMes } from "../api/orcamento";
import { lerTeto, segmentosDaBarra } from "./orcamento";

function orcamento(sobre: Partial<OrcamentoDoMes> = {}): OrcamentoDoMes {
  return {
    mes: "2026-11",
    receitaPrevistaCentavos: 1_850_000,
    receitaSugeridaCentavos: 0,
    fatias: {
      investir: { tetoCentavos: 680_000, usadoCentavos: 0, aportesCentavos: 0, percentual: 36.8 },
      dividas: { tetoCentavos: 390_000, usadoCentavos: 0, percentual: 21.1 },
      tetoDeGastos: { tetoCentavos: 210_000, usadoCentavos: 0, percentual: 11.4 },
    },
    folgaCentavos: 570_000,
    categorias: [],
    mesParaCopiar: null,
    ...sobre,
  };
}

describe("segmentosDaBarra", () => {
  it("dá quatro segmentos no cenário base", () => {
    const s = segmentosDaBarra(orcamento());
    expect(s.map((x) => [x.fatia, x.centavos, x.percentual])).toEqual([
      ["investir", 680_000, 36.8],
      ["gastos", 210_000, 11.4],
      ["dividas", 390_000, 21.1],
      ["folga", 570_000, 30.8],
    ]);
  });

  it("não tem segmento de folga quando a folga é negativa", () => {
    const s = segmentosDaBarra(orcamento({ receitaPrevistaCentavos: 500_000, folgaCentavos: -780_000 }));
    expect(s.map((x) => x.fatia)).toEqual(["investir", "gastos", "dividas"]);
  });

  it("não gera segmento para fatia sem teto", () => {
    const o = orcamento();
    o.fatias.dividas.tetoCentavos = null;
    expect(segmentosDaBarra(o).map((x) => x.fatia)).toEqual(["investir", "gastos", "folga"]);
  });

  it("arredonda a folga no ponto médio para cima (28,75% vira 28,8%)", () => {
    const s = segmentosDaBarra(orcamento({ receitaPrevistaCentavos: 800_000, folgaCentavos: 230_000 }));
    expect(s.find((x) => x.fatia === "folga")?.percentual).toBe(28.8);
  });

  it.each([null, 0])("é vazio sem receita prevista (%s)", (receita) => {
    expect(segmentosDaBarra(orcamento({ receitaPrevistaCentavos: receita, folgaCentavos: null }))).toEqual([]);
  });
});

describe("lerTeto", () => {
  const limite = "Use um valor entre R$ 0,00 e R$ 1.000.000.000,00.";

  it.each(["", "   "])("vazio: %j", (t) => expect(lerTeto(t)).toEqual({ tipo: "vazio" }));
  it("zero é um valor", () => expect(lerTeto("0")).toEqual({ tipo: "valor", centavos: 0 }));
  it("lê 1.600", () => expect(lerTeto("1.600")).toEqual({ tipo: "valor", centavos: 160_000 }));
  it("aceita o limite", () => expect(lerTeto("1.000.000.000,00")).toEqual({ tipo: "valor", centavos: 100_000_000_000 }));
  it.each(["1.000.000.000,01", "abc"])("recusa %j", (t) => expect(lerTeto(t)).toEqual({ tipo: "invalido", mensagem: limite }));
});
