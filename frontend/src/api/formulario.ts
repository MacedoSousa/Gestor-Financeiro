import { ErroDeApi } from "./autenticacao";

const SEM_CONEXAO = "Sem conexão com o servidor. Tente de novo.";

/**
 * Títulos fixos que Contas, Lancamentos, Investimentos e Integracoes escrevem nos Problem Details. Só estes vão para a tela; qualquer outro
 * título cai na mensagem genérica.
 */
const TITULOS: ReadonlySet<string> = new Set([
  "Já existe uma conta com esse nome.",
  "Conta com lançamentos: arquive em vez de apagar.",
  "Conta arquivada: escolha outra.",
  "Categoria arquivada: escolha outra.",
  "Categoria com lançamentos: arquive em vez de apagar.",
  "Categoria com papel não pode ser apagada: arquive ou renomeie.",
  "Chave de idempotência já usada.",
  "Fatura fechada: só descrição e categoria podem mudar.",
  "Esta conta já pertence a outra meta.",
  "Já existe uma meta com esse nome.",
  "Só contas de poupança ou investimento entram numa meta.",
  "Só contas de poupança ou investimento são conciliadas aqui.",
  "O saldo mudou desde que a tela foi aberta. Confira de novo.",
  // Integracoes (conexões do Meu Pluggy)
  "As chaves deste slot não foram gravadas no servidor.",
  "Este slot já é usado no casal.",
  "Você já tem uma conexão.",
  "Este item já está cadastrado.",
  "Só o titular pode alterar esta conexão.",
  "A Pluggy não respondeu. Tente de novo em alguns minutos.",
  "O banco recusou este item com as chaves do slot 1.",
  "O banco recusou este item com as chaves do slot 2.",
  // Integracoes e Contas (nascimento das contas ligadas, etapa 3)
  "Arquive antes as contas que vêm deste banco.",
  "Esta conta vem do banco. O saldo inicial é calculado.",
  "Esta conta vem do banco. Arquive para parar a sincronização.",
  "Esta conta já recebe outra conta do banco.",
  "Esta conta do banco já está ligada a outra conta.",
  "Esta conta já vem de outra conexão.",
  "Esta conta não está marcada como \"não importar\".",
  "Outra confirmação deste banco estava em andamento. Tente de novo.",
  "O banco não entregou as contas deste item agora. Tente de novo mais tarde.",
  "A conta escolhida já não existe. Atualize a página.",
  "Esta aplicação já está em outro grupo.",
  // Integracoes (banco escolhido pelo código, D18)
  "Escolha o banco desta conexão antes de ver as contas.",
  "O banco desta conexão já foi escolhido.",
]);

/** Títulos com o nome do cônjuge ou do grupo: só no formato exato, com o nome curto e sem ponto no meio. */
const PADROES: readonly RegExp[] = [
  /^Esta conta já vem da conexão de [^.]{1,60}\.$/u,
  /^Não deu para ligar o grupo [^.]{1,60} à meta escolhida\.$/u,
];

/** O título pode ir para a tela? Só os fixos acima e os padrões com nome. */
export const tituloConhecido = (titulo: string) => TITULOS.has(titulo) || PADROES.some((p) => p.test(titulo));

type Resposta<T> = { data?: T; error?: unknown; response: Response };

/**
 * Como `chamar`, para formulários: um 400 de validação mostra as mensagens dos campos, que a API escreve fixas
 * (nunca com o valor digitado). 404 vira `naoEncontrado`.
 */
export async function chamarFormulario<T>(
  chamada: () => Promise<Resposta<T>>,
  generica = "Não foi possível salvar. Tente de novo.",
  naoEncontrado = "Não encontrado. Atualize a página.",
): Promise<T> {
  let r: Resposta<T>;
  try {
    r = await chamada();
  } catch {
    throw new ErroDeApi(0, SEM_CONEXAO);
  }
  if (r.response.ok) return r.data as T;
  const status = r.response.status;
  const corpo = r.error as { title?: unknown; errors?: Record<string, unknown> } | undefined;
  if (typeof corpo?.title === "string" && tituloConhecido(corpo.title)) throw new ErroDeApi(status, corpo.title);
  if (status === 400 && corpo?.errors) {
    const mensagens = Object.values(corpo.errors).flat().filter((m): m is string => typeof m === "string");
    if (mensagens.length > 0) throw new ErroDeApi(status, mensagens.join(" "));
  }
  if (status === 404) throw new ErroDeApi(status, naoEncontrado);
  throw new ErroDeApi(status, generica);
}

/** A API manda int64 como número (ou texto, pelo contrato); o front sempre usa número. */
export const n = (valor: number | string | null | undefined): number => Number(valor ?? 0);
