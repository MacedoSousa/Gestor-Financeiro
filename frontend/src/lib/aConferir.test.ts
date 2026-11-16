import { describe, expect, it } from "vitest";
import { avisoDaConta, contaSugerida, contasDaOpcao, categoriasDaOpcao, oQuePede, rotuloDaOpcao, textoDaResolucao, tituloDoItem } from "./aConferir";

const conta = (id: string, tipo: string, arquivada = false) => ({ id, apelido: id, tipo, arquivada });
const contas = [
  conta("corrente", "CORRENTE"),
  conta("cartao", "CARTAO"),
  conta("dinheiro", "DINHEIRO"),
  conta("cdb", "INVESTIMENTO"),
  conta("poupanca", "POUPANCA"),
  conta("velha", "CORRENTE", true),
];
const categoria = (id: string, tipo: "DESPESA" | "RECEITA", arquivada = false) => ({ id, nome: id, tipo, arquivada });
const categorias = [categoria("mercado", "DESPESA"), categoria("tarifa-velha", "DESPESA", true), categoria("salario", "RECEITA")];

describe("A conferir (Tarefas 6.1 e 6.2)", () => {
  it("diz o que é cada item, pelo tipo e pelo sinal", () => {
    expect(tituloDoItem({ tipo: "TRANSFERENCIA_SEM_PAR", valorCentavos: -100 })).toBe("Saída sem o outro lado");
    expect(tituloDoItem({ tipo: "TRANSFERENCIA_SEM_PAR", valorCentavos: 100 })).toBe("Entrada sem o outro lado");
    expect(tituloDoItem({ tipo: "APORTE_OU_RESGATE", valorCentavos: -100 })).toBe("Aplicação");
    expect(tituloDoItem({ tipo: "APORTE_OU_RESGATE", valorCentavos: "100" })).toBe("Resgate");
    expect(tituloDoItem({ tipo: "PENDENTE_SUMIU", valorCentavos: -100 })).toBe("Compra a confirmar que o banco tirou");
    expect(tituloDoItem({ tipo: "PENDENTE_SEM_PAR", valorCentavos: -100 })).toBe("Compra a confirmar sem uma definitiva única");
    expect(tituloDoItem({ tipo: "OUTRO", valorCentavos: -100 })).toBe("Item a conferir");
  });

  it("dá nome a cada opção da API; não existe “resgate não identificado” (decisão 4)", () => {
    expect(rotuloDaOpcao("TRANSFERENCIA")).toBe("É transferência entre contas");
    expect(rotuloDaOpcao("GASTO")).toBe("É gasto");
    expect(rotuloDaOpcao("RECEITA")).toBe("É receita");
    expect(rotuloDaOpcao("APORTE")).toBe("É aporte num investimento");
    expect(rotuloDaOpcao("RESGATE")).toBe("É resgate de um investimento");
    expect(rotuloDaOpcao("CIENTE")).toBe("Ciente");
    expect(rotuloDaOpcao("DEIXAR_PARA_DEPOIS")).toBe("Deixar para depois");
  });

  it("pede conta para o par, categoria para gasto e receita, e nada para o resto", () => {
    expect(oQuePede("TRANSFERENCIA")).toBe("conta");
    expect(oQuePede("APORTE")).toBe("conta");
    expect(oQuePede("RESGATE")).toBe("conta");
    expect(oQuePede("GASTO")).toBe("categoria");
    expect(oQuePede("RECEITA")).toBe("categoria");
    expect(oQuePede("CIENTE")).toBe("nada");
    expect(oQuePede("DEIXAR_PARA_DEPOIS")).toBe("nada");
  });

  it("oferece as outras contas não arquivadas; aporte e resgate só com poupança ou investimento", () => {
    expect(contasDaOpcao("TRANSFERENCIA", "corrente", contas).map((c) => c.id)).toEqual(["cartao", "dinheiro", "cdb", "poupanca"]);
    expect(contasDaOpcao("APORTE", "corrente", contas).map((c) => c.id)).toEqual(["cdb", "poupanca"]);
    expect(contasDaOpcao("RESGATE", "cdb", contas).map((c) => c.id)).toEqual(["poupanca"]);
    expect(contasDaOpcao("GASTO", "corrente", contas)).toEqual([]);
  });

  it("com uma conta só que serve ao aporte ou resgate, ela já vem escolhida; senão, nenhuma (testador da E6, achado 2)", () => {
    expect(contaSugerida("APORTE", "corrente", [conta("corrente", "CORRENTE"), conta("cdb", "INVESTIMENTO")])).toBe("cdb");
    expect(contaSugerida("RESGATE", "corrente", [conta("corrente", "CORRENTE"), conta("cdb", "INVESTIMENTO")])).toBe("cdb");
    expect(contaSugerida("APORTE", "corrente", contas)).toBe("");
    expect(contaSugerida("TRANSFERENCIA", "corrente", [conta("corrente", "CORRENTE"), conta("dinheiro", "DINHEIRO")])).toBe("");
  });

  it("avisa do outro lado só onde ele tem de vir do banco (revisão do E6a, rodada 3)", () => {
    const ligada = (id: string, tipo: string) => ({ id, tipo, arquivada: false, ligada: true });
    const comuns = [ligada("poupanca", "POUPANCA"), ligada("grupo", "INVESTIMENTO"), { ...conta("dinheiro", "DINHEIRO"), ligada: false }];
    expect(avisoDaConta("APORTE", "poupanca", comuns)).toBe("Essa conta vem do banco: o outro lado precisa já ter chegado.");
    expect(avisoDaConta("TRANSFERENCIA", "poupanca", comuns)).toBe("Essa conta vem do banco: o outro lado precisa já ter chegado.");
    expect(avisoDaConta("APORTE", "grupo", comuns)).toBeUndefined();
    expect(avisoDaConta("TRANSFERENCIA", "dinheiro", comuns)).toBeUndefined();
    expect(avisoDaConta("TRANSFERENCIA", "", comuns)).toBe("Se a outra conta vem do banco, o outro lado precisa já ter chegado.");
    expect(avisoDaConta("APORTE", "", comuns)).toBeUndefined();
  });

  it("oferece categorias não arquivadas: despesa para gasto, receita para receita", () => {
    expect(categoriasDaOpcao("GASTO", categorias).map((c) => c.id)).toEqual(["mercado"]);
    expect(categoriasDaOpcao("RECEITA", categorias).map((c) => c.id)).toEqual(["salario"]);
    expect(categoriasDaOpcao("TRANSFERENCIA", categorias)).toEqual([]);
  });

  it("confirma o que aconteceu depois de resolver", () => {
    expect(textoDaResolucao("TRANSFERENCIA")).toBe("Resolvido: virou transferência e não conta no orçamento.");
    expect(textoDaResolucao("APORTE")).toBe("Resolvido: virou aporte e não conta no orçamento.");
    expect(textoDaResolucao("RESGATE")).toBe("Resolvido: virou resgate e não conta no orçamento.");
    expect(textoDaResolucao("GASTO")).toBe("Resolvido: o lançamento passa a contar no orçamento.");
    expect(textoDaResolucao("RECEITA")).toBe("Resolvido: o lançamento passa a contar no orçamento.");
    expect(textoDaResolucao("CIENTE")).toBe("Resolvido.");
    expect(textoDaResolucao("DEIXAR_PARA_DEPOIS")).toBe("Fica em “A conferir” para depois.");
  });
});
