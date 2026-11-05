import { Route, Routes } from "react-router";
import { Layout } from "./layout/Layout";
import { VisaoGeral } from "./paginas/VisaoGeral";
import { EmBreve } from "./paginas/EmBreve";

export function App() {
  return (
    <Routes>
      <Route element={<Layout />}>
        <Route index element={<VisaoGeral />} />
        <Route path="lancamentos" element={<EmBreve titulo="Lançamentos" fase="Fase 1" />} />
        <Route path="orcamento" element={<EmBreve titulo="Orçamento" fase="Fase 1" />} />
        <Route path="boletos" element={<EmBreve titulo="Boletos e faturas" fase="Fase 1" />} />
        <Route path="metas" element={<EmBreve titulo="Metas e investimentos" fase="Fase 1" />} />
        <Route path="assistente" element={<EmBreve titulo="Assistente" fase="Fase 4" />} />
        <Route path="*" element={<EmBreve titulo="Página não encontrada" />} />
      </Route>
    </Routes>
  );
}
