import { Route, Routes } from "react-router";
import { Layout } from "./layout/Layout";
import { VisaoGeral } from "./paginas/VisaoGeral";
import { Cadastro } from "./paginas/Cadastro";
import { EmBreve } from "./paginas/EmBreve";
import { Entrar } from "./paginas/Entrar";
import { PrimeiroAcesso } from "./paginas/PrimeiroAcesso";
import { NovoDispositivo } from "./paginas/NovoDispositivo";
import { RegistrarAcesso } from "./paginas/RegistrarAcesso";
import { Dispositivos } from "./paginas/Dispositivos";

export function App() {
  return (
    <Routes>
      <Route path="cadastro" element={<Cadastro />} />
      <Route path="entrar" element={<Entrar />} />
      <Route path="primeiro-acesso" element={<PrimeiroAcesso />} />
      <Route path="novo-dispositivo" element={<NovoDispositivo />} />
      <Route path="registrar" element={<RegistrarAcesso />} />
      <Route element={<Layout />}>
        <Route index element={<VisaoGeral />} />
        <Route path="lancamentos" element={<EmBreve titulo="Lançamentos" fase="Fase 1" />} />
        <Route path="orcamento" element={<EmBreve titulo="Orçamento" fase="Fase 1" />} />
        <Route path="boletos" element={<EmBreve titulo="Boletos e faturas" fase="Fase 1" />} />
        <Route path="metas" element={<EmBreve titulo="Metas e investimentos" fase="Fase 1" />} />
        <Route path="assistente" element={<EmBreve titulo="Assistente" fase="Fase 4" />} />
        <Route path="dispositivos" element={<Dispositivos />} />
        <Route path="*" element={<EmBreve titulo="Página não encontrada" />} />
      </Route>
    </Routes>
  );
}
