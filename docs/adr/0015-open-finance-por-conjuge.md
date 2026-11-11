# ADR-015 — Open Finance: uma conexão por cônjuge, só leitura, sem webhook

**Decisão:** cada cônjuge tem a própria conta no Meu Pluggy e as próprias credenciais da Pluggy, guardadas em `infra/secrets/` por cônjuge. O sistema só lê (contas, transações, faturas, investimentos), sincroniza uma vez por dia por job e não expõe webhook. Nas contas ligadas ao banco, o banco é a fonte: nada se lança à mão nelas.

**Por quê:** o Meu Pluggy é gratuito para uso pessoal, mas aceita só contas do mesmo CPF; uma conexão por titular respeita a regra e o consentimento do Open Finance, que é dado pelo titular. Sem webhook, não há rota pública nova (ADR-006). O mesmo par de credenciais também movimenta dinheiro na Pluggy; limitar o código à leitura reduz o estrago de um erro ou de uma chave vazada. Banco como fonte única nas contas ligadas elimina a principal causa de duplicidade.

**Descartado:**
- *Uma conta do Meu Pluggy para o casal* — o Meu Pluggy recusa contas de outro CPF.
- *Plano pago com webhook* — custo mensal alto para um casal e uma rota pública a mais.
- *Importação de arquivo (OFX/CSV)* — gratuita, mas manual; fica como alternativa se o Meu Pluggy deixar de servir.
- *Lançar à mão e conciliar depois (opção A)* — o casal preferiu a opção B: mais simples e sem duplicidade.

**Consequências:** o painel pode atrasar até um dia em relação ao banco; a conta conjunta entra por uma conexão só, com trava contra a segunda; renovar o consentimento é tarefa do titular, avisada 15 dias antes. Atualiza o ADR-007 (que previa webhook): a parte de webhook não se aplica ao Meu Pluggy. Spec: `docs/superpowers/specs/2026-11-11-fase-2-open-finance-design.md`.
