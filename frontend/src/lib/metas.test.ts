import { describe, expect, it } from "vitest";
import { lerPercentual, mesCurto, passoDoSimulador, projecaoExibida } from "./metas";

describe("mesCurto", () => {
  it("escreve o mês abreviado e o ano", () => {
    expect(mesCurto("2029-03")).toBe("mar/2029");
    expect(mesCurto("2026-12")).toBe("dez/2026");
  });
});

describe("passoDoSimulador", () => {
  const LIMITE = 100_000_000_000;
  it("não passa de zero", () => expect(passoDoSimulador(0, -1)).toBe(0));
  it("não passa do limite", () => expect(passoDoSimulador(LIMITE, 1)).toBe(LIMITE));
  it("anda R$ 500", () => {
    expect(passoDoSimulador(500_000, 1)).toBe(550_000);
    expect(passoDoSimulador(500_000, -1)).toBe(450_000);
  });
});

describe("lerPercentual", () => {
  it("lê com vírgula", () => expect(lerPercentual("0,85")).toEqual({ ok: true, valor: 0.85 }));
  it("aceita 4 casas", () => expect(lerPercentual("0,8512")).toEqual({ ok: true, valor: 0.8512 }));
  it("recusa 5 casas", () => expect(lerPercentual("0,85123").ok).toBe(false));
  it("aceita o teto 5", () => expect(lerPercentual("5")).toEqual({ ok: true, valor: 5 }));
  it("recusa acima de 5", () => expect(lerPercentual("5,0001").ok).toBe(false));
  it("recusa texto", () => {
    const r = lerPercentual("abc");
    expect(r).toEqual({ ok: false, mensagem: "Use um percentual entre 0 e 5, com até 4 casas." });
  });
});

describe("projecaoExibida", () => {
  const planejada = { aporteMensalCentavos: 200_000, mesesAteMeta: 40 };
  const simulada = { aporteMensalCentavos: 250_000, mesesAteMeta: 36 };
  it("no planejado mostra a do painel", () => {
    expect(projecaoExibida(false, 200_000, planejada, { isError: false, isFetching: false })).toEqual({ tipo: "projecao", projecao: planejada, atualizando: false, deOutroAporte: false });
  });
  it("primeiro passo, sem resposta, mostra a planejada rotulada como de outro aporte", () => {
    expect(projecaoExibida(true, 250_000, planejada, { isError: false, isFetching: true })).toEqual({ tipo: "projecao", projecao: planejada, atualizando: true, deOutroAporte: true });
  });
  it("resposta de outro aporte (cliques rápidos) mantém o último resultado rotulado", () => {
    expect(projecaoExibida(true, 300_000, planejada, { data: simulada, isError: false, isFetching: true })).toEqual({ tipo: "projecao", projecao: simulada, atualizando: true, deOutroAporte: true });
  });
  it("resposta do aporte exibido aparece, atualizando conforme a busca", () => {
    expect(projecaoExibida(true, 250_000, planejada, { data: simulada, isError: false, isFetching: false })).toEqual({ tipo: "projecao", projecao: simulada, atualizando: false, deOutroAporte: false });
    expect(projecaoExibida(true, 250_000, planejada, { data: simulada, isError: false, isFetching: true })).toMatchObject({ atualizando: true });
  });
  it("erro da simulação é erro, mesmo com dado antigo", () => {
    expect(projecaoExibida(true, 300_000, planejada, { data: simulada, isError: true, isFetching: false })).toEqual({ tipo: "erro" });
  });
});
