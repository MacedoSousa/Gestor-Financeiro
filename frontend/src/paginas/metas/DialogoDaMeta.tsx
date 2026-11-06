import { useEffect, useRef, useState, type FormEvent } from "react";
import { ErroDeApi } from "../../api/autenticacao";
import { n } from "../../api/formulario";
import { useAcoesDeMetas, type Meta, type PainelDeMetas } from "../../api/metas";
import { centavosDeTexto, textoDeCentavos } from "../../lib/dinheiro";
import { lerPercentual } from "../../lib/metas";
import { Alerta, Campo } from "../Campo";

const LIMITE = 100_000_000_000;

type Props = { meta?: Meta; contas: PainelDeMetas["contas"]; aoFechar: () => void };

/** Criar ou editar uma meta, num &lt;dialog&gt;. Apagar pede confirmação dentro dele. */
export function DialogoDaMeta({ meta, contas, aoFechar }: Props) {
  const ref = useRef<HTMLDialogElement>(null);
  const acoes = useAcoesDeMetas();
  const [nome, setNome] = useState(meta?.nome ?? "");
  const [alvo, setAlvo] = useState(() => (meta ? textoDeCentavos(n(meta.valorAlvoCentavos)) : ""));
  const [data, setData] = useState(meta?.dataDesejada ?? "");
  const [aporte, setAporte] = useState(() => (meta ? textoDeCentavos(n(meta.aportePlanejadoCentavos)) : "0,00"));
  const [rendimento, setRendimento] = useState(() => (meta ? String(n(meta.rendimentoMensalPercentual)).replace(".", ",") : ""));
  const [escolhidas, setEscolhidas] = useState<ReadonlySet<string>>(() => new Set(meta?.contas.map((c) => c.contaId) ?? []));
  const [confirmandoApagar, setConfirmandoApagar] = useState(false);
  const [enviando, setEnviando] = useState(false);
  const [erro, setErro] = useState<string>();
  const [tentativa, setTentativa] = useState(0);

  useEffect(() => {
    const d = ref.current;
    if (d && !d.open) d.showModal();
  }, []);

  // Arquivada só aparece se já é desta meta; conta de outra meta aparece desabilitada.
  const visiveis = contas.filter((c) => !c.arquivada || (meta !== undefined && c.metaId === meta.id));

  function falhar(mensagem: string) {
    setErro(mensagem);
    setTentativa((t) => t + 1);
  }

  async function concluir(acao: () => Promise<unknown>) {
    setErro(undefined);
    setEnviando(true);
    try {
      await acao();
      aoFechar();
    } catch (e) {
      falhar(e instanceof ErroDeApi ? e.message : "Não foi possível concluir. Tente de novo.");
      setEnviando(false);
    }
  }

  function alternar(id: string, marcada: boolean) {
    setEscolhidas((atual) => {
      const novo = new Set(atual);
      if (marcada) novo.add(id);
      else novo.delete(id);
      return novo;
    });
  }

  function salvar(e: FormEvent) {
    e.preventDefault();
    const nomeLimpo = nome.trim();
    if (!nomeLimpo) return falhar("Informe o nome da meta.");
    const valorAlvo = centavosDeTexto(alvo);
    if (valorAlvo === null || valorAlvo < 1 || valorAlvo > LIMITE) return falhar("Informe o valor alvo maior que zero, como 250.000,00.");
    const valorAporte = centavosDeTexto(aporte);
    if (valorAporte === null || valorAporte > LIMITE) return falhar("Informe o aporte mensal planejado, como 1.500,00 (ou 0).");
    const taxa = lerPercentual(rendimento);
    if (!taxa.ok) return falhar(taxa.mensagem);
    if (escolhidas.size === 0) return falhar("Escolha ao menos uma conta.");
    const corpo = {
      nome: nomeLimpo,
      valorAlvoCentavos: valorAlvo,
      dataDesejada: data || null,
      aportePlanejadoCentavos: valorAporte,
      rendimentoMensalPercentual: taxa.valor,
      contaIds: [...escolhidas],
    };
    void concluir(() => (meta ? acoes.editarMeta(meta.id, corpo) : acoes.criarMeta(corpo)));
  }

  return (
    <dialog ref={ref} className="dialogo" aria-labelledby="titulo-meta" onCancel={aoFechar}>
      <h2 id="titulo-meta" className="dialogo-titulo">
        {meta ? "Editar meta" : "Nova meta"}
      </h2>
      <Alerta mensagem={erro} tentativa={tentativa} />
      <form className="bloco-form" onSubmit={salvar} noValidate>
        <Campo rotulo="Nome" valor={nome} aoMudar={setNome} maxLength={80} autoComplete="off" />
        <Campo rotulo="Valor alvo" valor={alvo} aoMudar={setAlvo} inputMode="decimal" classeEntrada="num" autoComplete="off" />
        <Campo rotulo="Data desejada (opcional)" tipo="month" valor={data} aoMudar={setData} />
        <Campo rotulo="Aporte mensal planejado" valor={aporte} aoMudar={setAporte} inputMode="decimal" classeEntrada="num" autoComplete="off" />
        <Campo
          rotulo="Rendimento líquido estimado (% ao mês)"
          valor={rendimento}
          aoMudar={setRendimento}
          inputMode="decimal"
          classeEntrada="num"
          autoComplete="off"
          nota="De 0 a 5, com até 4 casas. Exemplo: 0,85."
        />
        <fieldset className="escolha">
          <legend className="campo-rotulo">Contas da meta</legend>
          {visiveis.length === 0 && <span className="cartao-nota">Nenhuma conta disponível.</span>}
          {visiveis.map((c) => {
            const emOutra = c.metaId !== null && c.metaId !== meta?.id;
            return (
              <label key={c.contaId} className="campo-check">
                <input
                  type="checkbox"
                  checked={escolhidas.has(c.contaId)}
                  disabled={emOutra}
                  onChange={(e) => alternar(c.contaId, e.target.checked)}
                />
                {c.apelido}
                {c.arquivada && " (arquivada)"}
                {emOutra && ` (meta ${c.metaNome ?? ""})`}
              </label>
            );
          })}
        </fieldset>
        {!confirmandoApagar && (
          <div className="acoes-linha">
            <button type="submit" className="botao-primario" disabled={enviando}>
              {enviando ? "Salvando…" : "Salvar"}
            </button>
            <button type="button" className="botao-secundario" onClick={aoFechar}>
              Cancelar
            </button>
            {meta && (
              <button type="button" className="botao-perigo" disabled={enviando} onClick={() => setConfirmandoApagar(true)}>
                Apagar
              </button>
            )}
          </div>
        )}
      </form>
      {meta && confirmandoApagar && (
        <div className="confirmacao" role="group" aria-labelledby="titulo-apagar-meta">
          <p id="titulo-apagar-meta" className="confirmacao-texto">
            Apagar a meta {meta.nome}? As contas ficam livres; o dinheiro não muda.
          </p>
          <div className="acoes-linha">
            <button type="button" className="botao-perigo" disabled={enviando} onClick={() => void concluir(() => acoes.apagarMeta(meta.id))}>
              {enviando ? "Apagando…" : "Apagar"}
            </button>
            <button type="button" className="botao-secundario" disabled={enviando} onClick={() => setConfirmandoApagar(false)}>
              Voltar
            </button>
          </div>
        </div>
      )}
    </dialog>
  );
}
