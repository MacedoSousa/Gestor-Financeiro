# ADR-007 — Meu Pluggy para Open Finance; DDA manual

**Decisão:** dados bancários entram pelo Meu Pluggy (gratuito, uso pessoal, até 5 conexões, atualização a cada 24 h, autorização renovada à mão a cada ano). Boletos do DDA são copiados do app do Itaú ou enviados como foto no WhatsApp.

**Por quê:** pessoa física não acessa o Open Finance direto; os planos pagos custam milhares de reais por mês; APIs de DDA (Celcoin, QI Tech, Tecnospeed) são B2B. `GET /bills` da Pluggy são faturas de cartão, não boletos.

**Consequências:** tratar `USER_AUTHORIZATION_EXPIRED`, avisar 15 dias antes de `consentExpiresAt`; a Pluggy não assina webhooks com HMAC — validar header secreto, IP e `clientId` e sempre rebuscar os dados pela API.

**Atualizado pelo ADR-015 (11/11/2026):** uma conexão por cônjuge, só leitura, sincronização diária sem webhook; a parte de webhook acima não se aplica ao Meu Pluggy.
