/**
 * Tipos da API — GERADO por `npm run gen:api` a partir de contracts/openapi.json.
 * Esta versão inicial foi escrita à mão para o primeiro build; o CI passa a regerá-la
 * assim que contracts/openapi.json for commitado. Não edite à mão depois disso.
 */

export interface paths {
  "/api/v1/saude": {
    parameters: { query?: never; header?: never; path?: never; cookie?: never };
    get: operations["Saude"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/casal": {
    parameters: { query?: never; header?: never; path?: never; cookie?: never };
    get: operations["ObterCasal"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
}

export type webhooks = Record<string, never>;

export interface components {
  schemas: {
    SaudeDto: { status: string; horario: string };
    CasalDto: { id: string; membros: components["schemas"]["MembroDto"][] };
    MembroDto: { id: string; apelido: string; ehVoce: boolean };
  };
  responses: never;
  parameters: never;
  requestBodies: never;
  headers: never;
  pathItems: never;
}

export type $defs = Record<string, never>;

export interface operations {
  Saude: {
    parameters: { query?: never; header?: never; path?: never; cookie?: never };
    requestBody?: never;
    responses: {
      200: {
        headers: { [name: string]: unknown };
        content: { "application/json": components["schemas"]["SaudeDto"] };
      };
    };
  };
  ObterCasal: {
    parameters: { query?: never; header?: never; path?: never; cookie?: never };
    requestBody?: never;
    responses: {
      200: {
        headers: { [name: string]: unknown };
        content: { "application/json": components["schemas"]["CasalDto"] };
      };
      404: {
        headers: { [name: string]: unknown };
        content?: never;
      };
    };
  };
}
