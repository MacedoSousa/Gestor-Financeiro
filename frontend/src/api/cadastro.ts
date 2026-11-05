import { useMutation, useQuery } from "@tanstack/react-query";
import { api } from "./cliente";
import type { components } from "./schema";

export type CadastroDoCasal = components["schemas"]["CadastroDoCasalDto"];
export type CasalCadastrado = components["schemas"]["CasalCadastradoDto"];
export type EstadoDoCadastro = components["schemas"]["EstadoDoCadastroDto"];

/** Erro do cadastro: `errors` só existe no 400 (chaves como `conjuge1.cpf`). */
export class ErroDeCadastro extends Error {
  readonly status: number;
  readonly errors: Record<string, string[]>;

  constructor(status: number, mensagem: string, errors: Record<string, string[]> = {}) {
    super(mensagem);
    this.status = status;
    this.errors = errors;
  }
}

export function useEstadoDoCadastro() {
  return useQuery({
    queryKey: ["cadastro"],
    queryFn: async (): Promise<EstadoDoCadastro> => {
      const { data, error } = await api.GET("/api/v1/casal/cadastro");
      if (error || !data) throw new Error("Não foi possível consultar o cadastro.");
      return data;
    },
  });
}

export function useCadastrarCasal() {
  return useMutation<CasalCadastrado, ErroDeCadastro, CadastroDoCasal>({
    mutationFn: async (corpo) => {
      let resposta;
      try {
        resposta = await api.POST("/api/v1/casal", { body: corpo });
      } catch {
        throw new ErroDeCadastro(0, "Sem conexão com o servidor. Tente de novo.");
      }
      const { data, error, response } = resposta;
      if (data) return data;
      if (response.status === 404) throw new ErroDeCadastro(404, "O casal já foi cadastrado.");
      if (response.status === 429)
        throw new ErroDeCadastro(429, "Muitas tentativas. Aguarde alguns minutos e tente de novo.");
      const problema = error as { title?: unknown; errors?: unknown } | undefined;
      if (response.status === 400) {
        const errors = problema?.errors && typeof problema.errors === "object" ? (problema.errors as Record<string, string[]>) : {};
        throw new ErroDeCadastro(400, "Revise os dados do formulário.", errors);
      }
      if (response.status === 403 && typeof problema?.title === "string") throw new ErroDeCadastro(403, problema.title);
      throw new ErroDeCadastro(response.status, "Não foi possível concluir o cadastro. Tente de novo.");
    },
  });
}
