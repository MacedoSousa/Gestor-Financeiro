import { Navigate, NavLink, Outlet, useNavigate } from "react-router";
import { useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { useEstadoDoCadastro } from "../api/cadastro";
import { sair, useEu } from "../api/autenticacao";

const icone = (filhos: ReactNode) => (
  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden="true">
    {filhos}
  </svg>
);

const itens = [
  {
    para: "/",
    rotulo: "Visão geral",
    icone: icone(
      <>
        <rect x="3" y="3" width="7" height="7" rx="1.5" />
        <rect x="14" y="3" width="7" height="7" rx="1.5" />
        <rect x="3" y="14" width="7" height="7" rx="1.5" />
        <rect x="14" y="14" width="7" height="7" rx="1.5" />
      </>,
    ),
  },
  { para: "/lancamentos", rotulo: "Lançamentos", icone: icone(<path d="M8 6h13M8 12h13M8 18h13M3 6h.01M3 12h.01M3 18h.01" />) },
  {
    para: "/orcamento",
    rotulo: "Orçamento",
    icone: icone(
      <>
        <path d="M21 12A9 9 0 1 1 12 3v9z" />
        <path d="M15 3.5A9 9 0 0 1 20.5 9H15z" />
      </>,
    ),
  },
  {
    para: "/boletos",
    rotulo: "Boletos e faturas",
    icone: icone(
      <>
        <rect x="3" y="5" width="18" height="16" rx="2" />
        <path d="M3 10h18M8 3v4M16 3v4" />
      </>,
    ),
  },
  {
    para: "/metas",
    rotulo: "Metas e investimentos",
    icone: icone(
      <>
        <circle cx="12" cy="12" r="9" />
        <circle cx="12" cy="12" r="5" />
        <circle cx="12" cy="12" r="1" />
      </>,
    ),
  },
  { para: "/assistente", rotulo: "Assistente", icone: icone(<path d="M12 3l1.8 5.2L19 10l-5.2 1.8L12 17l-1.8-5.2L5 10l5.2-1.8z" />) },
];

const itensSistema = [
  {
    para: "/dispositivos",
    rotulo: "Dispositivos",
    icone: icone(
      <>
        <rect x="7" y="2" width="10" height="20" rx="2" />
        <path d="M11 18h2" />
      </>,
    ),
  },
];

export function Layout() {
  const cadastro = useEstadoDoCadastro();
  // O cadastro vem primeiro: só depois dele a sessão é consultada.
  const eu = useEu(cadastro.isSuccess && !cadastro.data.disponivel);
  const navegar = useNavigate();
  const consultas = useQueryClient();
  const [saindo, setSaindo] = useState(false);

  async function aoSair() {
    setSaindo(true);
    try {
      await sair();
    } catch {
      // Sem resposta: o cookie pode seguir válido; a tela de entrada volta a conferir.
    }
    consultas.clear();
    void navegar("/entrar", { replace: true });
  }

  if (cadastro.isPending) return null;
  if (cadastro.data?.disponivel) return <Navigate to="/cadastro" replace />;
  // Rotas públicas não passam por aqui, então não há laço: 401 leva a /entrar uma vez.
  if (eu.isPending) return null;
  if (eu.error?.status === 401) return <Navigate to="/entrar" replace />;
  if (eu.isError)
    return (
      <div className="pagina-centro">
        <p role="alert" className="alerta-form">
          {eu.error.message}
        </p>
      </div>
    );

  return (
    <div className="casca">
      <aside className="lateral">
        <div className="marca">
          <div className="marca-icone" aria-hidden="true">
            GF
          </div>
          <div>
            <div style={{ fontWeight: 600, fontSize: 15 }}>Gestor Financeiro</div>
            <div className="subtitulo">Família</div>
          </div>
        </div>
        <nav className="nav" aria-label="Principal">
          {itens.map((item) => (
            <NavLink key={item.para} to={item.para} end={item.para === "/"}>
              {item.icone}
              {item.rotulo}
            </NavLink>
          ))}
        </nav>
        <nav className="nav" aria-label="Sistema">
          <span className="nav-secao">Sistema</span>
          {itensSistema.map((item) => (
            <NavLink key={item.para} to={item.para}>
              {item.icone}
              {item.rotulo}
            </NavLink>
          ))}
        </nav>
        <div className="lateral-rodape">
          <span className="subtitulo">{eu.data.apelido}</span>
          <button type="button" className="botao-secundario" onClick={() => void aoSair()} disabled={saindo}>
            Sair
          </button>
        </div>
      </aside>
      <main className="conteudo">
        <Outlet />
      </main>
    </div>
  );
}
