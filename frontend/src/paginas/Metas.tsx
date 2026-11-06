import { useState } from "react";
import { Link } from "react-router";
import { usePainelDeMetas, type PainelDeMetas } from "../api/metas";
import { CartaoDaMeta } from "./metas/CartaoDaMeta";
import { AtualizarSaldo } from "./metas/AtualizarSaldo";
import { DialogoDaMeta } from "./metas/DialogoDaMeta";
import { TabelaDeContas } from "./metas/TabelaDeContas";

type Aberto = { tipo: "meta"; metaId: string | null } | { tipo: "saldo"; contaId: string };

export function Metas() {
  const painel = usePainelDeMetas();
  const dados = painel.data;
  const [aberto, setAberto] = useState<Aberto | null>(null);
  const fechar = () => setAberto(null);
  const semContas = dados !== undefined && dados.contas.length === 0;

  return (
    <>
      <header className="cabecalho-pagina">
        <div className="cabecalho-titulo">
          <span className="subtitulo">Projeções com o rendimento líquido estimado de cada meta · IR e IOF entram na Fase 4</span>
          <h1 className="titulo">Metas e investimentos</h1>
        </div>
        <button type="button" className="botao-primario" disabled={semContas} onClick={() => setAberto({ tipo: "meta", metaId: null })}>
          + Nova meta
        </button>
      </header>

      {painel.isError && (
        <p role="alert" className="alerta-form">
          {painel.error.message}
        </p>
      )}
      {painel.isPending && <p className="subtitulo">Carregando…</p>}

      {dados && (
        <>
          {semContas && (
            <p className="subtitulo">
              Cadastre uma conta de poupança ou investimento para criar metas. <Link to="/contas">Ir para Contas</Link>
            </p>
          )}
          {!semContas && dados.metas.length === 0 && <p className="subtitulo">Crie a primeira meta: a casa, o carro, a reserva de emergência.</p>}
          {dados.metas.length > 0 && (
            <div className="grade-metas">
              {dados.metas.map((m) => (
                <CartaoDaMeta key={m.id} meta={m} aoEditar={() => setAberto({ tipo: "meta", metaId: m.id })}
                  aoAtualizarSaldo={(contaId) => setAberto({ tipo: "saldo", contaId })}
                 />
              ))}
            </div>
          )}
          {dados.contas.length > 0 && <TabelaDeContas contas={dados.contas} />}
        </>
      )}
      {dados && aberto?.tipo === "meta" && (
        <DialogoDaMeta
          key={aberto.metaId ?? "nova"}
          meta={dados.metas.find((m) => m.id === aberto.metaId)}
          contas={dados.contas}
          aoFechar={fechar}
        />
      )}
      {dados && aberto?.tipo === "saldo" && <DialogoDoSaldo contas={dados.contas} contaId={aberto.contaId} aoFechar={fechar} />}
    </>
  );
}

/** Usa o saldo atual do painel: depois de um 409 o painel recarrega e o diálogo mostra o saldo novo. */
function DialogoDoSaldo({ contas, contaId, aoFechar }: { contas: PainelDeMetas["contas"]; contaId: string; aoFechar: () => void }) {
  const conta = contas.find((c) => c.contaId === contaId);
  if (!conta) return null;
  return <AtualizarSaldo conta={conta} aoFechar={aoFechar} />;
}
