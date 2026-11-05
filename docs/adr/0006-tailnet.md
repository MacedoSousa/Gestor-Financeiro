# ADR-006 — App na tailnet, só o webhook é público

**Decisão:** o Caddy publica a porta 443 apenas no IP do Tailscale. O único caminho público é `POST /webhooks/pluggy`, num listener separado (127.0.0.1:8081) exposto pelo Tailscale Funnel.

**Por quê:** menor superfície de ataque possível; muitos provedores brasileiros usam CGNAT, o que inviabiliza port forwarding.

**Descartado:** expor tudo via DuckDNS + port forwarding. Continua possível como plano B para o webhook, se a Pluggy não aceitar o domínio `*.ts.net`. Sem webhook, a sincronização vira polling e a exposição pública vai a zero.
