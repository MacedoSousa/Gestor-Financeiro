# ADR-005 — Envelope encryption com código de recuperação

**Decisão:** uma DEK aleatória (AES-256-GCM, AAD `tabela|coluna|id`) cifra os campos sensíveis. Ela é embrulhada por: a senha de cada cônjuge (Argon2id 64 MiB, t=3, p=2), um código de recuperação impresso (32 caracteres, gerado no cadastro do casal e guardado só como envelope em `identidade.chaves`) e uma chave de serviço em arquivo 0400 num disco com LUKS. Busca e unicidade usam HMAC derivado da DEK (índice cego).

**Código de instalação:** o cadastro do casal não tem sessão, então é protegido por um código de uso único (`--codigo-instalacao`), válido por 24 h. Só o SHA-256 e a validade ficam num arquivo 0400; é consumido depois do commit do cadastro. Errado ou expirado: `403`.

**Rotação do código de recuperação:** usar o código de recuperação o gira na hora (o envelope antigo é marcado como removido e o novo é gravado na mesma transação), e um cônjuge logado, com confirmação recente, pode gerar um código novo (`POST /api/v1/casal/codigo-recuperacao`). O envelope é do casal, então a rotação toma o lock consultivo `identidade.recuperacao:{casalId}`. Ordem dos locks, sempre a mesma para evitar deadlock: primeiro o do cônjuge (`identidade.login:{usuarioId}`), depois o do casal.

**Forma canônica dos códigos:** a KEK de recuperação é derivada, e o código de instalação é conferido, a partir da forma canônica do código (`EnvelopeDeChaves.CanonizarCodigo`): maiúsculas e só A–Z e 0–9, sem hífens nem espaços. O código é exibido em grupos com hífen, mas qualquer fluxo de recuperação deve canonizar o que o usuário digitar do mesmo jeito antes de derivar a KEK.

**Por quê:** esquecer a senha não perde os dados; jobs noturnos (sync, relatório) rodam sem ninguém logado; CPF não fica exposto a força bruta como num SHA-256 puro.

**Descartado:** DEK derivada direto da senha mestre; pgcrypto (a chave trafegaria no SQL).

## Backup e chaves

**Conteúdo do pacote diário** (`gestor-AAAA-MM-DD.tar.age`): o dump do banco (`pg_dump -Fc`), os segredos de `infra/secrets/`, a `dek.servico`, um `MANIFESTO` (contagens e versões), o `SHA256SUMS` de tudo e o `cadeia.txt` (último evento da auditoria de cada casal). Plano: `docs/superpowers/plans/2026-11-10-backup.md`.

**A chave privada do age é, na prática, a chave-mestra.** Como o pacote leva a chave de serviço e a `dek.servico`, quem tem a chave privada do age abre todos os dados. Por isso:
- o pacote é cifrado para **dois destinatários**, um por cônjuge, e qualquer um dos dois restaura sozinho;
- as chaves privadas nunca ficam no PC: impressas em papel e num gerenciador de senhas;
- o servidor só tem as chaves públicas (`/etc/gestor-backup/destinatarios.txt`), e o backup aborta se encontrar uma linha `AGE-SECRET-KEY` ali.

**Verificar antes de cifrar.** Sem a chave privada, o servidor só consegue testar o dump antes de cifrar: ele restaura o dump num Postgres descartável, confere as contagens e a cadeia de auditoria e só então cifra. O dump em claro só existe no `RuntimeDirectory` do systemd (memória, apagado no fim da unidade).

**Âncora externa da cadeia.** O `cadeia-<data>.txt` também sai em claro, fora do pacote, ao lado de cada pacote nos destinos externos (pendrive e Google Drive). Só tem UUIDs, `seq` e hashes, nenhum dado pessoal, e não é rotacionado. Antes de cada backup, o script confere que a última âncora ainda existe na auditoria: isso detecta uma cadeia reescrita e recalculada por um superusuário, coisa que a verificação da cadeia sozinha não pega.

**Destinos:** local (7 diários, 4 semanais), pendrive opcional e Google Drive pelo rclone, obrigatório, com o escopo `drive.file` (o rclone só enxerga o que ele mesmo criou). Os pacotes já saem cifrados, então o provedor não lê nada, e não se usa o `crypt` do rclone.

**Descartado:**
- *`pgcrypto` ou `gpg -c` com senha guardada no servidor* — quem invade o servidor leva a senha junto com os dados.
- *Chave privada no servidor para testar a decifragem* — a mesma coisa; o teste acontece antes de cifrar e no ensaio mensal de restauração (`restaurar.sh --verificar`), feito fora do servidor ou com a chave trazida só para o ensaio.
