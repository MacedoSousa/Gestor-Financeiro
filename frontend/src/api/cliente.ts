import createClient from "openapi-fetch";
import type { paths } from "./schema";

/**
 * Único ponto de saída do front: a própria API, na mesma origem.
 * A sessão é um cookie HttpOnly (BFF); nenhum token passa pelo JavaScript.
 */
export const api = createClient<paths>({
  baseUrl: "",
  credentials: "same-origin",
  headers: { "X-Requested-With": "gestor-web" },
});
