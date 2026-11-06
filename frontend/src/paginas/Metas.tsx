import { Link } from "react-router";
import { usePainelDeMetas } from "../api/metas";
import { CartaoDaMeta } from "./metas/CartaoDaMeta";
import { TabelaDeContas } from "./metas/TabelaDeContas";

// Os diálogos de criar/editar meta e de "Atualizar saldo" entram na Tarefa 7; estes ganchos ficam prontos.
function abrirNovaMeta(): void {}
function abrirEdicao(): void {}
function abrirAtualizarSaldo(): void {}

export function Metas() {
  const painel = usePainelDeMetas();
  const dados = painel.data;
  const semContas = dados !== undefined && dados.contas.length === 0;

  return (
    <>
      <header className="cabecalho-pagina">
        <div className="cabecalho-titulo">
          <span className="subtitulo">Projeções com o rendimento líquido estimado de cada meta · IR e IOF entram na Fase 4</span>
          <h1 className="titulo">Metas e investimentos</h1>
        </div>
        <button type="button" className="botao-primario" disabled={semContas} onClick={abrirNovaMeta}>
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
                <CartaoDaMeta key={m.id} meta={m} aoEditar={abrirEdicao} aoAtualizarSaldo={abrirAtualizarSaldo} />
              ))}
            </div>
          )}
          {dados.contas.length > 0 && <TabelaDeContas contas={dados.contas} />}
        </>
      )}
    </>
  );
}
