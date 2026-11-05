/** Máscara 000.000.000-00 aplicada só enquanto digita. */
export function mascararCpf(valor: string) {
  const d = valor.replace(/\D/g, "").slice(0, 11);
  const partes = [d.slice(0, 3), d.slice(3, 6), d.slice(6, 9)].filter(Boolean).join(".");
  return d.length > 9 ? `${partes}-${d.slice(9)}` : partes;
}
