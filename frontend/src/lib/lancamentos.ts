import type { Conta } from "../api/contas";
import type { Categoria, Lancamento } from "../api/lancamentos";

export type TipoDeEntrada = "DESPESA" | "RECEITA" | "ESTORNO";

export const AVISO_DE_APORTE =
  "Para guardar dinheiro, prefira Transferência para a conta de investimento. Assim o aporte aparece no saldo dela e não corre o risco de contar duas vezes.";

/** Categorias ativas oferecidas para o tipo; estorno é despesa sem papel. */
export function categoriasDoTipo(tipo: TipoDeEntrada, categorias: Categoria[]): Categoria[] {
  return categorias.filter((c) => {
    if (c.arquivada) return false;
    if (tipo === "ESTORNO") return c.tipo === "DESPESA" && c.papel == null;
    return c.tipo === tipo;
  });
}

/** Despesa em categoria Investimento saindo de conta que não guarda o aporte: risco de contar duas vezes. */
export function avisaAporteEmDobro(tipo: TipoDeEntrada | "TRANSFERENCIA", categoria?: Categoria, conta?: Conta): boolean {
  return (
    tipo === "DESPESA" &&
    categoria?.papel === "INVESTIMENTO" &&
    conta != null &&
    conta.tipo !== "POUPANCA" &&
    conta.tipo !== "INVESTIMENTO"
  );
}

/** Receita cuja categoria é de despesa é um estorno. */
export function ehEstorno(l: Lancamento, categorias: Categoria[]): boolean {
  if (l.natureza !== "RECEITA" || !l.categoria) return false;
  const id = l.categoria.id;
  return categorias.find((c) => c.id === id)?.tipo === "DESPESA";
}

/**
 * Como o editor trata um lançamento de conta ligada ao banco (opção B), a mesma regra da API:
 * - `travada`: valor, data, tipo e contraparte não mudam à mão em conta ligada, seja a linha do banco ou digitada antes de
 *   ligar; só descrição e categoria;
 * - `apagavel`: o que veio do banco (OPEN_FINANCE) ou do sistema (SISTEMA) some quando o banco diz; o que o casal digitou
 *   antes de ligar ele apaga, para limpar o que o banco trouxe em dobro.
 * Sem a lista de contas, trata como manual (a API continua sendo a regra).
 */
export function edicaoEmContaLigada(
  l: Pick<Lancamento, "conta" | "origem">,
  contas: readonly Pick<Conta, "id" | "ligada">[] | undefined,
): { travada: boolean; apagavel: boolean } {
  const ligada = contas?.find((c) => c.id === l.conta.id)?.ligada === true;
  const doBanco = l.origem === "OPEN_FINANCE" || l.origem === "SISTEMA";
  return { travada: ligada, apagavel: !(ligada && doBanco) };
}

/** Contas que aceitam lançamento à mão: nem arquivadas nem ligadas ao banco (opção B; a API recusa com 409). */
export function contasParaLancar<T extends Pick<Conta, "arquivada" | "ligada">>(contas: readonly T[]): T[] {
  return contas.filter((c) => !c.arquivada && !c.ligada);
}

/**
 * Importado ainda sem categoria (D15): o editor não marca nenhuma de antemão. Salvar sem escolher revisaria o lançamento
 * com uma categoria que a pessoa não escolheu, e ele passaria a contar no orçamento.
 */
export function exigeEscolhaDeCategoria(l: Pick<Lancamento, "revisao" | "categoria">): boolean {
  return l.revisao === "A_REVISAR" && !l.categoria;
}

/** De onde veio o lançamento de conta ligada: do banco, da mão do casal antes de ligar, ou do sistema (D12). */
export function deOndeVeio(l: { origem: string; natureza: string }): string {
  if (l.origem === "MANUAL") return "Esta conta agora vem do banco. Lançado à mão antes de ligar";
  if (l.origem !== "SISTEMA") return "Veio do banco";
  return "Criado pelo sistema como o outro lado, ao resolver em “A conferir”";
}

/** Transferência do banco não tem categoria (D9): no editor, só a descrição muda. */
export const oQueSeMudaNoImportado = (transferencia: boolean): string =>
  transferencia ? "Aqui você muda só a descrição" : "Aqui você escolhe a categoria e, se quiser, a descrição";

/**
 * Fim da nota do editor para um lançamento do banco (E6, item 2). Pagamento de fatura, transferência entre as contas do
 * casal e investimento nunca "passam a contar no orçamento" (D23): o sistema os reconhece como transferência.
 */
export function fimDaNotaDoImportado(l: { revisao?: string | null; entreContas?: boolean | null }): string {
  if (l.entreContas) return ". Pagamento de fatura e transferência entre as contas de vocês não entram no orçamento";
  return l.revisao === "A_REVISAR" ? "; salvar marca como revisado e o lançamento passa a contar no orçamento" : "";
}
