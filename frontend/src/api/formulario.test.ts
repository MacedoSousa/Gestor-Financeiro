import { describe, expect, it } from "vitest";
import { ErroDeApi } from "./autenticacao";
import { chamarFormulario } from "./formulario";

const resposta = (status: number, corpo: unknown) => () => Promise.resolve({ error: corpo, response: new Response(null, { status }) });

async function mensagem(status: number, corpo: unknown): Promise<string> {
  try {
    await chamarFormulario(resposta(status, corpo), "genérica");
    return "sem erro";
  } catch (e) {
    return e instanceof ErroDeApi ? e.message : "outro erro";
  }
}

describe("títulos da API que vão para a tela", () => {
  it("os fixos da etapa 3 passam", async () => {
    expect(await mensagem(409, { title: "Arquive antes as contas que vêm deste banco." })).toBe("Arquive antes as contas que vêm deste banco.");
    expect(await mensagem(409, { title: "Esta conta vem do banco. O saldo inicial é calculado." })).toBe(
      "Esta conta vem do banco. O saldo inicial é calculado.",
    );
    expect(await mensagem(409, { title: "A conta escolhida já não existe. Atualize a página." })).toBe(
      "A conta escolhida já não existe. Atualize a página.",
    );
    expect(await mensagem(409, { title: "Esta aplicação já está em outro grupo." })).toBe("Esta aplicação já está em outro grupo.");
  });

  it("os do banco escolhido pelo código (D18) passam", async () => {
    expect(await mensagem(409, { title: "Escolha o banco desta conexão antes de ver as contas." })).toBe(
      "Escolha o banco desta conexão antes de ver as contas.",
    );
    expect(await mensagem(409, { title: "O banco desta conexão já foi escolhido." })).toBe("O banco desta conexão já foi escolhido.");
  });

  it("os com nome passam só no formato exato", async () => {
    expect(await mensagem(409, { title: "Esta conta já vem da conexão de Ana." })).toBe("Esta conta já vem da conexão de Ana.");
    expect(await mensagem(409, { title: "Não deu para ligar o grupo CDBs Casa à meta escolhida." })).toBe(
      "Não deu para ligar o grupo CDBs Casa à meta escolhida.",
    );
    expect(await mensagem(409, { title: "Esta conta já vem da conexão de Ana. E outra coisa." })).toBe("genérica");
  });

  it("título desconhecido vira a genérica", async () => {
    expect(await mensagem(500, { title: "System.InvalidOperationException: detalhe" })).toBe("genérica");
  });
});
