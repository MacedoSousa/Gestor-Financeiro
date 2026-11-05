# Telas de referência

Cópia das pranchas do canvas "Gestor Financeiro — Telas" (tema escuro, desktop primeiro). Servem de referência de layout, espaçamento e hierarquia; o código do front usa os tokens de `frontend/src/styles/tokens.css`, que têm as mesmas cores.

| Arquivo | Tela |
| --- | --- |
| `telas/Main.dc.html` | Visão geral (cartões do mês, patrimônio, metas, orçamento, vencimentos, últimos lançamentos) |
| `telas/Lancamentos.dc.html` | Extrato com filtros, caixa "Conciliar" e lançamento rápido |
| `telas/Orcamento.dc.html` | Divisão da renda, teto por categoria, dívidas |
| `telas/Boletos.dc.html` | Calendário de vencimentos, fatura do cartão, adicionar boleto |
| `telas/Metas.dc.html` | Metas com simulador de aporte, carteira |
| `telas/Assistente.dc.html` | Relatório mensal e conversa |

Os arquivos são HTML com estilos inline. `{{...}}` e `<sc-for>`/`<sc-if>` são marcações do editor de design (dados de exemplo calculados no `<script>` no fim de cada arquivo), não do React. Os valores são de exemplo.

Telas que ainda não existem aqui (cadastro, login, conexões, auditoria) seguem o mesmo padrão: barra lateral de 232–272 px, cartões `#111720` com borda `#1F2935` e raio 16 px, títulos de 28 px, números em Geist Mono, um único destaque em verde-água (`#2DD4BF`) por tela.
