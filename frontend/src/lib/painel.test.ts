import { describe, expect, it } from "vitest";
import { aparenciaDaFatura, aparenciaDaMeta, diaDoLancamento, rotuloDaOrigem, resumoDoGrafico, saudacao } from "./painel";

describe("saudacao", () => {
  it.each([
    [0, "Bom dia"],
    [11, "Bom dia"],
    [12, "Boa tarde"],
    [17, "Boa tarde"],
    [18, "Boa noite"],
    [23, "Boa noite"],
  ])("às %i h: %s", (hora, esperado) => {
    expect(saudacao(hora)).toBe(esperado);
  });
});

describe("resumoDoGrafico", () => {
  const meses = ["2025-12", "2026-01", "2026-02", "2026-03", "2026-04", "2026-05", "2026-06", "2026-07", "2026-08", "2026-09", "2026-10", "2026-11"];
  const ponto = (mes: string, totalCentavos: number | null) => ({ rotulo: mes.slice(5), mes, totalCentavos });

  it("usa o primeiro e o último mês com dado", () => {
    const valores: Record<string, number> = { "2026-09": 4015000, "2026-10": 9905000, "2026-11": 9855000 };
    const pontos = meses.map((m) => ponto(m, valores[m] ?? null));
    expect(resumoDoGrafico(pontos)?.replaceAll("\u00a0", " ")).toBe("de R$ 40.150,00 em set/2026 a R$ 98.550,00 em nov/2026");
  });

  it("tudo nulo não tem resumo", () => {
    expect(resumoDoGrafico(meses.map((m) => ponto(m, null)))).toBeNull();
    expect(resumoDoGrafico([])).toBeNull();
  });
});

describe("aparenciaDaFatura", () => {
  it("aberta começa com Aberta", () => {
    const a = aparenciaDaFatura("ABERTA");
    expect(a.rotulo).toBe("Aberta");
    expect(a.tom).toBe("normal");
  });
  it("fechada é normal", () => {
    expect(aparenciaDaFatura("FECHADA")).toEqual({ rotulo: "Fechada", tom: "normal" });
  });
  it("vencida é alerta", () => {
    expect(aparenciaDaFatura("VENCIDA")).toEqual({ rotulo: "Vencida", tom: "alerta" });
  });
});

describe("aparenciaDaMeta", () => {
  it("chega", () => {
    expect(aparenciaDaMeta("CHEGA", "2029-02")).toEqual({ texto: "previsão fev/2029", tom: "normal" });
  });
  it("alcançada", () => {
    expect(aparenciaDaMeta("ALCANCADA")).toEqual({ texto: "Alcançada", tom: "ok" });
  });
  it("não chega", () => {
    expect(aparenciaDaMeta("NAO_CHEGA", null)).toEqual({ texto: "Não chega em 100 anos com o aporte planejado", tom: "alerta" });
  });
});

describe("diaDoLancamento", () => {
  it.each([
    ["2026-11-05", "Hoje"],
    ["2026-11-04", "Ontem"],
    ["2026-11-03", "03/11"],
    ["2026-11-06", "06/11"],
  ])("%s com hoje 05/11 → %s", (data, esperado) => {
    expect(diaDoLancamento(data, "2026-11-05")).toBe(esperado);
  });
  it("ontem atravessa o mês e o ano", () => {
    expect(diaDoLancamento("2026-12-31", "2027-01-01")).toBe("Ontem");
  });
});

describe("rotuloDaOrigem", () => {
  it.each([
    ["MANUAL", "Manual"],
    ["OPEN_FINANCE", "Open Finance"],
    ["WHATSAPP", "WhatsApp"],
    ["OUTRA", "OUTRA"],
  ])("%s → %s", (o, e) => expect(rotuloDaOrigem(o)).toBe(e));
});
