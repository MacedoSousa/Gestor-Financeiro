/** Regras de exibição da tela Conexões (Open Finance pelo Meu Pluggy). Nada aqui calcula dinheiro. */

export type Tom = "ok" | "alerta" | "erro" | "normal";
export type AparenciaDoEstado = { rotulo: string; tom: Tom; explicacao: string | null };

/** Limites iguais aos da API (Item.ItemIdMaximo e Item.BancoMaximo). */
export const ITEM_ID_MAXIMO = 100;
export const BANCO_MAXIMO = 60;

const MOTIVOS: Readonly<Record<string, string>> = {
  LOGIN_ERROR: "O banco recusou o acesso. Reconecte o banco no Meu Pluggy.",
  OUTDATED: "O banco não atualizou os dados. Atualize o banco no Meu Pluggy.",
  WAITING_USER_INPUT: "O banco pede uma confirmação sua no Meu Pluggy.",
};
const MOTIVO_GENERICO = "O banco respondeu com erro. Confira o banco no Meu Pluggy.";

/** Estado do item (ATIVO, CONSENTIMENTO_VENCENDO, CONSENTIMENTO_VENCIDO, ERRO) em texto, tom e o que fazer. */
export function estadoDoItem(estado: string, motivo: string | null | undefined): AparenciaDoEstado {
  switch (estado) {
    case "ATIVO":
      return { rotulo: "Ativo", tom: "ok", explicacao: null };
    case "CONSENTIMENTO_VENCENDO":
      return { rotulo: "Consentimento vencendo", tom: "alerta", explicacao: "Renove o consentimento no Meu Pluggy antes da data." };
    case "CONSENTIMENTO_VENCIDO":
      return { rotulo: "Consentimento vencido", tom: "erro", explicacao: "A sincronização parou. Renove o consentimento no Meu Pluggy." };
    case "ERRO":
      return { rotulo: "Erro no banco", tom: "erro", explicacao: (motivo && MOTIVOS[motivo]) || MOTIVO_GENERICO };
    default:
      return { rotulo: "Estado desconhecido", tom: "normal", explicacao: null };
  }
}

/**
 * "2026-11-14" → "Consentimento até 14/11/2026" (data do dia, sem passar por fuso). Se o rótulo do estado já fala de
 * consentimento, fica só "até 14/11/2026".
 */
export function textoDoConsentimento(dia: string | null | undefined, estado: string): string | null {
  if (!dia) return null;
  const [ano, mes, d] = dia.split("-");
  const data = `${d}/${mes}/${ano}`;
  return estado.startsWith("CONSENTIMENTO_") ? `até ${data}` : `Consentimento até ${data}`;
}

export const rotuloDoSlot = (slot: number) => `Slot ${slot} · arquivos pluggy_${slot}_*`;

/** O que impede (ou atrapalha) escolher o slot: em uso por outra conexão ou sem as chaves no servidor. */
export function avisoDoSlot(s: { slot: number; configurado: boolean; emUso: boolean }): string | null {
  if (s.emUso) return "Já usado pela outra conexão.";
  if (!s.configurado)
    return `As chaves do slot ${s.slot} ainda não foram gravadas no servidor (arquivos pluggy_${s.slot}_client_id e pluggy_${s.slot}_client_secret).`;
  return null;
}

/** Valor do "Outro" na lista de bancos: código de 3 dígitos e nome digitados. */
export const OUTRO_BANCO = "OUTRO";

/** O que a pessoa escolheu: um código da lista, ou OUTRO_BANCO com o código e o nome digitados (D18). */
export type EscolhaDeBanco = { escolha: string; codigoOutro: string; nomeOutro: string };
export const ESCOLHA_DE_BANCO_VAZIA: EscolhaDeBanco = { escolha: "", codigoOutro: "", nomeOutro: "" };

export type ErrosDoBanco = Partial<Record<"codigoBanco" | "banco", string>>;

/** O corpo da API: da lista, só o código (o nome vem da lista no servidor); "Outro", código e nome aparados. */
export function pedidoDoBanco(e: EscolhaDeBanco): { codigoBanco: string; banco: string | null } {
  return e.escolha === OUTRO_BANCO ? { codigoBanco: e.codigoOutro.trim(), banco: e.nomeOutro.trim() } : { codigoBanco: e.escolha, banco: null };
}

/** Mesmas regras da API: banco escolhido; em "Outro", código de 3 dígitos e nome de 1 a 60 caracteres. */
export function validarBanco(e: EscolhaDeBanco): ErrosDoBanco {
  if (e.escolha === "") return { codigoBanco: "Escolha o banco." };
  if (e.escolha !== OUTRO_BANCO) return {};
  const erros: ErrosDoBanco = {};
  if (!/^\d{3}$/.test(e.codigoOutro.trim())) erros.codigoBanco = "O código do banco tem 3 dígitos.";
  const nome = e.nomeOutro.trim();
  if (nome.length === 0) erros.banco = "Informe o nome do banco.";
  else if (nome.length > BANCO_MAXIMO) erros.banco = `O nome do banco tem no máximo ${BANCO_MAXIMO} caracteres.`;
  return erros;
}

/** O nome que o servidor vai gravar: o da lista quando o código está nela, senão o digitado. */
export function nomeDoBancoEscolhido(e: EscolhaDeBanco, bancos: readonly { codigo: string; nome: string }[]): string {
  const { codigoBanco, banco } = pedidoDoBanco(e);
  return bancos.find((b) => b.codigo === codigoBanco)?.nome ?? banco ?? "";
}

export type PedidoDeCadastro = { slot?: number | null; itemId: string; banco: EscolhaDeBanco };
export type ErrosDoCadastro = Partial<Record<"slot" | "itemId" | "codigoBanco" | "banco", string>>;

/**
 * Mesmas regras da API, para avisar antes de enviar. Com `slot` presente no pedido (cadastro da conexão), ele é obrigatório:
 * o slot é escolhido explicitamente, nunca sugerido.
 */
export function validarCadastro(p: PedidoDeCadastro): ErrosDoCadastro {
  const erros: ErrosDoCadastro = {};
  if ("slot" in p && p.slot == null) erros.slot = "Escolha em qual slot estão as suas chaves.";
  const id = p.itemId.trim();
  if (id.length === 0) erros.itemId = "Cole o id do item, copiado do Meu Pluggy.";
  else if (id.length > ITEM_ID_MAXIMO) erros.itemId = `O id do item tem no máximo ${ITEM_ID_MAXIMO} caracteres.`;
  return { ...erros, ...validarBanco(p.banco) };
}

/** A conexão de quem está na sessão e a do outro cônjuge (cada um tem no máximo uma). */
export function separarConexoes<T extends { ehSua: boolean }>(conexoes: readonly T[]): { sua: T | null; outra: T | null } {
  return { sua: conexoes.find((c) => c.ehSua) ?? null, outra: conexoes.find((c) => !c.ehSua) ?? null };
}
