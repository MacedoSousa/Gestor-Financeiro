/** "2026-11" ± meses. */
export function somarMeses(mes: string, delta: number): string {
  const [ano, m] = mes.split("-").map(Number);
  const d = new Date(ano ?? 2000, (m ?? 1) - 1 + delta, 1);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}`;
}

export function nomeDoMes(mes: string): string {
  const [ano, m] = mes.split("-").map(Number);
  const texto = new Date(ano ?? 2000, (m ?? 1) - 1, 1).toLocaleDateString("pt-BR", { month: "long", year: "numeric" });
  return texto.charAt(0).toUpperCase() + texto.slice(1);
}

/** Só o nome do mês, sem o ano ("outubro"). */
export function soNomeDoMes(mes: string): string {
  const [ano, m] = mes.split("-").map(Number);
  return new Date(ano ?? 2000, (m ?? 1) - 1, 1).toLocaleDateString("pt-BR", { month: "long" });
}
