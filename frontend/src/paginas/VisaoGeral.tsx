import { useEffect, useRef, useState } from "react";
import { Link } from "react-router";
import { useContas } from "../api/contas";
import { useCategorias } from "../api/lancamentos";
import { useVisaoGeral } from "../api/painel";
import { nomeDoMes } from "../lib/mes";
import { saudacao } from "../lib/painel";
import { LancamentoRapido } from "./lancamentos/LancamentoRapido";
import { CartoesDoTopo } from "./visao-geral/CartoesDoTopo";
import { GraficoDoPatrimonio } from "./visao-geral/GraficoDoPatrimonio";
import { MetasDoPainel } from "./visao-geral/MetasDoPainel";
import { OrcamentoDoPainel } from "./visao-geral/OrcamentoDoPainel";
import { Esqueleto, Secao } from "./visao-geral/Secao";
import { UltimosLancamentos } from "./visao-geral/UltimosLancamentos";
import { Vencimentos } from "./visao-geral/Vencimentos";

function NovoLancamento({ aoFechar }: { aoFechar: () => void }) {
  const ref = useRef<HTMLDialogElement>(null);
  const contas = useContas();
  const categorias = useCategorias();
  useEffect(() => {
    const d = ref.current;
    if (d && !d.open) d.showModal();
  }, []);
  const temConta = contas.data?.some((c) => !c.arquivada) ?? false;
  return (
    <dialog ref={ref} className="dialogo dialogo-largo" aria-label="Novo lançamento" onCancel={aoFechar} onClose={aoFechar}>
      {(contas.isError || categorias.isError) && <p role="alert" className="alerta-form">Não foi possível carregar contas e categorias. Tente de novo.</p>}
      {(contas.isPending || categorias.isPending) && <p className="subtitulo">Carregando…</p>}
      {contas.data && !temConta && (
        <p className="cartao-nota">
          Cadastre uma conta antes de lançar. <Link to="/contas">Ir para contas e cartões</Link>
        </p>
      )}
      {contas.data && temConta && categorias.data && <LancamentoRapido contas={contas.data} categorias={categorias.data} />}
      <button type="button" className="botao-secundario" onClick={() => ref.current?.close()}>
        Fechar
      </button>
    </dialog>
  );
}

export function VisaoGeral() {
  const consulta = useVisaoGeral();
  const [novo, setNovo] = useState(false);
  const v = consulta.data;
  const tentando = consulta.isFetching;
  const refazer = () => void consulta.refetch();
  const base = { tentando, aoTentarDeNovo: refazer };

  return (
    <>
      <header className="cabecalho-pagina">
        <div className="cabecalho-titulo">
          <span className="subtitulo">
            {v ? `${nomeDoMes(v.mes)} · dia ${Number(v.diaDoMes)} de ${Number(v.diasNoMes)}` : "Visão geral"}
          </span>
          <h1 className="titulo">{v ? `${saudacao(new Date().getHours())}, ${v.apelido}` : "Visão geral"}</h1>
        </div>
        <button type="button" className="botao-primario" onClick={() => setNovo(true)}>
          + Novo lançamento
        </button>
      </header>

      {consulta.isPending && (
        <div aria-busy="true" aria-label="Carregando a visão geral">
          <div className="grade-kpi">
            {[0, 1, 2, 3].map((i) => (
              <div key={i} className="cartao">
                <Esqueleto linhas={3} />
              </div>
            ))}
          </div>
        </div>
      )}

      {consulta.isError && (
        <div role="alert" className="erro-secao erro-pagina">
          <span>{consulta.error.message}</span>
          <button type="button" className="botao-secundario" disabled={tentando} onClick={refazer}>
            Tentar de novo
          </button>
        </div>
      )}

      {v && (
        <>
          <CartoesDoTopo visao={v} {...base} />

          {v.avisosDeFatura.erro !== null || v.avisosDeFatura.dados === null ? (
            <div role="alert" className="faixa-aviso">
              <span>Não foi possível carregar</span>
              <button type="button" className="botao-secundario" disabled={tentando} onClick={refazer}>
                Tentar de novo
              </button>
            </div>
          ) : (
            v.avisosDeFatura.dados.map((a) => (
              <p key={a.contaId} role="status" className="faixa-aviso">
                {a.texto}
              </p>
            ))
          )}

          <div className="painel-linha painel-linha-1">
            <Secao titulo="Patrimônio em 12 meses" secao={v.patrimonioEm12Meses} {...base}>
              {(d) => <GraficoDoPatrimonio dados={d} />}
            </Secao>
            <Secao titulo="Metas" secao={v.metas} {...base} acao={<Link to="/metas">Ver todas</Link>}>
              {(d) => <MetasDoPainel dados={d} />}
            </Secao>
          </div>

          <div className="painel-linha painel-linha-2">
            <Secao titulo="Orçamento do mês" secao={v.orcamento} {...base} acao={<Link to="/orcamento">Ajustar</Link>}>
              {(d) => <OrcamentoDoPainel dados={d} />}
            </Secao>
            <Secao titulo="Próximos vencimentos" secao={v.vencimentos} {...base}>
              {(d) => <Vencimentos dados={d} />}
            </Secao>
            <Secao titulo="Últimos lançamentos" secao={v.ultimosLancamentos} {...base} acao={<Link to="/lancamentos">Ver todos</Link>}>
              {(d) => <UltimosLancamentos dados={d} />}
            </Secao>
          </div>
        </>
      )}

      {novo && <NovoLancamento aoFechar={() => setNovo(false)} />}
    </>
  );
}
