import { describe, expect, it } from "vitest";
import {
  avisoDoSlot,
  estadoDoItem,
  limparErrosDoBanco,
  nomeDoBancoEscolhido,
  OUTRO_BANCO,
  pedidoDoBanco,
  rotuloDoSlot,
  separarConexoes,
  textoDaDiferenca,
  textoDaFatura,
  textoDaSincronizacao,
  textoDoConsentimento,
  textoDoHistorico,
  validarBanco,
  validarCadastro,
} from "./conexoes";

const slot = (n: number, configurado: boolean, emUso: boolean) => ({ slot: n, configurado, emUso });

describe("estadoDoItem", () => {
  it("ativo é positivo", () => {
    expect(estadoDoItem("ATIVO", null)).toEqual({ rotulo: "Ativo", tom: "ok", explicacao: null });
  });

  it("consentimento vencendo pede renovação", () => {
    const e = estadoDoItem("CONSENTIMENTO_VENCENDO", null);
    expect(e.rotulo).toBe("Consentimento vencendo");
    expect(e.tom).toBe("alerta");
    expect(e.explicacao).toBe("Renove o consentimento no Meu Pluggy antes da data.");
  });

  it("consentimento vencido para a sincronização", () => {
    expect(estadoDoItem("CONSENTIMENTO_VENCIDO", null)).toEqual({
      rotulo: "Consentimento vencido",
      tom: "erro",
      explicacao: "A sincronização parou. Renove o consentimento no Meu Pluggy.",
    });
  });

  it.each([
    ["LOGIN_ERROR", "O banco recusou o acesso. Reconecte o banco no Meu Pluggy."],
    ["OUTDATED", "O banco não atualizou os dados. Atualize o banco no Meu Pluggy."],
    ["WAITING_USER_INPUT", "O banco pede uma confirmação sua no Meu Pluggy."],
  ])("erro %s vira texto em português", (motivo, texto) => {
    expect(estadoDoItem("ERRO", motivo)).toEqual({ rotulo: "Erro no banco", tom: "erro", explicacao: texto });
  });

  it("erro com código desconhecido ou sem código tem texto genérico, sem mostrar o código", () => {
    const generico = "O banco respondeu com erro. Confira o banco no Meu Pluggy.";
    expect(estadoDoItem("ERRO", "DESCONHECIDO").explicacao).toBe(generico);
    expect(estadoDoItem("ERRO", null).explicacao).toBe(generico);
    expect(estadoDoItem("ERRO", "ALGO_NOVO").explicacao).toBe(generico);
  });

  it("estado que o front não conhece não quebra a tela", () => {
    expect(estadoDoItem("OUTRO", null)).toEqual({ rotulo: "Estado desconhecido", tom: "normal", explicacao: null });
  });
});

describe("textoDoConsentimento", () => {
  it("data do dia, sem fuso", () => {
    expect(textoDoConsentimento("2026-11-14", "ATIVO")).toBe("Consentimento até 14/11/2026");
  });
  it("quando o estado já fala de consentimento, não repete a palavra", () => {
    expect(textoDoConsentimento("2026-11-14", "CONSENTIMENTO_VENCENDO")).toBe("até 14/11/2026");
    expect(textoDoConsentimento("2026-11-01", "CONSENTIMENTO_VENCIDO")).toBe("até 01/11/2026");
  });
  it("sem data, sem texto", () => {
    expect(textoDoConsentimento(null, "ATIVO")).toBeNull();
    expect(textoDoConsentimento(undefined, "ERRO")).toBeNull();
  });
});

describe("rotuloDoSlot e avisoDoSlot", () => {
  it("o rótulo diz em quais arquivos estão as chaves", () => {
    expect(rotuloDoSlot(1)).toBe("Slot 1 · arquivos pluggy_1_*");
    expect(rotuloDoSlot(2)).toBe("Slot 2 · arquivos pluggy_2_*");
  });

  it("slot livre e configurado não tem aviso", () => {
    expect(avisoDoSlot(slot(1, true, false))).toBeNull();
  });

  it("slot sem chaves avisa quais arquivos faltam", () => {
    expect(avisoDoSlot(slot(2, false, false))).toBe(
      "As chaves do slot 2 ainda não foram gravadas no servidor (arquivos pluggy_2_client_id e pluggy_2_client_secret).",
    );
  });

  it("slot em uso avisa antes de faltar chave", () => {
    expect(avisoDoSlot(slot(1, false, true))).toBe("Já usado pela outra conexão.");
  });
});

const itau = { escolha: "341", codigoOutro: "", nomeOutro: "" };
const outro = { escolha: OUTRO_BANCO, codigoOutro: " 999 ", nomeOutro: " Banco Sintético " };
const bancos = [
  { codigo: "260", nome: "Nubank" },
  { codigo: "341", nome: "Itaú" },
];

describe("escolha do banco (D18)", () => {
  it("banco da lista manda só o código; Outro manda código e nome aparados", () => {
    expect(pedidoDoBanco(itau)).toEqual({ codigoBanco: "341", banco: null });
    expect(pedidoDoBanco(outro)).toEqual({ codigoBanco: "999", banco: "Banco Sintético" });
  });

  it("sem escolha é erro no código", () => {
    expect(validarBanco({ escolha: "", codigoOutro: "", nomeOutro: "" })).toEqual({ codigoBanco: "Escolha o banco." });
  });

  it("Outro exige 3 dígitos e o nome, com o limite da API", () => {
    expect(validarBanco(outro)).toEqual({});
    expect(validarBanco({ escolha: OUTRO_BANCO, codigoOutro: "34a", nomeOutro: "" })).toEqual({
      codigoBanco: "O código do banco tem 3 dígitos.",
      banco: "Informe o nome do banco.",
    });
    expect(validarBanco({ escolha: OUTRO_BANCO, codigoOutro: "1234", nomeOutro: "b".repeat(61) })).toEqual({
      codigoBanco: "O código do banco tem 3 dígitos.",
      banco: "O nome do banco tem no máximo 60 caracteres.",
    });
  });

  it("trocar a escolha limpa os erros do banco (o do select não passa para o código do Outro) e mantém os outros", () => {
    expect(limparErrosDoBanco({ itemId: "x", codigoBanco: "Escolha o banco.", banco: "Informe o nome do banco." })).toEqual({ itemId: "x" });
    expect(limparErrosDoBanco({})).toEqual({});
  });

  it("nome para a mensagem de sucesso: o da lista ou o digitado", () => {
    expect(nomeDoBancoEscolhido(itau, bancos)).toBe("Itaú");
    expect(nomeDoBancoEscolhido(outro, bancos)).toBe("Banco Sintético");
    expect(nomeDoBancoEscolhido({ escolha: OUTRO_BANCO, codigoOutro: "341", nomeOutro: "x" }, bancos)).toBe("Itaú");
  });
});

describe("validarCadastro", () => {
  const ok = { slot: 1, itemId: " 3f1c-item ", banco: itau };

  it("pedido completo não tem erros", () => {
    expect(validarCadastro(ok)).toEqual({});
  });

  it("o slot é escolhido explicitamente: sem escolha é erro", () => {
    expect(validarCadastro({ ...ok, slot: null })).toEqual({ slot: "Escolha em qual slot estão as suas chaves." });
  });

  it("id do item e banco obrigatórios, aparados", () => {
    expect(validarCadastro({ ...ok, itemId: "   ", banco: { escolha: "", codigoOutro: "", nomeOutro: "" } })).toEqual({
      itemId: "Cole o id do item, copiado do Meu Pluggy.",
      codigoBanco: "Escolha o banco.",
    });
  });

  it("limites de tamanho iguais aos da API", () => {
    expect(validarCadastro({ ...ok, itemId: "x".repeat(101), banco: { ...outro, nomeOutro: "b".repeat(61) } })).toEqual({
      itemId: "O id do item tem no máximo 100 caracteres.",
      banco: "O nome do banco tem no máximo 60 caracteres.",
    });
    expect(validarCadastro({ ...ok, itemId: "x".repeat(100), banco: { ...outro, nomeOutro: "b".repeat(60) } })).toEqual({});
  });

  it("sem slot no formulário de acrescentar banco, o slot não é cobrado", () => {
    expect(validarCadastro({ itemId: "x", banco: itau })).toEqual({});
  });
});

describe("separarConexoes", () => {
  const conexao = (id: string, ehSua: boolean) => ({
    id,
    usuarioId: `u-${id}`,
    apelido: id,
    slot: 1,
    slotConfigurado: true,
    ehSua,
    items: [],
  });

  it("separa a sua da do outro cônjuge", () => {
    const r = separarConexoes([conexao("Bruno", false), conexao("Ana", true)]);
    expect(r.sua?.id).toBe("Ana");
    expect(r.outra?.id).toBe("Bruno");
  });

  it("nenhuma conexão", () => {
    expect(separarConexoes([])).toEqual({ sua: null, outra: null });
  });
});

describe("textos da sincronização (Tarefa 4.5)", () => {
  it("histórico desde DD/MM, sem fuso; nulo antes da primeira sincronização", () => {
    expect(textoDoHistorico("2026-03-15")).toBe("Histórico desde 15/03");
    expect(textoDoHistorico(null)).toBeNull();
  });

  it("diferença de saldo diz quem tem mais; o valor vem à parte, para a fonte de número (testador, notas 6 e 7)", () => {
    expect(textoDaDiferenca(1_000)).toEqual({ antes: "O banco mostra ", valor: "R$\u00a010,00", depois: " a mais que o sistema" });
    expect(textoDaDiferenca(-250_050)).toEqual({ antes: "O banco mostra ", valor: "R$\u00a02.500,50", depois: " a menos que o sistema" });
    expect(textoDaDiferenca(null)).toBeNull();
  });

  it("fatura do banco diferente: mês por extenso do vencimento", () => {
    expect(textoDaFatura({ vencimento: "2026-04-10", diferencaCentavos: 10_000 })).toEqual({
      antes: "Fatura de abril: o banco soma ",
      valor: "R$\u00a0100,00",
      depois: " a mais que as compras dela",
    });
  });

  it("resumo da sincronização fala de contas e lançamentos, e avisa a falha", () => {
    expect(textoDaSincronizacao({ contasSincronizadas: 2, contasComFalha: 0, lancamentosNovos: 37, lancamentosAtualizados: 1 })).toBe(
      "Sincronizado: 2 contas, 37 lançamentos novos.",
    );
    expect(textoDaSincronizacao({ contasSincronizadas: 2, contasComFalha: 0, lancamentosNovos: 0, lancamentosAtualizados: 0 }, "Itaú")).toBe(
      "Itaú sincronizado: 2 contas, nenhum lançamento novo.",
    );
    expect(textoDaSincronizacao({ contasSincronizadas: 1, contasComFalha: 1, lancamentosNovos: 1, lancamentosAtualizados: 0 })).toBe(
      "Sincronizado: 1 conta, 1 lançamento novo. 1 conta não veio do banco agora; a próxima sincronização tenta de novo.",
    );
    expect(textoDaSincronizacao({ contasSincronizadas: 0, contasComFalha: 0, lancamentosNovos: 0, lancamentosAtualizados: 0 })).toBe(
      "Nada para sincronizar: nenhuma conta ativa deste banco.",
    );
  });
});
