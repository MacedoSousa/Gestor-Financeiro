const reais = new Intl.NumberFormat("pt-BR", { style: "currency", currency: "BRL" });

/** A API trafega dinheiro em centavos (inteiro). Converta só na hora de exibir. */
export function formatarReais(centavos: number): string {
  return reais.format(centavos / 100);
}

/**
 * Texto digitado (formato brasileiro) para centavos. Aceita "142,50", "1.234,56", "R$ 23" e "23.90".
 * Ponto com 1 ou 2 casas no fim é decimal; com grupos de 3 dígitos é milhar. Vazio, negativo ou ambíguo → null.
 */
export function centavosDeTexto(texto: string): number | null {
  const limpo = texto.replace(/R\$/gi, "").replace(/\s/g, "");
  if (limpo === "" || limpo.includes("-")) return null;

  let inteiro: string;
  let decimal = "";
  const milhar = /^\d{1,3}(\.\d{3})+$/;
  const comVirgula = /^([\d.]+),(\d{1,2})$/.exec(limpo);
  const comPonto = /^(\d+)\.(\d{1,2})$/.exec(limpo);
  if (limpo.includes(",")) {
    const parteInteira = comVirgula?.[1] ?? "";
    if (!/^\d+$/.test(parteInteira) && !milhar.test(parteInteira)) return null;
    inteiro = parteInteira.replace(/\./g, "");
    decimal = comVirgula?.[2] ?? "";
  } else if (comPonto) {
    inteiro = comPonto[1] ?? "";
    decimal = comPonto[2] ?? "";
  } else if (milhar.test(limpo)) {
    inteiro = limpo.replace(/\./g, "");
  } else if (/^\d+$/.test(limpo)) {
    inteiro = limpo;
  } else {
    return null;
  }

  const centavos = Number(inteiro) * 100 + Number(decimal.padEnd(2, "0"));
  return Number.isSafeInteger(centavos) ? centavos : null;
}

/** Centavos para o texto do campo ("1234,56"), sem símbolo nem milhar, para editar. */
export function textoDeCentavos(centavos: number): string {
  const abs = Math.abs(centavos);
  return `${Math.trunc(abs / 100)},${String(abs % 100).padStart(2, "0")}`;
}
