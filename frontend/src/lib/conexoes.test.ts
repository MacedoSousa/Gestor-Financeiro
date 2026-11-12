import { describe, expect, it } from "vitest";
import { avisoDoSlot, estadoDoItem, rotuloDoSlot, separarConexoes, textoDoConsentimento, validarCadastro } from "./conexoes";

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

describe("validarCadastro", () => {
  const ok = { slot: 1, itemId: " 3f1c-item ", banco: " Itaú " };

  it("pedido completo não tem erros", () => {
    expect(validarCadastro(ok)).toEqual({});
  });

  it("o slot é escolhido explicitamente: sem escolha é erro", () => {
    expect(validarCadastro({ ...ok, slot: null })).toEqual({ slot: "Escolha em qual slot estão as suas chaves." });
  });

  it("id do item e banco obrigatórios, aparados", () => {
    expect(validarCadastro({ ...ok, itemId: "   ", banco: "" })).toEqual({
      itemId: "Cole o id do item, copiado do Meu Pluggy.",
      banco: "Informe o nome do banco.",
    });
  });

  it("limites de tamanho iguais aos da API", () => {
    expect(validarCadastro({ ...ok, itemId: "x".repeat(101), banco: "b".repeat(61) })).toEqual({
      itemId: "O id do item tem no máximo 100 caracteres.",
      banco: "O nome do banco tem no máximo 60 caracteres.",
    });
    expect(validarCadastro({ ...ok, itemId: "x".repeat(100), banco: "b".repeat(60) })).toEqual({});
  });

  it("sem slot no formulário de acrescentar banco, o slot não é cobrado", () => {
    expect(validarCadastro({ itemId: "x", banco: "Nubank" })).toEqual({});
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
