# ADR-006 — Acesso pela internet via DuckDNS, com defesas em camadas

**Decisão:** o app é publicado em `seu-nome.duckdns.org`, porta 443 apenas, atrás do Caddy. Certificado Let's Encrypt pelo desafio DNS do DuckDNS (a porta 80 fica fechada). Postgres, gateway de WhatsApp e OCR nunca são publicados.

**Por quê:** o casal troca de celular com frequência e não quer depender de um app de VPN em cada aparelho.

**Camadas de proteção (obrigatórias antes de abrir a porta):**
- Login de cada cônjuge com passkey ou TOTP; sessão em cookie `HttpOnly`/`Secure`/`SameSite=Strict` (ADR-011); bloqueio após 5 falhas.
- Celular novo entra cadastrando uma passkey nova, aprovada pelo outro cônjuge ou pelo código de recuperação.
- Caddy com limite de requisições (10/min por IP em `/api/v1/auth/*`), cabeçalhos de segurança e CSP restrita.
- CrowdSec lendo o log do Caddy e bloqueio de IPs fora do Brasil (Fase 5).
- Roteador com só a 443 encaminhada; firewall do host (`ufw`) negando o resto.
- A 443 só é encaminhada no roteador depois que o login (passkey/TOTP) existir; até lá o cadastro é feito pela rede de casa.
- Cadastro do casal: no máximo 5 tentativas a cada 10 minutos por IP em `POST /api/v1/casal`.

**Pré-requisito:** o provedor precisa entregar IP público (sem CGNAT). Se não entregar, o plano B é um Cloudflare Tunnel apontando para o mesmo Caddy.

**Descartado:** Tailscale (tailnet) — é mais seguro, mas exige instalar e logar o app em cada celular novo.
