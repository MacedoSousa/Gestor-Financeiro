/** Lógica de exibição da tela "Revisar importados" (Fase 2, Tarefa 5.2). Nenhum valor é calculado aqui: vêm da API. */

type CategoriaBasica = { id: string; nome: string; tipo: string; arquivada: boolean };
type GrupoBasico = {
  natureza: string;
  quantidade: number | string;
  categoriaSugeridaId?: string | null;
  categoriaDoBanco?: string | null;
  nomeDaCategoriaDoBanco?: string | null;
};

export type Aplicar = "SO_ESTA" | "SEMPRE" | "SEMPRE_DO_BANCO";

/** Categorias que servem ao grupo: do mesmo tipo da natureza e não arquivadas. */
export function categoriasDoGrupo<C extends CategoriaBasica>(natureza: string, categorias: readonly C[]): C[] {
  return categorias.filter((c) => !c.arquivada && c.tipo === natureza);
}

/** A sugestão da regra ou do mapa, se ainda serve; senão nada marcado (o casal escolhe, D15). */
export function categoriaInicial(grupo: GrupoBasico, categorias: readonly CategoriaBasica[]): string {
  const sugerida = grupo.categoriaSugeridaId;
  return sugerida && categoriasDoGrupo(grupo.natureza, categorias).some((c) => c.id === sugerida) ? sugerida : "";
}

/** "Só estes", "Sempre que vier assim" e, quando o banco mandou a categoria, "Sempre que o banco disser “…”" (decisão 2). */
export function opcoesDeAplicar(grupo: GrupoBasico): { valor: Aplicar; rotulo: string }[] {
  const opcoes: { valor: Aplicar; rotulo: string }[] = [
    { valor: "SO_ESTA", rotulo: "Só estes" },
    { valor: "SEMPRE", rotulo: "Sempre que vier assim" },
  ];
  if (grupo.categoriaDoBanco)
    opcoes.push({ valor: "SEMPRE_DO_BANCO", rotulo: `Sempre que o banco disser “${grupo.nomeDaCategoriaDoBanco ?? grupo.categoriaDoBanco}”` });
  return opcoes;
}

export function resumoDoGrupo(grupo: GrupoBasico): string {
  const n = Number(grupo.quantidade);
  return `${n} ${n === 1 ? "lançamento" : "lançamentos"} · ${grupo.natureza === "RECEITA" ? "receita" : "despesa"}`;
}

export function textoDaRevisao(n: number): string {
  return n === 1 ? "1 lançamento revisado. Ele já conta no orçamento." : `${n} lançamentos revisados. Eles já contam no orçamento.`;
}
