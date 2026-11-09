import { formatarReais } from "./dinheiro";

export type PreviaDaConciliacao = { resultado: "LANCADO" | "CONFERE" | "MENOR"; diferenca: number; mensagem: string };

/** O que o servidor vai fazer com o saldo informado; só informa, quem decide é a API. */
export function previaDaConciliacao(informado: number, exibido: number): PreviaDaConciliacao {
  const diferenca = informado - exibido;
  if (diferenca > 0) {
    return { resultado: "LANCADO", diferenca, mensagem: `Diferença: ${formatarReais(diferenca)}, lançada como Rendimentos hoje` };
  }
  if (diferenca === 0) return { resultado: "CONFERE", diferenca, mensagem: "O saldo já confere; nada a lançar." };
  return {
    resultado: "MENOR",
    diferenca,
    mensagem: "O saldo no banco é menor que o do sistema: pode haver um resgate ou uma transferência ainda não lançados.",
  };
}
