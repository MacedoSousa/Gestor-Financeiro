# ADR-011 — Sessão em cookie (BFF)

**Decisão:** login cria uma sessão no servidor; o navegador guarda só um cookie `HttpOnly`, `Secure`, `SameSite=Strict`. Cada cônjuge tem login próprio com passkey ou TOTP.

**Por quê:** nenhum token fica acessível ao JavaScript; front e API estão na mesma origem atrás do Caddy.

**Descartado:** JWT guardado no navegador.
