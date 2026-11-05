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
      const { data, error, response } = await api.POST("/api/v1/casal", { body: corpo });
      if (data) return data;
      if (response.status === 404) throw new ErroDeCadastro(404, "O casal já foi cadastrado.");
      const problema = error as { title?: string | null; errors?: Record<string, string[]> } | undefined;
      if (response.status === 400) {
        throw new ErroDeCadastro(400, problema?.title ?? "Confira os campos.", problema?.errors ?? {});
      }
      throw new ErroDeCadastro(response.status, problema?.title ?? "Não foi possível concluir o cadastro.");
    },
  });
}
