# ADR-005 — Envelope encryption com código de recuperação

**Decisão:** uma DEK aleatória (AES-256-GCM, AAD `tabela|coluna|id`) cifra os campos sensíveis. Ela é embrulhada por: a senha de cada cônjuge (Argon2id 64 MiB, t=3, p=2), um código de recuperação impresso (32 caracteres, gerado no cadastro do casal e guardado só como envelope em `identidade.chaves`) e uma chave de serviço em arquivo 0400 num disco com LUKS. Busca e unicidade usam HMAC derivado da DEK (índice cego).

**Código de instalação:** o cadastro do casal não tem sessão, então é protegido por um código de uso único (`--codigo-instalacao`), válido por 24 h. Só o SHA-256 e a validade ficam num arquivo 0400; é consumido depois do commit do cadastro. Errado ou expirado: `403`.

**Rotação do código de recuperação:** usar o código de recuperação o gira na hora (o envelope antigo é marcado como removido e o novo é gravado na mesma transação), e um cônjuge logado, com confirmação recente, pode gerar um código novo (`POST /api/v1/casal/codigo-recuperacao`). O envelope é do casal, então a rotação toma o lock consultivo `identidade.recuperacao:{casalId}`. Ordem dos locks, sempre a mesma para evitar deadlock: primeiro o do cônjuge (`identidade.login:{usuarioId}`), depois o do casal.

**Forma canônica dos códigos:** a KEK de recuperação é derivada, e o código de instalação é conferido, a partir da forma canônica do código (`EnvelopeDeChaves.CanonizarCodigo`): maiúsculas e só A–Z e 0–9, sem hífens nem espaços. O código é exibido em grupos com hífen, mas qualquer fluxo de recuperação deve canonizar o que o usuário digitar do mesmo jeito antes de derivar a KEK.

**Por quê:** esquecer a senha não perde os dados; jobs noturnos (sync, relatório) rodam sem ninguém logado; CPF não fica exposto a força bruta como num SHA-256 puro.

**Descartado:** DEK derivada direto da senha mestre; pgcrypto (a chave trafegaria no SQL).
