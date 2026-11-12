/** Regras de exibição da confirmação das contas que vêm do banco (nascimento, Fase 2). Nada aqui calcula dinheiro. */
import type { components } from "../api/schema";

type Descoberta = components["schemas"]["DescobertaDto"];
type Pedido = components["schemas"]["NascimentoDto"];
type ContaConfirmada = components["schemas"]["ContaConfirmadaDto"];
type AplicacaoConfirmada = components["schemas"]["AplicacaoConfirmadaDto"];

export type Acao = "CRIAR" | "LIGAR" | "NAO_IMPORTAR";

/** Uma conta ou cartão encontrado, como a pessoa está deixando. `titular` vazio = conjunta; dias em texto do campo. */
export type LinhaDeConta = {
  idExterno: string;
  tipo: string;
  nomeNoBanco: string;
  acao: Acao;
  apelido: string;
  titular: string;
  contaExistenteId: string;
  diaFechamento: string;
  diaVencimento: string;
  limiteCentavos: number | null;
};

/** Uma aplicação; `grupo` vazio = fica aguardando grupo (não vira conta agora). */
export type LinhaDeAplicacao = { idExterno: string; nome: string; tipo: string; saldoCentavos: number; grupo: string };

export const APELIDO_MAXIMO = 60;

const NOME_DO_TIPO: Readonly<Record<string, string>> = { CORRENTE: "Corrente", POUPANCA: "Poupança", CARTAO: "Cartão" };

/** Igual à API (SugestaoDeApelido.Para): "<Banco> <Tipo> · <primeiro nome>", ou "· Conjunta"; o banco é cortado para caber em 60. */
export function apelidoSugerido(banco: string, tipo: string, primeiroNome: string | null): string {
  const fim = ` ${NOME_DO_TIPO[tipo] ?? tipo} · ${primeiroNome ?? "Conjunta"}`;
  const inicio = banco.trim();
  return inicio.slice(0, Math.max(0, APELIDO_MAXIMO - fim.length)).trimEnd() + fim;
}

const plural = (n: number, um: string, varios: string) => `${n} ${n === 1 ? um : varios}`;

function juntar(partes: string[]): string {
  return partes.length <= 1 ? (partes[0] ?? "") : `${partes.slice(0, -1).join(", ")} e ${partes[partes.length - 1]}`;
}

/** "Encontramos 1 conta, 1 cartão e 1 investimento no Itaú. Criar?" (spec, seção 1). A que já vem da outra conexão não conta. */
export function resumoDaDescoberta(d: Pick<Descoberta, "banco" | "contas" | "aplicacoes">): string {
  const novas = d.contas.filter((c) => !c.jaVemDe);
  const cartoes = novas.filter((c) => c.tipo === "CARTAO").length;
  const contas = novas.length - cartoes;
  const partes = [
    contas > 0 ? plural(contas, "conta", "contas") : null,
    cartoes > 0 ? plural(cartoes, "cartão", "cartões") : null,
    d.aplicacoes.length > 0 ? plural(d.aplicacoes.length, "investimento", "investimentos") : null,
  ].filter((p): p is string => p !== null);
  return partes.length === 0
    ? `Nada novo no ${d.banco}: o que o banco mostra já está no sistema.`
    : `Encontramos ${juntar(partes)} no ${d.banco}. Criar?`;
}

/** As contas a confirmar, com o que o banco sugere; a que já vem da outra conexão fica de fora (a trava a ignora). */
export function linhasIniciais(d: Pick<Descoberta, "contas">): LinhaDeConta[] {
  return d.contas
    .filter((c) => !c.jaVemDe)
    .map((c) => ({
      idExterno: c.idExterno,
      tipo: c.tipo,
      nomeNoBanco: c.nomeNoBanco,
      acao: "CRIAR",
      apelido: c.apelidoSugerido,
      titular: c.titularSugeridoUsuarioId,
      contaExistenteId: "",
      diaFechamento: c.diaFechamentoSugerido == null ? "" : String(c.diaFechamentoSugerido),
      diaVencimento: c.diaVencimentoSugerido == null ? "" : String(c.diaVencimentoSugerido),
      limiteCentavos: c.limiteCentavos == null ? null : Number(c.limiteCentavos),
    }));
}

/** As aplicações começam todas num grupo com o nome do banco; a pessoa separa por meta ("CDBs Casa", "CDBs Carro"). */
export function linhasDeAplicacao(d: Pick<Descoberta, "banco" | "aplicacoes">): LinhaDeAplicacao[] {
  return d.aplicacoes.map((a) => ({
    idExterno: a.idExterno,
    nome: a.nome,
    tipo: a.tipo,
    saldoCentavos: Number(a.saldoCentavos),
    grupo: `Investimentos ${d.banco}`.slice(0, APELIDO_MAXIMO),
  }));
}

/** Troca o titular; se o apelido ainda é o sugerido (para o titular antigo), troca junto. */
export function trocarTitular(linha: LinhaDeConta, titular: string, banco: string, nomes: Readonly<Record<string, string>>): LinhaDeConta {
  const sugeridoAntes = apelidoSugerido(banco, linha.tipo, linha.titular ? (nomes[linha.titular] ?? null) : null);
  const apelido = linha.apelido === sugeridoAntes ? apelidoSugerido(banco, linha.tipo, titular ? (nomes[titular] ?? null) : null) : linha.apelido;
  return { ...linha, titular, apelido };
}

const diaValido = (texto: string) => /^\d{1,2}$/.test(texto.trim()) && Number(texto) >= 1 && Number(texto) <= 31;
/** O nome do grupo (e o apelido) não diferencia caixa nem espaços em volta, como na API. */
const chaveDoGrupo = (nome: string) => nome.trim().toLocaleLowerCase("pt-BR");

/** Erro de uma linha: o campo que recebe a marca de inválido e o foco, e a mensagem. */
export type ErroDaLinha = {
  campo: "apelido" | "diaFechamento" | "diaVencimento" | "contaExistenteId" | "grupo";
  mensagem: string;
};

/** Mesmas regras da API, para avisar antes de enviar. Chave = idExterno; valor = o primeiro erro daquela linha. */
export function validarConfirmacao(contas: readonly LinhaDeConta[], aplicacoes: readonly LinhaDeAplicacao[]): Record<string, ErroDaLinha> {
  const erros: Record<string, ErroDaLinha> = {};
  const apelidos = new Set<string>();
  for (const c of contas) {
    if (c.acao === "CRIAR") {
      const apelido = c.apelido.trim();
      if (apelido.length === 0) erros[c.idExterno] = { campo: "apelido", mensagem: "Dê um apelido à conta." };
      else if (apelido.length > APELIDO_MAXIMO)
        erros[c.idExterno] = { campo: "apelido", mensagem: `O apelido tem no máximo ${APELIDO_MAXIMO} caracteres.` };
      else if (c.tipo === "CARTAO" && !(diaValido(c.diaFechamento) && diaValido(c.diaVencimento)))
        erros[c.idExterno] = {
          campo: diaValido(c.diaFechamento) ? "diaVencimento" : "diaFechamento",
          mensagem: "Informe os dias de fechamento e de vencimento (1 a 31).",
        };
      else if (apelidos.has(chaveDoGrupo(apelido))) erros[c.idExterno] = { campo: "apelido", mensagem: "Outra conta desta lista já tem esse apelido." };
      apelidos.add(chaveDoGrupo(apelido));
    } else if (c.acao === "LIGAR" && !c.contaExistenteId) {
      erros[c.idExterno] = { campo: "contaExistenteId", mensagem: "Escolha a conta que vai receber esta." };
    }
  }
  for (const a of aplicacoes)
    if (a.grupo.trim().length > APELIDO_MAXIMO)
      erros[a.idExterno] = { campo: "grupo", mensagem: `O nome do grupo tem no máximo ${APELIDO_MAXIMO} caracteres.` };
  return erros;
}

/** Os grupos distintos (sem caixa), na ordem em que aparecem, com o nome como foi escrito da primeira vez. */
export function gruposDistintos(aplicacoes: readonly LinhaDeAplicacao[]): { chave: string; nome: string }[] {
  const vistos = new Map<string, string>();
  for (const a of aplicacoes) {
    const nome = a.grupo.trim();
    if (nome && !vistos.has(chaveDoGrupo(nome))) vistos.set(chaveDoGrupo(nome), nome);
  }
  return [...vistos].map(([chave, nome]) => ({ chave, nome }));
}

/** O corpo do POST. `metaPorGrupo` é indexado pela chave de `gruposDistintos`. */
export function montarPedido(contas: readonly LinhaDeConta[], aplicacoes: readonly LinhaDeAplicacao[], metaPorGrupo: Readonly<Record<string, string>>): Pedido {
  const meta = (nome: string) => metaPorGrupo[chaveDoGrupo(nome)] || null;
  return {
    contas: contas.map((c): ContaConfirmada => {
      const nada = { contaExistenteId: null, apelido: null, titularUsuarioId: null, diaFechamento: null, diaVencimento: null };
      if (c.acao === "NAO_IMPORTAR") return { ...nada, idExterno: c.idExterno, acao: c.acao };
      if (c.acao === "LIGAR") return { ...nada, idExterno: c.idExterno, acao: c.acao, contaExistenteId: c.contaExistenteId };
      const cartao = c.tipo === "CARTAO";
      return {
        idExterno: c.idExterno,
        acao: c.acao,
        apelido: c.apelido.trim(),
        titularUsuarioId: c.titular || null,
        diaFechamento: cartao ? Number(c.diaFechamento) : null,
        diaVencimento: cartao ? Number(c.diaVencimento) : null,
        contaExistenteId: null,
      };
    }),
    aplicacoes: aplicacoes.map((a): AplicacaoConfirmada => {
      const nome = a.grupo.trim();
      return { idExterno: a.idExterno, grupo: nome ? { nome, metaId: meta(nome) } : null };
    }),
  };
}

type ContaDoSistema = { id: string; apelido: string; tipo: string; ligada: boolean; arquivada: boolean };

/** Quem pode receber a conta do banco (LIGAR): conta recebe corrente ou poupança, cartão recebe cartão; nunca uma já ligada. */
export function contasCompativeis<T extends ContaDoSistema>(tipo: string, contas: readonly T[]): T[] {
  const aceitos = tipo === "CARTAO" ? ["CARTAO"] : ["CORRENTE", "POUPANCA"];
  return contas.filter((c) => aceitos.includes(c.tipo) && !c.ligada);
}

/** Selo da conta ligada: "Itaú · sincronizado" (spec, seção 1). */
export function seloDaConta(c: { ligada: boolean; instituicao?: string | null }): string | null {
  return c.ligada ? `${c.instituicao || "Banco"} · sincronizado` : null;
}
