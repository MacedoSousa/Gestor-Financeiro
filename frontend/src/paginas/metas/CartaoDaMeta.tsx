import { useState } from "react";
import { useAcoesDeMetas, useSimulacao, type Meta, type Projecao } from "../../api/metas";
import { n } from "../../api/formulario";
import { formatarReais } from "../../lib/dinheiro";
import { mesCurto, passoDoSimulador } from "../../lib/metas";
import { Alerta } from "../Campo";

type Props = {
  meta: Meta;
  aoEditar: () => void;
  aoAtualizarSaldo: (contaId: string) => void;
};

const plural = (qtd: number, um: string, varios: string) => `${qtd} ${qtd === 1 ? um : varios}`;

function textoChegaEm(p: Projecao): string {
  if (p.situacao === "ALCANCADA") return "Meta alcançada";
  if (p.situacao === "NAO_CHEGA" || !p.chegaEm) return "Não chega em 100 anos";
  return mesCurto(p.chegaEm);
}

function textoDaMeta(p: Projecao): string {
  if (p.situacao === "ALCANCADA") return "Meta alcançada";
  if (p.situacao === "NAO_CHEGA") return "Com esse aporte, a meta não chega em 100 anos.";
  return `Faltam ${plural(n(p.mesesAteMeta), "mês", "meses")}`;
}

function textoDaData(dataDesejada: string, p: Projecao): string {
  const base = `Desejada: ${mesCurto(dataDesejada)}`;
  if (p.dataDesejadaPassou) return `${base} · a data desejada já passou`;
  if (p.diferencaMeses == null) return base;
  const dif = n(p.diferencaMeses);
  if (dif === 0) return `${base} · na data desejada`;
  return `${base} · ${plural(Math.abs(dif), "mês", "meses")} ${dif < 0 ? "antes" : "depois"}`;
}

export function CartaoDaMeta({ meta, aoEditar, aoAtualizarSaldo }: Props) {
  const acoes = useAcoesDeMetas();
  const planejado = n(meta.aportePlanejadoCentavos);
  const [aporte, setAporte] = useState(planejado);
  const [salvando, setSalvando] = useState(false);
  const [erro, setErro] = useState<string>();
  const [tentativa, setTentativa] = useState(0);

  const simulando = aporte !== planejado;
  const simulacao = useSimulacao(meta.id, aporte, simulando);
  // Os números vêm sempre do servidor: o do painel no planejado, o da simulação fora dele.
  const projecao: Projecao = simulando && simulacao.data ? simulacao.data : meta.projecao;
  const atualizando = simulando && (simulacao.isFetching || simulacao.isPlaceholderData || !simulacao.data);

  const guardado = n(meta.guardadoCentavos);
  const progresso = Math.min(100, Math.max(0, n(meta.progressoPercentual)));
  const aporteNecessario = projecao.aporteNecessarioCentavos == null ? null : n(projecao.aporteNecessarioCentavos);
  const contas = meta.contas;

  async function salvarComoPlanejado() {
    setTentativa((t) => t + 1);
    setSalvando(true);
    setErro(undefined);
    try {
      await acoes.editarMeta(meta.id, {
        nome: meta.nome,
        valorAlvoCentavos: n(meta.valorAlvoCentavos),
        dataDesejada: meta.dataDesejada,
        aportePlanejadoCentavos: aporte,
        rendimentoMensalPercentual: n(meta.rendimentoMensalPercentual),
        contaIds: contas.map((c) => c.contaId),
      });
    } catch (x) {
      setErro(x instanceof Error ? x.message : "Não foi possível salvar. Tente de novo.");
    } finally {
      setSalvando(false);
    }
  }

  const titulo = `titulo-meta-${meta.id}`;
  return (
    <section className="cartao meta" aria-labelledby={titulo}>
      <div className="meta-topo">
        <div>
          <div className="cartao-rotulo">Meta</div>
          <h2 className="meta-nome" id={titulo}>
            {meta.nome}
          </h2>
        </div>
        <div className="meta-chega">
          <div className="cartao-rotulo">Chega em</div>
          <div className="num meta-chega-valor" aria-live="polite">
            {textoChegaEm(projecao)}
          </div>
        </div>
      </div>

      <div className="progresso-linha">
        <div
          className="progresso"
          role="progressbar"
          aria-label={`Progresso de ${meta.nome}`}
          aria-valuenow={progresso}
          aria-valuemin={0}
          aria-valuemax={100}
        >
          <div className="progresso-preenchido" style={{ width: `${progresso}%` }} />
        </div>
        <span className="num progresso-texto">{progresso}%</span>
      </div>
      <div className="num meta-valores">
        <span>{formatarReais(guardado)} guardados</span>
        <span>alvo {formatarReais(n(meta.valorAlvoCentavos))}</span>
      </div>

      {meta.dataDesejada && (
        <div className="meta-data">
          <div>{textoDaData(meta.dataDesejada, projecao)}</div>
          {aporteNecessario !== null && aporteNecessario > 0 && !projecao.dataDesejadaPassou && (
            <div>
              Para chegar em {mesCurto(meta.dataDesejada)}: <span className="num">{formatarReais(aporteNecessario)}</span> por mês
            </div>
          )}
        </div>
      )}

      <div className="meta-mes">
        <div>
          Aporte deste mês: <span className="num">{formatarReais(n(meta.aporteDoMesCentavos))}</span> de{" "}
          <span className="num">{formatarReais(planejado)}</span> planejados
        </div>
        <div className="cartao-nota">Aporte do mês conta só dinheiro novo.</div>
      </div>

      <div className="simulador">
        <div className="simulador-linha">
          <div>
            <div className="cartao-rotulo" id={`aporte-${meta.id}`}>
              Aporte mensal
            </div>
            <div className="num simulador-valor" aria-labelledby={`aporte-${meta.id}`}>
              {formatarReais(aporte)}
            </div>
          </div>
          <div className="simulador-botoes">
            <button type="button" className="botao-passo" aria-label="Diminuir aporte em R$ 500" onClick={() => setAporte((a) => passoDoSimulador(a, -1))}>
              −
            </button>
            <button type="button" className="botao-passo" aria-label="Aumentar aporte em R$ 500" onClick={() => setAporte((a) => passoDoSimulador(a, 1))}>
              +
            </button>
          </div>
        </div>
        <div className="simulador-meses">
          <span aria-live="polite">{textoDaMeta(projecao)}</span>
          {atualizando && <span className="atualizando"> atualizando…</span>}
        </div>
        {simulando && (
          <div className="simulador-simulacao">
            <span>
              Simulação: o planejado continua <span className="num">{formatarReais(planejado)}</span>
            </span>
            <div className="simulador-acoes">
              <button type="button" className="botao-link" onClick={() => setAporte(planejado)}>
                Voltar ao planejado
              </button>
              <button type="button" className="botao-secundario" disabled={salvando} onClick={() => void salvarComoPlanejado()}>
                {salvando ? "Salvando…" : "Salvar como planejado"}
              </button>
            </div>
          </div>
        )}
        <Alerta mensagem={erro} tentativa={tentativa} />
      </div>

      <div className="meta-contas">
        <div className="cartao-rotulo">Contas da meta</div>
        {contas.length === 0 && <div className="cartao-nota">Nenhuma conta vinculada.</div>}
        <ul className="meta-contas-lista">
          {contas.map((c) => (
            <li key={c.contaId}>
              <span>
                {c.apelido}
                {c.arquivada && <span className="cartao-nota"> (arquivada)</span>}
              </span>
              <span className="num">{formatarReais(n(c.saldoCentavos))}</span>
              <button type="button" className="botao-link" aria-label={`Atualizar saldo de ${c.apelido}`} onClick={() => aoAtualizarSaldo(c.contaId)}>
                Atualizar saldo
              </button>
            </li>
          ))}
        </ul>
      </div>

      <div>
        <button type="button" className="botao-secundario" aria-label={`Editar ${meta.nome}`} onClick={aoEditar}>
          Editar
        </button>
      </div>
    </section>
  );
}
