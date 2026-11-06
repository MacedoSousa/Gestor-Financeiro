import { ErroDeApi } from "./autenticacao";

const SEM_CONEXAO = "Sem conexão com o servidor. Tente de novo.";

/**
 * Títulos fixos que Contas, Lancamentos e Investimentos escrevem nos Problem Details. Só estes vão para a tela; qualquer outro
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
]);

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
  if (typeof corpo?.title === "string" && TITULOS.has(corpo.title)) throw new ErroDeApi(status, corpo.title);
  if (status === 400 && corpo?.errors) {
    const mensagens = Object.values(corpo.errors).flat().filter((m): m is string => typeof m === "string");
    if (mensagens.length > 0) throw new ErroDeApi(status, mensagens.join(" "));
  }
  if (status === 404) throw new ErroDeApi(status, naoEncontrado);
  throw new ErroDeApi(status, generica);
}

/** A API manda int64 como número (ou texto, pelo contrato); o front sempre usa número. */
export const n = (valor: number | string | null | undefined): number => Number(valor ?? 0);
