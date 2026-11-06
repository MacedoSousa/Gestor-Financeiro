import { describe, expect, it } from "vitest";
import { centavosDeTexto, formatarReais, textoDeCentavos } from "./dinheiro";

// O Intl separa "R$" do número com espaço sem quebra.
const r$ = (texto: string) => texto.replace(" ", " ");

describe("centavosDeTexto", () => {
  it.each([
    ["142,50", 14250],
    ["1.234,56", 123456],
    ["R$ 23", 2300],
    ["23.90", 2390],
    ["R$ 1.234", 123400],
    ["0,5", 50],
    ["12.345.678,90", 1234567890],
  ])("lê %j como %i centavos", (texto, esperado) => {
    expect(centavosDeTexto(texto)).toBe(esperado);
  });

  it.each(["", "   ", "R$"])("recusa vazio: %j", (texto) => {
    expect(centavosDeTexto(texto)).toBeNull();
  });

  it.each(["-10", "R$ -1,00", "abc", "1,234", "1.23.4", "1,2,3", "12,3a", "9".repeat(20)])("recusa inválido ou negativo: %j", (texto) => {
    expect(centavosDeTexto(texto)).toBeNull();
  });
});

describe("formatarReais", () => {
  it.each([
    [14250, "R$ 142,50"],
    [123456, "R$ 1.234,56"],
    [2300, "R$ 23,00"],
    [0, "R$ 0,00"],
    [-1050, "-R$ 10,50"],
  ])("formata %i centavos como %j", (centavos, esperado) => {
    expect(formatarReais(centavos)).toBe(r$(esperado));
  });

  it("volta ao mesmo valor pelo centavosDeTexto", () => {
    for (const centavos of [14250, 123456, 2300, 1]) expect(centavosDeTexto(formatarReais(centavos))).toBe(centavos);
  });
});

describe("textoDeCentavos", () => {
  it.each([
    [14250, "142,50"],
    [123456, "1234,56"],
    [5, "0,05"],
    [-1050, "10,50"],
  ])("%i centavos → %j", (centavos, esperado) => {
    expect(textoDeCentavos(centavos)).toBe(esperado);
  });
});
