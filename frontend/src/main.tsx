import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { QueryCache, QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { BrowserRouter } from "react-router";
import "@fontsource/geist-sans/400.css";
import "@fontsource/geist-sans/500.css";
import "@fontsource/geist-sans/600.css";
import "@fontsource/geist-mono/500.css";
import "@fontsource/geist-mono/600.css";
import "./styles/tokens.css";
import "./styles/app.css";
import { App } from "./App";
import { ErroDeApi } from "./api/autenticacao";

const consultas: QueryClient = new QueryClient({
  // 401 em qualquer consulta logada: a sessão acabou. Invalida ["eu"] e o Layout leva a /entrar (uma vez).
  queryCache: new QueryCache({
    onError: (erro, consulta) => {
      if (erro instanceof ErroDeApi && erro.status === 401 && consulta.queryKey[0] !== "eu")
        void consultas.invalidateQueries({ queryKey: ["eu"] });
    },
  }),
  defaultOptions: {
    // Segredos (otpauth, códigos, senha do primeiro acesso) não ficam no cache de mutações depois de sair da tela.
    mutations: { gcTime: 0 },
    queries: { staleTime: 60_000, retry: 1, refetchOnWindowFocus: false } },
});

createRoot(document.getElementById("raiz")!).render(
  <StrictMode>
    <QueryClientProvider client={consultas}>
      <BrowserRouter>
        <App />
      </BrowserRouter>
    </QueryClientProvider>
  </StrictMode>,
);
