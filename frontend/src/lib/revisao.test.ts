import { describe, expect, it } from "vitest";
import { categoriaInicial, categoriasDoGrupo, opcoesDeAplicar, resumoDoGrupo, textoDaRevisao } from "./revisao";

const categoria = (id: string, tipo: "DESPESA" | "RECEITA", arquivada = false) => ({ id, nome: id, tipo, arquivada, papel: null });
const categorias = [categoria("mercado", "DESPESA"), categoria("velha", "DESPESA", true), categoria("salario", "RECEITA")];

const grupo = (o: Partial<{ natureza: string; quantidade: number; totalCentavos: number; categoriaSugeridaId: string | null; categoriaDoBanco: string | null; nomeDaCategoriaDoBanco: string | null }> = {}) => ({
  descricao: "PADARIA DO ZE",
  natureza: "DESPESA",
  quantidade: 2,
  totalCentavos: -3_500,
  categoriaSugeridaId: null,
  categoriaDoBanco: "01010000",
  nomeDaCategoriaDoBanco: "Padaria",
  ids: ["a", "b"],
  ...o,
});

describe("revisão em lote (Tarefa 5.2)", () => {
  it("só oferece categorias não arquivadas do tipo do grupo", () => {
    expect(categoriasDoGrupo("DESPESA", categorias).map((c) => c.id)).toEqual(["mercado"]);
    expect(categoriasDoGrupo("RECEITA", categorias).map((c) => c.id)).toEqual(["salario"]);
  });

  it("começa na sugestão da regra ou do mapa, se ela ainda serve; senão, nada marcado (D15)", () => {
    expect(categoriaInicial(grupo({ categoriaSugeridaId: "mercado" }), categorias)).toBe("mercado");
    expect(categoriaInicial(grupo({ categoriaSugeridaId: "velha" }), categorias)).toBe("");
    expect(categoriaInicial(grupo({ categoriaSugeridaId: "salario" }), categorias)).toBe("");
    expect(categoriaInicial(grupo(), categorias)).toBe("");
  });

  it("o botão do banco só aparece com a categoria do banco, com o nome dela", () => {
    expect(opcoesDeAplicar(grupo()).map((o) => o.rotulo)).toEqual([
      "Só estes",
      "Sempre que vier assim",
      "Sempre que o banco disser “Padaria”",
    ]);
    expect(opcoesDeAplicar(grupo({ categoriaDoBanco: null, nomeDaCategoriaDoBanco: null })).map((o) => o.valor)).toEqual(["SO_ESTA", "SEMPRE"]);
    expect(opcoesDeAplicar(grupo({ nomeDaCategoriaDoBanco: null })).at(-1)?.rotulo).toBe("Sempre que o banco disser “01010000”");
  });

  it("resumo e confirmação no singular e no plural", () => {
    expect(resumoDoGrupo(grupo())).toBe("2 lançamentos · despesa");
    expect(resumoDoGrupo(grupo({ quantidade: 1, natureza: "RECEITA" }))).toBe("1 lançamento · receita");
    expect(textoDaRevisao(1)).toBe("1 lançamento revisado. Ele já conta no orçamento.");
    expect(textoDaRevisao(3)).toBe("3 lançamentos revisados. Eles já contam no orçamento.");
  });
});
