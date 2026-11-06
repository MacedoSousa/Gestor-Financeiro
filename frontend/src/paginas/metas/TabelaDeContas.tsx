import { rotuloDoTipo } from "../../api/contas";
import { n } from "../../api/formulario";
import type { PainelDeMetas } from "../../api/metas";
import { formatarReais } from "../../lib/dinheiro";

export function TabelaDeContas({ contas }: { contas: PainelDeMetas["contas"] }) {
  const total = contas.reduce((soma, c) => soma + n(c.saldoCentavos), 0);

  return (
    <section className="cartao-tabela" aria-labelledby="titulo-contas-investimento">
      <div className="cartao-cabecalho tabela-cabecalho">
        <h2 className="cartao-titulo" id="titulo-contas-investimento">
          Contas de investimento
        </h2>
        <span className="num subtitulo">{formatarReais(total)} em poupança e investimento</span>
      </div>
      <div className="rolagem-x">
        <table className="tabela">
          <thead>
            <tr>
              <th scope="col">Conta</th>
              <th scope="col">Instituição</th>
              <th scope="col">Tipo</th>
              <th scope="col">Meta</th>
              <th scope="col" className="celula-valor">
                Saldo
              </th>
            </tr>
          </thead>
          <tbody>
            {contas.map((c) => (
              <tr key={c.contaId}>
                <td>
                  {c.apelido}
                  {c.arquivada && <span className="cartao-nota"> (arquivada)</span>}
                </td>
                <td>{c.instituicao ?? "—"}</td>
                <td>{rotuloDoTipo(c.tipo)}</td>
                <td>{c.metaNome ?? "—"}</td>
                <td className="num celula-valor">{formatarReais(n(c.saldoCentavos))}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}
