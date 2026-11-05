const reais = new Intl.NumberFormat("pt-BR", { style: "currency", currency: "BRL" });

/** A API trafega dinheiro em centavos (inteiro). Converta só na hora de exibir. */
export function formatarReais(centavos: number): string {
  return reais.format(centavos / 100);
}
