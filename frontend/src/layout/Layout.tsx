import { Navigate, NavLink, Outlet } from "react-router";
import type { ReactNode } from "react";
import { useEstadoDoCadastro } from "../api/cadastro";

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

export function Layout() {
  const cadastro = useEstadoDoCadastro();
  if (cadastro.isPending) return null;
  if (cadastro.data?.disponivel) return <Navigate to="/cadastro" replace />;

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
      </aside>
      <main className="conteudo">
        <Outlet />
      </main>
    </div>
  );
}
