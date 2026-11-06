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
