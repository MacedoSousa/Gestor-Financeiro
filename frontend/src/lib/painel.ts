import { formatarReais } from "./dinheiro";
import { mesCurto } from "./mes";

/** Antes de 12h bom dia, de 12h às 17h59 boa tarde, a partir de 18h boa noite. */
export function saudacao(hora: number): "Bom dia" | "Boa tarde" | "Boa noite" {
  if (hora < 12) return "Bom dia";
  if (hora < 18) return "Boa tarde";
  return "Boa noite";
}

type Ponto = { rotulo: string; mes: string; totalCentavos?: number | string | null };

/** Texto do gráfico para leitor de tela: primeiro e último mês com dado. Nulo se não há nenhum. */
export function resumoDoGrafico(pontos: Ponto[]): string | null {
  const comDado = pontos.filter((p) => p.totalCentavos != null);
  const primeiro = comDado[0];
  const ultimo = comDado[comDado.length - 1];
  if (!primeiro || !ultimo) return null;
  const parte = (p: Ponto) => `${formatarReais(Number(p.totalCentavos))} em ${mesCurto(p.mes)}`;
  return `de ${parte(primeiro)} a ${parte(ultimo)}`;
}

export function aparenciaDaFatura(estado: string): { rotulo: string; tom: "alerta" | "normal" } {
  if (estado === "COM_ATRASO") return { rotulo: "Em atraso", tom: "alerta" };
  if (estado === "ABERTA") return { rotulo: "Aberta, ainda recebe compras", tom: "normal" };
  return { rotulo: "Fechada", tom: "normal" };
}

export function aparenciaDaMeta(situacao: string, chegaEm?: string | null): { texto: string; tom: "ok" | "normal" | "alerta" } {
  if (situacao === "ALCANCADA") return { texto: "Alcançada", tom: "ok" };
  if (situacao === "NAO_CHEGA") return { texto: "Não chega em 100 anos com o aporte planejado", tom: "alerta" };
  return { texto: chegaEm ? `previsão ${mesCurto(chegaEm)}` : "sem previsão", tom: "normal" };
}
