# ADR-011 — Sessão em cookie (BFF)

**Decisão:** login cria uma sessão no servidor; o navegador guarda só um cookie `HttpOnly`, `Secure`, `SameSite=Strict`. Cada cônjuge tem login próprio com passkey ou TOTP.

**Por quê:** nenhum token fica acessível ao JavaScript; front e API estão na mesma origem atrás do Caddy.

**Descartado:** JWT guardado no navegador.

## Detalhes

**Tempos** (constantes em `TemposDoLogin`):
- Sessão: expira com 7 dias sem uso e, de qualquer forma, 30 dias depois de criada. `sessoes.ultimo_uso_em` só é regravado quando tem mais de 1 h, para não inundar a trilha de auditoria.
- Confirmação recente: 5 min (passkey ou TOTP via `POST /api/v1/auth/confirmar`). Exigem confirmação: aprovar celular novo, destravar o cônjuge, gerar código de recuperação, autorizar/remover credencial.
- Pedido de celular novo, autorização de registro: 10 min. Desafio WebAuthn: 5 min (em `IMemoryCache`; um restart os invalida).

**Cookies:** `__Host-gf_sessao`, `__Host-gf_pedido` e `__Host-gf_registro`, todos `HttpOnly`, `Secure`, `SameSite=Strict`, `Path=/`, sem `Domain`. O servidor guarda só o HMAC dos tokens (`ICofre.IndiceCego`), nunca o token, IP ou user agent.

**Bloqueio progressivo:** 5 falhas seguidas bloqueiam por 15 min; cada novo bloqueio dobra o tempo, até 24 h; um login certo zera falhas e contagem. O outro cônjuge, logado e com confirmação recente, destrava na hora. Contam como falha: código TOTP errado, senha errada no primeiro acesso, código de recuperação errado, assinatura de passkey inválida de credencial conhecida e falhas de atestação/confirmação no registro de credencial. As tentativas são serializadas por cônjuge com lock consultivo (`identidade.login:{usuarioId}`).

**Primeiro acesso:** CPF + senha do cadastro emite uma autorização de registro. Fecha para sempre quando o cônjuge confirma o primeiro dispositivo (`usuarios.primeiro_acesso_encerrado_em`); depois disso responde 403 sem testar a senha, mesmo que ele remova todas as credenciais. A regra olha o marcador, não a contagem de credenciais. Não se remove a última credencial ativa e confirmada (409), checado sob o lock do cônjuge.

**Remover credencial encerra as outras sessões:** ao remover uma credencial (`DELETE /api/v1/dispositivos/credenciais/{id}`), na mesma transação e sob o mesmo lock do cônjuge, todas as outras sessões dele são encerradas (soft delete); só a sessão que fez a remoção continua. Remover credencial costuma ser tirar um celular perdido ou roubado, e a sessão aberta nele não pode continuar valendo. As sessões do outro cônjuge não mudam.

**Celular novo:** o celular informa o CPF e recebe um código de 6 dígitos; o cônjuge logado confere e aprova (o dono não aprova o próprio pedido). O celular consulta o estado com um GET sem efeito colateral (limite próprio de 60/min por IP no Caddy) e, aprovado, resgata a autorização de registro por um POST separado.

**Recuperação:** CPF + código de recuperação dá a autorização de registro e gira o código na hora (ADR-005). Um cônjuge logado, com confirmação recente, também pode gerar código novo (`POST /api/v1/casal/codigo-recuperacao`).

**CSRF:** `SameSite=Strict` mais o cabeçalho `X-Requested-With: gestor-web`, obrigatório em todo método que não seja GET/HEAD; sem ele, 403.

**Risco aceito:** quem já conhece um CPF consegue distinguir se ele existe pelas respostas 403 (primeiro acesso já configurado) e 423 (conta bloqueada), que não aparecem para CPF inexistente. Aceito porque é um sistema de casal, o CPF não é segredo para quem o conhece e o ganho de esconder isso não compensa a perda de clareza para o usuário.

**Risco aceito (falhas no registro):** falhas de atestação e de confirmação no registro de credencial contam para o bloqueio. Quem tiver um cookie de autorização de registro roubado (vale 10 min) pode, errando de propósito, bloquear o cônjuge por um tempo; o bloqueio é temporário e o outro cônjuge pode destravar.

**Risco aceito (travar o cônjuge pelo CPF):** quem sabe um CPF (no Brasil, quase público) consegue manter o cônjuge bloqueado, inclusive para entrar com passkey, errando 5 vezes o código de recuperação ou o TOTP dentro do limite do Caddy (10/min por IP). Se os dois cônjuges forem bloqueados, nenhum destrava o outro até o bloqueio expirar (até 24 h). Aceito por ora: o atacante não ganha acesso, só nega serviço, e a política para passkey fica para a decisão abaixo.

## Antes de abrir a porta 443

Lista de conferência; nenhum item abre a porta sozinho (ver também ADR-006, "Camadas de proteção"):

- [ ] **Bloqueio e passkey (decisão do casal):** decidir se a passkey fica fora do bloqueio contado por falhas de recuperação/TOTP (fecha o risco acima) ou se continua sujeita a ele.
- [ ] **Pedidos de celular novo:** limitar quantos pedidos pendentes cada cônjuge pode ter ao mesmo tempo.
- [x] **WebAuthn na subida:** `Seguranca:WebAuthn:RpId`/`Origens` conferidos ao subir (`ValidateOnStart`); configuração errada derruba o boot em vez de dar 500 na primeira passkey.
- [ ] **Sessões ativas:** revisar as sessões ativas de cada usuário (quantas, de quando) e encerrar as que não forem reconhecidas.
