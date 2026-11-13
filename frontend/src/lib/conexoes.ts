/** Regras de exibição da tela Conexões (Open Finance pelo Meu Pluggy). Nada aqui calcula dinheiro. */

import { formatarReais } from "./dinheiro";
import { soNomeDoMes } from "./mes";

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

/**
 * Ao trocar a escolha do banco, os erros dela saem: o "Escolha o banco." do select não pode cair no campo de código que
 * aparece com "Outro". Os outros erros do formulário ficam.
 */
export function limparErrosDoBanco<T extends ErrosDoBanco>(erros: T): Omit<T, "codigoBanco" | "banco"> {
  const resto: Partial<T> = { ...erros };
  delete resto.codigoBanco;
  delete resto.banco;
  return resto as Omit<T, "codigoBanco" | "banco">;
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

// ------------------------------------------------------------------ sincronização (Tarefa 4.5)

/** "2026-03-15" → "Histórico desde 15/03" (dia do calendário, sem fuso). Nulo antes da primeira sincronização. */
export function textoDoHistorico(dia: string | null | undefined): string | null {
  if (!dia) return null;
  return `Histórico desde ${dia.slice(8, 10)}/${dia.slice(5, 7)}`;
}

/** Frase com o valor à parte: a tela põe só o valor na fonte de número (classe `num`). */
export type FraseComValor = { antes: string; valor: string; depois: string };

/**
 * Saldo do banco menos o do sistema, quando já persistiu 2 sincronizações (a API manda nulo antes): diz quem tem mais.
 */
export function textoDaDiferenca(centavos: number | null | undefined): FraseComValor | null {
  if (centavos == null) return null;
  return { antes: "O banco mostra ", valor: formatarReais(Math.abs(centavos)), depois: ` a ${centavos > 0 ? "mais" : "menos"} que o sistema` };
}

/**
 * Grupo de investimento (D21): o valor no banco, só quando difere do saldo do sistema na última sincronização. Nada é
 * lançado: o rendimento entra no sistema na próxima etapa (E6).
 */
export function textoDoGrupoNoBanco(
  valorNoBancoCentavos: number | null | undefined,
  diferencaCentavos: number | null | undefined,
): FraseComValor | null {
  if (valorNoBancoCentavos == null || diferencaCentavos == null) return null;
  return {
    antes: "No banco hoje: ",
    valor: formatarReais(valorNoBancoCentavos),
    depois: " · o rendimento entra no sistema na próxima etapa",
  };
}

const dataBr = (iso: string) => `${iso.slice(8, 10)}/${iso.slice(5, 7)}/${iso.slice(0, 4)}`;

/**
 * Nota do patrimônio (D21): o grupo de investimento entra no patrimônio a partir do dia em que foi confirmado (o saldo
 * inicial dele), não antes. Nulo sem grupo ligado ao banco.
 */
export function textoDoPatrimonioDosInvestimentos(
  contas: readonly { tipo: string; ligada?: boolean | null; arquivada?: boolean | null; saldoInicialEm: string }[],
): string | null {
  // Grupo arquivado não conta mais para o que a nota explica.
  const datas = [...new Set(contas.filter((c) => c.tipo === "INVESTIMENTO" && c.ligada && !c.arquivada).map((c) => c.saldoInicialEm))].sort();
  const lista = datas.map(dataBr);
  const ultima = lista.pop();
  if (ultima === undefined) return null;
  if (lista.length === 0) return `Investimentos contam a partir de ${ultima} (data da confirmação).`;
  return `Investimentos contam a partir de ${lista.join(", ")} e ${ultima} (data da confirmação de cada grupo).`;
}

/** Fatura do banco cujo total não bate com a soma das compras dela (total do banco menos a soma); o mês é o do vencimento. */
export function textoDaFatura(f: { vencimento: string; diferencaCentavos: number }): FraseComValor {
  return {
    antes: `Fatura de ${soNomeDoMes(f.vencimento.slice(0, 7))}: o banco soma `,
    valor: formatarReais(Math.abs(f.diferencaCentavos)),
    depois: ` a ${f.diferencaCentavos > 0 ? "mais" : "menos"} que as compras dela`,
  };
}

const plural = (n: number, um: string, varios: string) => `${n} ${n === 1 ? um : varios}`;

/** O que a sincronização fez, em uma frase (só contagens). */
type Contagem = number | string; // inteiros da API chegam como number | string no tipo gerado

export function textoDaSincronizacao(
  resumo: { contasSincronizadas: Contagem; contasComFalha: Contagem; lancamentosNovos: Contagem; lancamentosAtualizados?: Contagem },
  banco?: string,
): string {
  const r = {
    contasSincronizadas: Number(resumo.contasSincronizadas),
    contasComFalha: Number(resumo.contasComFalha),
    lancamentosNovos: Number(resumo.lancamentosNovos),
  };
  if (r.contasSincronizadas === 0 && r.contasComFalha === 0) return "Nada para sincronizar: nenhuma conta ativa deste banco.";
  const novos = r.lancamentosNovos === 0 ? "nenhum lançamento novo" : plural(r.lancamentosNovos, "lançamento novo", "lançamentos novos");
  const feito = `${banco ? `${banco} sincronizado` : "Sincronizado"}: ${plural(r.contasSincronizadas, "conta", "contas")}, ${novos}.`;
  if (r.contasComFalha === 0) return feito;
  const falha = r.contasComFalha === 1 ? "1 conta não veio" : `${r.contasComFalha} contas não vieram`;
  return `${feito} ${falha} do banco agora; a próxima sincronização tenta de novo.`;
}
