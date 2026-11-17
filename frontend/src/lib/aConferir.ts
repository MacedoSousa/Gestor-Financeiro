/** Lógica de exibição da caixa "A conferir" (Fase 2, Tarefas 6.1 a 6.3). Nenhum valor é calculado aqui: vêm da API. */

import { formatarReais } from "./dinheiro";

type ItemBasico = { tipo: string; valorCentavos: number | string };
type ContaBasica = { id: string; tipo: string; arquivada: boolean };
type CategoriaBasica = { id: string; tipo: string; arquivada: boolean };

export type Pede = "conta" | "categoria" | "movimento" | "nada";

export function tituloDoItem(item: ItemBasico): string {
  const saida = Number(item.valorCentavos) < 0;
  switch (item.tipo) {
    case "TRANSFERENCIA_SEM_PAR":
      return saida ? "Saída sem o outro lado" : "Entrada sem o outro lado";
    case "APORTE_OU_RESGATE":
      return saida ? "Aplicação" : "Resgate";
    case "PENDENTE_SUMIU":
      return "Compra a confirmar que o banco tirou";
    case "PENDENTE_SEM_PAR":
      return "Compra a confirmar sem uma definitiva única";
    case "DIFERENCA_DO_GRUPO":
      return saida ? "O banco mostra menos que o sistema no investimento" : "O banco mostra mais que o sistema no investimento";
    default:
      return "Item a conferir";
  }
}

const ROTULOS: Record<string, string> = {
  TRANSFERENCIA: "É transferência entre contas",
  GASTO: "É gasto",
  RECEITA: "É receita",
  APORTE: "É aporte num investimento",
  RESGATE: "É resgate de um investimento",
  RENDIMENTO: "É rendimento",
  IR_IOF_TAXA: "É IR, IOF ou taxa",
  PAREAR_MOVIMENTO: "Veio de um aporte ou resgate",
  AJUSTAR_SALDO_INICIAL: "Ajustar o saldo inicial do grupo",
  CIENTE: "Ciente",
  DEIXAR_PARA_DEPOIS: "Deixar para depois",
};

export const rotuloDaOpcao = (opcao: string): string => ROTULOS[opcao] ?? opcao;

export function oQuePede(opcao: string): Pede {
  if (opcao === "TRANSFERENCIA" || opcao === "APORTE" || opcao === "RESGATE") return "conta";
  if (opcao === "GASTO" || opcao === "RECEITA" || opcao === "IR_IOF_TAXA") return "categoria";
  if (opcao === "PAREAR_MOVIMENTO") return "movimento";
  return "nada";
}

/** As outras contas não arquivadas; aporte e resgate só com poupança ou investimento (a API recusa o resto). */
export function contasDaOpcao<C extends ContaBasica>(opcao: string, contaDoItem: string, contas: readonly C[]): C[] {
  if (oQuePede(opcao) !== "conta") return [];
  return contas.filter(
    (c) => !c.arquivada && c.id !== contaDoItem && (opcao === "TRANSFERENCIA" || c.tipo === "POUPANCA" || c.tipo === "INVESTIMENTO"),
  );
}

/**
 * O aviso do outro lado (a mesma regra da API): conta ligada que não é o grupo de investimento só recebe o lado quando
 * ele vem do banco; no grupo e em conta manual, o sistema o cria.
 */
export function avisoDaConta(opcao: string, escolha: string, contas: readonly (ContaBasica & { ligada?: boolean })[]): string | undefined {
  const destino = contas.find((c) => c.id === escolha);
  if (destino?.ligada && destino.tipo !== "INVESTIMENTO") return "Essa conta vem do banco: o outro lado precisa já ter chegado.";
  if (opcao === "TRANSFERENCIA" && !escolha) return "Se a outra conta vem do banco, o outro lado precisa já ter chegado.";
  return undefined;
}

/** Aporte e resgate com uma conta só que serve: ela já vem escolhida (o casal confere). Transferência nunca vem escolhida. */
export function contaSugerida(opcao: string, contaDoItem: string, contas: readonly ContaBasica[]): string {
  if (opcao !== "APORTE" && opcao !== "RESGATE") return "";
  const servem = contasDaOpcao(opcao, contaDoItem, contas);
  const [unica, ...outras] = servem;
  return unica && outras.length === 0 ? unica.id : "";
}

/**
 * D25: a diferença do grupo se pareia com um aporte ou resgate ainda aberto em "A conferir", de data a partir do saldo
 * inicial do grupo (o de antes já está nele; a API recusa o resto). Sem o grupo, nenhum.
 */
export function movimentosDoGrupo<I extends { tipo: string; data: string }>(itens: readonly I[], grupo: { saldoInicialEm: string } | undefined): I[] {
  if (!grupo) return [];
  return itens.filter((i) => i.tipo === "APORTE_OU_RESGATE" && i.data >= grupo.saldoInicialEm);
}

/** O aporte ou resgate na escolha do par: do ponto de vista do grupo (aporte entra, resgate sai), sem o sinal da corrente. */
export function rotuloDoMovimento(m: ItemBasico & { data: string; contaApelido: string; descricao?: string | null }): string {
  const valor = Number(m.valorCentavos);
  const dia = `${m.data.slice(8, 10)}/${m.data.slice(5, 7)}`;
  return `${m.descricao || tituloDoItem(m)} · ${m.contaApelido} · ${dia} · ${valor < 0 ? "aporte" : "resgate"} de ${formatarReais(Math.abs(valor))}`;
}

/** Categorias não arquivadas: receita para "é receita", despesa para "é gasto" e "IR, IOF ou taxa". */
export function categoriasDaOpcao<C extends CategoriaBasica>(opcao: string, categorias: readonly C[]): C[] {
  if (oQuePede(opcao) !== "categoria") return [];
  const tipo = opcao === "RECEITA" ? "RECEITA" : "DESPESA";
  return categorias.filter((c) => !c.arquivada && c.tipo === tipo);
}

export function textoDaResolucao(opcao: string): string {
  switch (opcao) {
    case "TRANSFERENCIA":
      return "Resolvido: virou transferência e não conta no orçamento.";
    case "APORTE":
      return "Resolvido: virou aporte e não conta no orçamento.";
    case "RESGATE":
      return "Resolvido: virou resgate e não conta no orçamento.";
    case "GASTO":
    case "RECEITA":
      return "Resolvido: o lançamento passa a contar no orçamento.";
    case "RENDIMENTO":
      return "Resolvido: a diferença entrou como rendimento.";
    case "IR_IOF_TAXA":
      return "Resolvido: a despesa foi lançada no investimento.";
    case "AJUSTAR_SALDO_INICIAL":
      return "Resolvido: o saldo inicial do grupo foi ajustado.";
    case "PAREAR_MOVIMENTO":
      return "Pareado: o aporte ou resgate entrou no grupo. Se sobrou diferença, ela continua aqui.";
    case "DEIXAR_PARA_DEPOIS":
      return "Fica em “A conferir” para depois.";
    default:
      return "Resolvido.";
  }
}
