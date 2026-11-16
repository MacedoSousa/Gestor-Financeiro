/** Lógica de exibição da caixa "A conferir" (Fase 2, Tarefas 6.1 e 6.2). Nenhum valor é calculado aqui: vêm da API. */

type ItemBasico = { tipo: string; valorCentavos: number | string };
type ContaBasica = { id: string; tipo: string; arquivada: boolean };
type CategoriaBasica = { id: string; tipo: string; arquivada: boolean };

export type Pede = "conta" | "categoria" | "nada";

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
  CIENTE: "Ciente",
  DEIXAR_PARA_DEPOIS: "Deixar para depois",
};

export const rotuloDaOpcao = (opcao: string): string => ROTULOS[opcao] ?? opcao;

export function oQuePede(opcao: string): Pede {
  if (opcao === "TRANSFERENCIA" || opcao === "APORTE" || opcao === "RESGATE") return "conta";
  if (opcao === "GASTO" || opcao === "RECEITA") return "categoria";
  return "nada";
}

/** As outras contas não arquivadas; aporte e resgate só com poupança ou investimento (a API recusa o resto). */
export function contasDaOpcao<C extends ContaBasica>(opcao: string, contaDoItem: string, contas: readonly C[]): C[] {
  if (oQuePede(opcao) !== "conta") return [];
  return contas.filter(
    (c) => !c.arquivada && c.id !== contaDoItem && (opcao === "TRANSFERENCIA" || c.tipo === "POUPANCA" || c.tipo === "INVESTIMENTO"),
  );
}

/** Aporte e resgate com uma conta só que serve: ela já vem escolhida (o casal confere). Transferência nunca vem escolhida. */
export function contaSugerida(opcao: string, contaDoItem: string, contas: readonly ContaBasica[]): string {
  if (opcao !== "APORTE" && opcao !== "RESGATE") return "";
  const servem = contasDaOpcao(opcao, contaDoItem, contas);
  const [unica, ...outras] = servem;
  return unica && outras.length === 0 ? unica.id : "";
}

/** Categorias não arquivadas: receita para "é receita", despesa para "é gasto". */
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
    case "DEIXAR_PARA_DEPOIS":
      return "Fica em “A conferir” para depois.";
    default:
      return "Resolvido.";
  }
}
