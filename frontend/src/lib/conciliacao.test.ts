import { describe, expect, it } from "vitest";
import { formatarReais } from "./dinheiro";
import { previaDaConciliacao } from "./conciliacao";

describe("previaDaConciliacao", () => {
  it("saldo do banco maior: lança a diferença como rendimento", () => {
    const p = previaDaConciliacao(103_500, 100_000);
    expect(p.resultado).toBe("LANCADO");
    expect(p.diferenca).toBe(3_500);
    expect(p.mensagem).toBe(`Diferença: ${formatarReais(3_500)}, lançada como Rendimentos hoje`);
  });

  it("saldos iguais: confere", () => {
    const p = previaDaConciliacao(100_000, 100_000);
    expect(p.resultado).toBe("CONFERE");
    expect(p.diferenca).toBe(0);
    expect(p.mensagem).toBe("O saldo já confere; nada a lançar.");
  });

  it("saldo do banco menor: não lança", () => {
    const p = previaDaConciliacao(90_000, 100_000);
    expect(p.resultado).toBe("MENOR");
    expect(p.diferenca).toBe(-10_000);
    expect(p.mensagem).toBe("O saldo no banco é menor que o do sistema: pode haver um resgate ou uma transferência ainda não lançados.");
  });
});
