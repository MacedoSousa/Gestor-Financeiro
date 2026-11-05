# ADR-008 — Canal de mensagens atrás de uma porta

**Decisão:** o módulo Integracoes define uma interface de canal (receber mensagem, baixar mídia, responder). A primeira implementação é a Evolution API num chip dedicado, lendo o grupo do casal.

**Por quê:** gateways não oficiais violam os termos do WhatsApp e o número pode ser banido. Com a porta, dá para trocar por WhatsApp Cloud API em conversa 1:1 ou por Telegram sem mexer no domínio.

**Regras:** nunca usar endpoints de envio em massa ou de checagem de números; o gateway só existe na rede Docker interna; a mensagem é idempotente por `(canal, message_id)`.
