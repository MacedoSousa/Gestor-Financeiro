import { useQuery } from "@tanstack/react-query";
import { api } from "../api/cliente";

/**
 * Fase 0: casca da visão geral e verificação de que a API responde.
 * Os cartões ganham dados reais na Fase 1 (orçamento, contas, metas).
 */
export function VisaoGeral() {
  const saude = useQuery({
    queryKey: ["saude"],
    queryFn: async () => {
      const { data, error } = await api.GET("/api/v1/saude");
      if (error || !data) throw new Error("API indisponível");
      return data;
    },
  });

  const cartoes = [
    { rotulo: "Ainda dá para gastar", nota: "Teto do mês menos o já gasto", destaque: true },
    { rotulo: "Conta conjunta Itaú", nota: "Chega com o Meu Pluggy (Fase 2)" },
    { rotulo: "Aporte planejado", nota: "Soma das metas do mês" },
    { rotulo: "Patrimônio do casal", nota: "Contas + investimentos" },
  ];

  return (
    <>
      <header style={{ display: "flex", flexWrap: "wrap", justifyContent: "space-between", alignItems: "flex-end", gap: 16 }}>
        <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
          <span className="subtitulo">Visão geral</span>
          <h1 className="titulo">Olá!</h1>
        </div>
        <span className="status" role="status">
          <span className={`ponto ${saude.isSuccess ? "ok" : saude.isError ? "erro" : ""}`} aria-hidden="true" />
          {saude.isSuccess ? "API conectada" : saude.isError ? "API fora do ar" : "Conectando…"}
        </span>
      </header>

      <section className="grade-kpi" aria-label="Resumo do mês">
        {cartoes.map((c) => (
          <div key={c.rotulo} className={`cartao${c.destaque ? " destaque" : ""}`}>
            <div className="cartao-rotulo">{c.rotulo}</div>
            <div className="cartao-valor num">—</div>
            <div className="cartao-nota">{c.nota}</div>
          </div>
        ))}
      </section>
    </>
  );
}
