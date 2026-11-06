import { useRef, useState, type FormEvent } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { ErroDeApi } from "../../api/autenticacao";
import type { Conta } from "../../api/contas";
import { criarLancamento, criarTransferencia, hojeIso, type Categoria } from "../../api/lancamentos";
import { centavosDeTexto } from "../../lib/dinheiro";
import { AVISO_DE_APORTE, avisaAporteEmDobro, categoriasDoTipo } from "../../lib/lancamentos";
import { Alerta, Campo, CampoSelecao } from "../Campo";

type Tipo = "DESPESA" | "RECEITA" | "ESTORNO" | "TRANSFERENCIA";

const TIPOS: ReadonlyArray<{ valor: Tipo; rotulo: string }> = [
  { valor: "DESPESA", rotulo: "Despesa" },
  { valor: "RECEITA", rotulo: "Receita" },
  { valor: "ESTORNO", rotulo: "Estorno" },
  { valor: "TRANSFERENCIA", rotulo: "Transferência" },
];

const PARCELAS = Array.from({ length: 24 }, (_, i) => ({ valor: String(i + 1), rotulo: i === 0 ? "À vista" : `${i + 1}x` }));

/** Escolhido, se ainda for uma opção válida; senão a primeira opção. */
const efetivo = (escolhido: string, opcoes: ReadonlyArray<{ id: string }>) =>
  opcoes.some((o) => o.id === escolhido) ? escolhido : (opcoes[0]?.id ?? "");

type Props = { contas: Conta[]; categorias: Categoria[] };

/**
 * Lançamento rápido. A chave de idempotência nasce com o formulário e só muda depois de salvar com sucesso:
 * duplo clique ou reenvio depois de uma falha repetem a mesma chave, e a API devolve o mesmo lançamento.
 */
export function LancamentoRapido({ contas, categorias }: Props) {
  const consultas = useQueryClient();
  const [tipo, setTipo] = useState<Tipo>("DESPESA");
  const [valor, setValor] = useState("");
  const [descricao, setDescricao] = useState("");
  const [categoriaId, setCategoriaId] = useState("");
  const [contaId, setContaId] = useState("");
  const [destinoId, setDestinoId] = useState("");
  const [data, setData] = useState(hojeIso);
  const [parcelas, setParcelas] = useState("1");
  const [chave, setChave] = useState(() => crypto.randomUUID());
  const [enviando, setEnviando] = useState(false);
  const enviandoRef = useRef(false);
  const [erro, setErro] = useState<string>();
  const [tentativa, setTentativa] = useState(0);
  const [aviso, setAviso] = useState<string>();

  const ativas = contas.filter((c) => !c.arquivada);
  const opcoesDeCategoria = tipo === "TRANSFERENCIA" ? [] : categoriasDoTipo(tipo, categorias);
  const conta = efetivo(contaId, ativas);
  const destinos = ativas.filter((c) => c.id !== conta);
  const destino = efetivo(destinoId, destinos);
  const categoria = efetivo(categoriaId, opcoesDeCategoria);
  const ehCartao = ativas.find((c) => c.id === conta)?.tipo === "CARTAO";
  const podeParcelar = tipo === "DESPESA" && ehCartao;
  const mostrarAviso = avisaAporteEmDobro(
    tipo,
    opcoesDeCategoria.find((c) => c.id === categoria),
    ativas.find((c) => c.id === conta),
  );
  const opcoesDeConta = ativas.map((c) => ({ valor: c.id, rotulo: c.apelido }));

  function falhar(mensagem: string) {
    setErro(mensagem);
    setTentativa((t) => t + 1);
  }

  async function salvar(e: FormEvent) {
    e.preventDefault();
    if (enviandoRef.current) return;
    setErro(undefined);
    setAviso(undefined);

    const centavos = centavosDeTexto(valor);
    if (!centavos) return falhar("Informe um valor maior que zero, como 142,50.");
    if (!descricao.trim()) return falhar("Informe a descrição.");
    if (!conta) return falhar("Escolha a conta.");
    if (tipo === "TRANSFERENCIA" && !destino) return falhar("Cadastre outra conta para receber a transferência.");
    if (tipo !== "TRANSFERENCIA" && !categoria) return falhar("Escolha a categoria.");

    enviandoRef.current = true;
    setEnviando(true);
    try {
      if (tipo === "TRANSFERENCIA") {
        await criarTransferencia({
          chaveIdempotente: chave,
          contaOrigemId: conta,
          contaDestinoId: destino,
          valorCentavos: centavos,
          data,
          descricao: descricao.trim(),
        });
      } else {
        await criarLancamento({
          chaveIdempotente: chave,
          contaId: conta,
          natureza: tipo === "ESTORNO" ? "RECEITA" : tipo,
          valorCentavos: centavos,
          data,
          descricao: descricao.trim(),
          contraparte: null,
          categoriaId: categoria,
          parcelas: podeParcelar ? Number(parcelas) : 1,
        });
      }
      setChave(crypto.randomUUID());
      setValor("");
      setDescricao("");
      setParcelas("1");
      setAviso(tipo === "TRANSFERENCIA" ? "Transferência salva." : "Lançamento salvo.");
      await Promise.all([
        consultas.invalidateQueries({ queryKey: ["lancamentos"] }),
        consultas.invalidateQueries({ queryKey: ["saldos"] }),
      ]);
    } catch (err) {
      if (err instanceof ErroDeApi && err.status === 401) void consultas.invalidateQueries({ queryKey: ["eu"] });
      falhar(err instanceof ErroDeApi ? err.message : "Não foi possível salvar. Tente de novo.");
    } finally {
      enviandoRef.current = false;
      setEnviando(false);
    }
  }

  return (
    <form className="cartao" onSubmit={(e) => void salvar(e)} noValidate aria-labelledby="titulo-rapido">
      <h2 id="titulo-rapido" className="cartao-titulo">
        Lançamento rápido
      </h2>
      <div className="segmentos" role="group" aria-label="Tipo de lançamento">
        {TIPOS.map((t) => (
          <button key={t.valor} type="button" className="segmento" aria-pressed={tipo === t.valor} onClick={() => setTipo(t.valor)}>
            {t.rotulo}
          </button>
        ))}
      </div>
      <Alerta mensagem={erro} tentativa={tentativa} />
      <Campo rotulo="Valor" valor={valor} aoMudar={setValor} inputMode="decimal" placeholder="0,00" classeEntrada="num entrada-valor" autoComplete="off" />
      <Campo
        rotulo="Descrição"
        valor={descricao}
        aoMudar={setDescricao}
        maxLength={140}
        autoComplete="off"
        placeholder={tipo === "TRANSFERENCIA" ? "ex.: pagamento da fatura" : "ex.: feira de domingo"}
      />
      {tipo !== "TRANSFERENCIA" && (
        <CampoSelecao
          rotulo="Categoria"
          valor={categoria}
          aoMudar={setCategoriaId}
          opcoes={opcoesDeCategoria.map((c) => ({ valor: c.id, rotulo: c.nome }))}
        />
      )}
      {mostrarAviso && <p className="cartao-nota">{AVISO_DE_APORTE}</p>}
      <CampoSelecao rotulo={tipo === "TRANSFERENCIA" ? "Conta de origem" : "Conta"} valor={conta} aoMudar={setContaId} opcoes={opcoesDeConta} />
      {tipo === "TRANSFERENCIA" && (
        <CampoSelecao
          rotulo="Conta de destino"
          valor={destino}
          aoMudar={setDestinoId}
          opcoes={destinos.map((c) => ({ valor: c.id, rotulo: c.apelido }))}
          nota="Para pagar a fatura, escolha o cartão como destino."
        />
      )}
      {podeParcelar && <CampoSelecao rotulo="Parcelas" valor={parcelas} aoMudar={setParcelas} opcoes={PARCELAS} />}
      <Campo rotulo="Data" tipo="date" valor={data} aoMudar={setData} />
      <button type="submit" className="botao-primario" disabled={enviando}>
        {enviando ? "Salvando…" : "Salvar"}
      </button>
      {aviso && (
        <p role="status" className="cartao-nota">
          {aviso}
        </p>
      )}
    </form>
  );
}
