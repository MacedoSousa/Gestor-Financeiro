import { describe, expect, it } from "vitest";
import type { Conta } from "../api/contas";
import type { Categoria, Lancamento } from "../api/lancamentos";
import { avisaAporteEmDobro, categoriasDoTipo, contasParaLancar, edicaoEmContaLigada, ehEstorno, exigeEscolhaDeCategoria } from "./lancamentos";

const cat = (id: string, tipo: string, papel: string | null = null, arquivada = false): Categoria => ({ id, nome: id, tipo, arquivada, papel });
const conta = (tipo: string) => ({ id: "c", apelido: "c", tipo }) as Conta;

const mercado = cat("mercado", "DESPESA");
const invest = cat("invest", "DESPESA", "INVESTIMENTO");
const dividas = cat("dividas", "DESPESA", "DIVIDAS");
const velha = cat("velha", "DESPESA", null, true);
const salario = cat("salario", "RECEITA");
const todas = [mercado, invest, dividas, velha, salario];

describe("categoriasDoTipo", () => {
  it("estorno é só despesa sem papel e ativa", () => {
    expect(categoriasDoTipo("ESTORNO", todas)).toEqual([mercado]);
  });
  it("despesa e receita só trazem as ativas do tipo", () => {
    expect(categoriasDoTipo("DESPESA", todas)).toEqual([mercado, invest, dividas]);
    expect(categoriasDoTipo("RECEITA", todas)).toEqual([salario]);
  });
});

describe("avisaAporteEmDobro", () => {
  it("avisa despesa em Investimento na corrente", () => {
    expect(avisaAporteEmDobro("DESPESA", invest, conta("CORRENTE"))).toBe(true);
  });
  it("não avisa na poupança nem no investimento", () => {
    expect(avisaAporteEmDobro("DESPESA", invest, conta("POUPANCA"))).toBe(false);
    expect(avisaAporteEmDobro("DESPESA", invest, conta("INVESTIMENTO"))).toBe(false);
  });
  it("não avisa em receita nem em outra categoria", () => {
    expect(avisaAporteEmDobro("RECEITA", invest, conta("CORRENTE"))).toBe(false);
    expect(avisaAporteEmDobro("DESPESA", mercado, conta("CORRENTE"))).toBe(false);
  });
});

describe("ehEstorno", () => {
  const lanc = (natureza: string, id: string) => ({ natureza, categoria: { id, nome: id, arquivada: false } }) as Lancamento;
  it("receita em categoria de despesa é estorno", () => {
    expect(ehEstorno(lanc("RECEITA", "mercado"), todas)).toBe(true);
  });
  it("receita em Salário não é", () => {
    expect(ehEstorno(lanc("RECEITA", "salario"), todas)).toBe(false);
    expect(ehEstorno(lanc("DESPESA", "mercado"), todas)).toBe(false);
  });
});

describe("edicaoEmContaLigada (Tarefa 4.4; revisão da E4, rodada 2)", () => {
  const conta = { id: "itau", apelido: "Itaú", tipo: "CORRENTE" };
  const doBanco = { conta, origem: "OPEN_FINANCE" } as Lancamento;
  const manual = { conta, origem: "MANUAL" } as Lancamento;
  const ligada = [{ id: "itau", ligada: true } as Conta];

  it("linha do banco em conta ligada: só descrição e categoria, sem apagar", () => {
    expect(edicaoEmContaLigada(doBanco, ligada)).toEqual({ travada: true, apagavel: false });
    expect(edicaoEmContaLigada({ ...doBanco, origem: "SISTEMA" }, ligada)).toEqual({ travada: true, apagavel: false });
  });
  it("manual de antes de ligar: só descrição e categoria, mas apaga (limpar o que veio em dobro)", () => {
    expect(edicaoEmContaLigada(manual, ligada)).toEqual({ travada: true, apagavel: true });
  });
  it("conta manual, ou que a lista ainda não trouxe, edita e apaga como antes", () => {
    expect(edicaoEmContaLigada(doBanco, [{ id: "itau", ligada: false }])).toEqual({ travada: false, apagavel: true });
    expect(edicaoEmContaLigada(manual, undefined)).toEqual({ travada: false, apagavel: true });
  });
});

describe("contasParaLancar (testador da E4, achado 3)", () => {
  it("tira as arquivadas e as ligadas ao banco: lançar nelas dá 409", () => {
    const contas = [
      { id: "manual", arquivada: false, ligada: false },
      { id: "velha", arquivada: true, ligada: false },
      { id: "itau", arquivada: false, ligada: true },
    ] as Conta[];
    expect(contasParaLancar(contas).map((c) => c.id)).toEqual(["manual"]);
  });
});

describe("exigeEscolhaDeCategoria (testador da E4, achado 2)", () => {
  const base = { revisao: "A_REVISAR", categoria: null } as unknown as Lancamento;
  it("importado sem categoria não vem com uma categoria marcada: a pessoa escolhe", () => {
    expect(exigeEscolhaDeCategoria(base)).toBe(true);
  });
  it("com sugestão ou já revisado, a categoria atual vale", () => {
    expect(exigeEscolhaDeCategoria({ ...base, categoria: { id: "m", nome: "Mercado", arquivada: false } })).toBe(false);
    expect(exigeEscolhaDeCategoria({ ...base, revisao: "REVISADA" })).toBe(false);
  });
});
