import { describe, expect, it } from "vitest";
import {
  apelidoSugerido,
  contasCompativeis,
  linhasDeAplicacao,
  aplicacoesResgatadas,
  linhasIniciais,
  montarPedido,
  resumoDaDescoberta,
  seloDaConta,
  trocarTitular,
  validarConfirmacao,
  type LinhaDeConta,
} from "./nascimento";

const ANA = "11111111-1111-1111-1111-111111111111";
const BRUNO = "22222222-2222-2222-2222-222222222222";
const nomes = { [ANA]: "Ana", [BRUNO]: "Bruno" };

/** Os dois primeiros; falha o teste se faltar algum. */
function duas<T>(xs: readonly T[]): [T, T] {
  const [a, b] = xs;
  if (a === undefined || b === undefined) throw new Error("faltam elementos");
  return [a, b];
}

/** O primeiro elemento; falha o teste se a lista vier vazia. */
function primeira<T>(xs: readonly T[]): T {
  const x = xs[0];
  if (x === undefined) throw new Error("lista vazia");
  return x;
}

const encontrada = (idExterno: string, tipo: string, extra: Partial<Record<string, unknown>> = {}) => ({
  idExterno,
  tipo,
  nomeNoBanco: "Conta",
  apelidoSugerido: apelidoSugerido("Itaú", tipo, "Ana"),
  titularSugeridoUsuarioId: ANA,
  diaFechamentoSugerido: null,
  diaVencimentoSugerido: null,
  limiteCentavos: null,
  jaVemDe: null,
  ...extra,
});

const descoberta = (contas: ReturnType<typeof encontrada>[], aplicacoes: { idExterno: string; nome: string; tipo: string; saldoCentavos: number | null; situacao?: string }[] = []) => ({
  banco: "Itaú",
  contas,
  aplicacoes: aplicacoes.map((a) => ({ situacao: "ATIVA", ...a })),
  naoImportadas: [],
});

describe("resumoDaDescoberta", () => {
  it("conta contas, cartões e investimentos no singular", () => {
    const d = descoberta([encontrada("c", "CORRENTE"), encontrada("k", "CARTAO")], [{ idExterno: "a", nome: "CDB", tipo: "CDB", saldoCentavos: 1 }]);
    expect(resumoDaDescoberta(d)).toBe("Encontramos 1 conta, 1 cartão e 1 investimento no Itaú. Criar?");
  });

  it("usa o plural e omite o que não veio", () => {
    const d = descoberta([encontrada("c", "CORRENTE"), encontrada("p", "POUPANCA"), encontrada("k1", "CARTAO"), encontrada("k2", "CARTAO")]);
    expect(resumoDaDescoberta(d)).toBe("Encontramos 2 contas e 2 cartões no Itaú. Criar?");
  });

  it("não conta a que já vem da outra conexão", () => {
    const d = descoberta([encontrada("c", "CORRENTE", { jaVemDe: "Bruno" }), encontrada("k", "CARTAO")]);
    expect(resumoDaDescoberta(d)).toBe("Encontramos 1 cartão no Itaú. Criar?");
  });

  it("sem nada novo diz que está tudo no sistema", () => {
    expect(resumoDaDescoberta(descoberta([]))).toBe("Nada novo no Itaú: o que o banco mostra já está no sistema.");
  });
});

describe("apelidoSugerido", () => {
  it("tem banco, tipo e primeiro nome", () => {
    expect(apelidoSugerido("Itaú", "CORRENTE", "Ana")).toBe("Itaú Corrente · Ana");
    expect(apelidoSugerido("Itaú", "POUPANCA", "Ana")).toBe("Itaú Poupança · Ana");
    expect(apelidoSugerido("Itaú", "CARTAO", "Bruno")).toBe("Itaú Cartão · Bruno");
  });

  it("sem titular é conjunta", () => {
    expect(apelidoSugerido("Itaú", "CORRENTE", null)).toBe("Itaú Corrente · Conjunta");
  });

  it("corta o banco para caber em 60 caracteres", () => {
    const a = apelidoSugerido("B".repeat(60), "CORRENTE", "Ana");
    expect(a).toHaveLength(60);
    expect(a.endsWith(" Corrente · Ana")).toBe(true);
  });
});

describe("linhasIniciais", () => {
  it("vem com a sugestão do banco e cria por padrão", () => {
    const linha = primeira(linhasIniciais(descoberta([encontrada("k", "CARTAO", { diaFechamentoSugerido: 3, diaVencimentoSugerido: 10 })])));
    expect(linha).toMatchObject({ idExterno: "k", acao: "CRIAR", apelido: "Itaú Cartão · Ana", titular: ANA, diaFechamento: "3", diaVencimento: "10" });
  });

  it("a que já vem da outra conexão fica de fora", () => {
    expect(linhasIniciais(descoberta([encontrada("c", "CORRENTE", { jaVemDe: "Bruno" })]))).toEqual([]);
  });
});

describe("trocarTitular", () => {
  const base = primeira(linhasIniciais(descoberta([encontrada("c", "CORRENTE")])));

  it("troca o apelido sugerido junto, inclusive para conjunta", () => {
    expect(trocarTitular(base, "", "Itaú", nomes).apelido).toBe("Itaú Corrente · Conjunta");
    expect(trocarTitular(base, BRUNO, "Itaú", nomes).apelido).toBe("Itaú Corrente · Bruno");
  });

  it("não mexe num apelido que a pessoa escreveu", () => {
    const editada: LinhaDeConta = { ...base, apelido: "Conta da casa" };
    expect(trocarTitular(editada, "", "Itaú", nomes)).toMatchObject({ titular: "", apelido: "Conta da casa" });
  });
});

describe("validarConfirmacao", () => {
  const corrente = primeira(linhasIniciais(descoberta([encontrada("c", "CORRENTE")])));
  const cartao = primeira(linhasIniciais(descoberta([encontrada("k", "CARTAO")])));

  it("criar pede apelido de 1 a 60", () => {
    expect(validarConfirmacao([{ ...corrente, apelido: " " }], [])).toEqual({ c: { campo: "apelido", mensagem: "Dê um apelido à conta." } });
    expect(validarConfirmacao([{ ...corrente, apelido: "x".repeat(61) }], [])).toEqual({ c: { campo: "apelido", mensagem: "O apelido tem no máximo 60 caracteres." } });
  });

  it("cartão novo pede os dois dias", () => {
    expect(validarConfirmacao([{ ...cartao, diaFechamento: "3", diaVencimento: "" }], [])).toEqual({
      k: { campo: "diaVencimento", mensagem: "Informe os dias de fechamento e de vencimento (1 a 31)." },
    });
    expect(validarConfirmacao([{ ...cartao, diaFechamento: "0", diaVencimento: "32" }], [])).toEqual({
      k: { campo: "diaFechamento", mensagem: "Informe os dias de fechamento e de vencimento (1 a 31)." },
    });
    expect(validarConfirmacao([{ ...cartao, diaFechamento: "3", diaVencimento: "10" }], [])).toEqual({});
  });

  it("ligar pede a conta que recebe", () => {
    expect(validarConfirmacao([{ ...corrente, acao: "LIGAR", contaExistenteId: "" }], [])).toEqual({
      c: { campo: "contaExistenteId", mensagem: "Escolha a conta que vai receber esta." },
    });
  });

  it("não importar não pede nada", () => {
    expect(validarConfirmacao([{ ...cartao, acao: "NAO_IMPORTAR", apelido: "", diaFechamento: "" }], [])).toEqual({});
  });

  it("dois apelidos iguais na mesma confirmação", () => {
    const outra = { ...corrente, idExterno: "c2", apelido: " itaú corrente · ana " };
    expect(validarConfirmacao([corrente, outra], [])).toEqual({ c2: { campo: "apelido", mensagem: "Outra conta desta lista já tem esse apelido." } });
  });

  it("grupo com nome longo", () => {
    const ap = primeira(linhasDeAplicacao(descoberta([], [{ idExterno: "a", nome: "CDB", tipo: "CDB", saldoCentavos: 1 }])));
    expect(validarConfirmacao([], [{ ...ap, grupo: "g".repeat(61) }])).toEqual({ a: { campo: "grupo", mensagem: "O nome do grupo tem no máximo 60 caracteres." } });
  });
});

describe("linhasDeAplicacao e montarPedido", () => {
  it("as aplicações começam num grupo só, com o nome do banco", () => {
    const linhas = linhasDeAplicacao(descoberta([], [{ idExterno: "a", nome: "CDB", tipo: "CDB", saldoCentavos: 1 }]));
    expect(primeira(linhas)).toMatchObject({ idExterno: "a", grupo: "Investimentos Itaú" });
  });

  it("resgatada não vira linha de grupo; fica na lista das resgatadas (D22)", () => {
    const d = descoberta([], [
      { idExterno: "a1", nome: "CDB", tipo: "CDB", saldoCentavos: 1, situacao: "ATIVA" },
      { idExterno: "a2", nome: "CDB antigo", tipo: "CDB", saldoCentavos: 300_000, situacao: "RESGATADA" },
      { idExterno: "a3", nome: "CDB novo", tipo: "CDB", saldoCentavos: 1, situacao: "PENDENTE" },
    ]);
    expect(linhasDeAplicacao(d).map((l) => [l.idExterno, l.situacao])).toEqual([
      ["a1", "ATIVA"],
      ["a3", "PENDENTE"],
    ]);
    expect(aplicacoesResgatadas(d)).toEqual([{ idExterno: "a2", nome: "CDB antigo", tipo: "CDB" }]);
  });

  it("aplicação sem valor no banco continua sem valor, não vira R$ 0,00 (D21)", () => {
    const linhas = linhasDeAplicacao(descoberta([], [{ idExterno: "a", nome: "Fundo", tipo: "MUTUAL_FUND", saldoCentavos: null }]));
    expect(primeira(linhas).saldoCentavos).toBeNull();
  });

  it("monta o corpo da API: titular vazio é conjunta, dias só no cartão, grupo vazio aguarda e a meta vem do grupo", () => {
    const [corrente, cartao] = duas(linhasIniciais(descoberta([encontrada("c", "CORRENTE"), encontrada("k", "CARTAO")])));
    const aplicacoes = linhasDeAplicacao(
      descoberta([], [
        { idExterno: "a1", nome: "CDB 1", tipo: "CDB", saldoCentavos: 1 },
        { idExterno: "a2", nome: "CDB 2", tipo: "CDB", saldoCentavos: 1 },
        { idExterno: "a3", nome: "CDB 3", tipo: "CDB", saldoCentavos: 1 },
      ]),
    );
    const pedido = montarPedido(
      [{ ...corrente, titular: "" }, { ...cartao, diaFechamento: "3", diaVencimento: "10" }],
      aplicacoes.map((a, i) => ({ ...a, grupo: ["CDBs Casa", " cdbs casa ", ""][i] ?? "" })),
      { "cdbs casa": "meta-1" },
    );

    expect(pedido.contas).toEqual([
      { idExterno: "c", acao: "CRIAR", apelido: "Itaú Corrente · Ana", titularUsuarioId: null, diaFechamento: null, diaVencimento: null, contaExistenteId: null },
      { idExterno: "k", acao: "CRIAR", apelido: "Itaú Cartão · Ana", titularUsuarioId: ANA, diaFechamento: 3, diaVencimento: 10, contaExistenteId: null },
    ]);
    expect(pedido.aplicacoes).toEqual([
      { idExterno: "a1", grupo: { nome: "CDBs Casa", metaId: "meta-1" } },
      { idExterno: "a2", grupo: { nome: "cdbs casa", metaId: "meta-1" } },
      { idExterno: "a3", grupo: null },
    ]);
  });

  it("ligar manda só a conta existente; não importar, só a ação", () => {
    const [corrente, cartao] = duas(linhasIniciais(descoberta([encontrada("c", "CORRENTE"), encontrada("k", "CARTAO")])));
    const pedido = montarPedido([{ ...corrente, acao: "LIGAR", contaExistenteId: "x" }, { ...cartao, acao: "NAO_IMPORTAR" }], [], {});
    const nada = { apelido: null, titularUsuarioId: null, diaFechamento: null, diaVencimento: null };
    expect(pedido.contas).toEqual([
      { ...nada, idExterno: "c", acao: "LIGAR", contaExistenteId: "x" },
      { ...nada, idExterno: "k", acao: "NAO_IMPORTAR", contaExistenteId: null },
    ]);
  });
});

describe("contasCompativeis", () => {
  const contas = [
    { id: "1", apelido: "Corrente", tipo: "CORRENTE", ligada: false, arquivada: false },
    { id: "2", apelido: "Poupança", tipo: "POUPANCA", ligada: false, arquivada: false },
    { id: "3", apelido: "Cartão", tipo: "CARTAO", ligada: false, arquivada: false },
    { id: "4", apelido: "Já do banco", tipo: "CORRENTE", ligada: true, arquivada: false },
    { id: "5", apelido: "Dinheiro", tipo: "DINHEIRO", ligada: false, arquivada: false },
  ];

  it("conta recebe corrente ou poupança; cartão recebe cartão; nunca uma já ligada", () => {
    expect(contasCompativeis("CORRENTE", contas).map((c) => c.id)).toEqual(["1", "2"]);
    expect(contasCompativeis("POUPANCA", contas).map((c) => c.id)).toEqual(["1", "2"]);
    expect(contasCompativeis("CARTAO", contas).map((c) => c.id)).toEqual(["3"]);
  });
});

describe("seloDaConta", () => {
  it("conta ligada mostra o banco e sincronizado", () => {
    expect(seloDaConta({ ligada: true, instituicao: "Itaú" })).toBe("Itaú · sincronizado");
    expect(seloDaConta({ ligada: true, instituicao: null })).toBe("Banco · sincronizado");
    expect(seloDaConta({ ligada: false, instituicao: "Itaú" })).toBeNull();
  });
});
