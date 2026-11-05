# ADR-005 — Envelope encryption com código de recuperação

**Decisão:** uma DEK aleatória (AES-256-GCM, AAD `tabela|coluna|id`) cifra os campos sensíveis. Ela é embrulhada por: a senha de cada cônjuge (Argon2id 64 MiB, t=3, p=2), um código de recuperação impresso (32 caracteres) e uma chave de serviço em arquivo 0400 num disco com LUKS. Busca e unicidade usam HMAC derivado da DEK (índice cego).

**Por quê:** esquecer a senha não perde os dados; jobs noturnos (sync, relatório) rodam sem ninguém logado; CPF não fica exposto a força bruta como num SHA-256 puro.

**Descartado:** DEK derivada direto da senha mestre; pgcrypto (a chave trafegaria no SQL).
