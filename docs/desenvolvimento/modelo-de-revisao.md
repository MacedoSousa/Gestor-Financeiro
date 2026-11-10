# Modelo de revisão

Copie para `.revisoes/<branch>.md` (a pasta fica fora do git). Quem revisa preenche tudo menos "Resposta do Claude"; o Claude Code responde nessa coluna e nunca apaga o texto do revisor.

```markdown
# Revisão — <branch> @ <hash curto>

- Revisor: Antigravity (<modelo>) · <data>
- Escopo: `git diff main...<hash>` · spec: docs/superpowers/specs/<arquivo> · plano: tarefa <n>
- `scripts/verificar.sh`: <verde | falhou — cole o trecho>

## Achados

| # | Gravidade | Onde | Achado | Regra / spec | Resposta do Claude |
| - | --------- | ---- | ------ | ------------ | ------------------ |
| 1 | Bloqueia | backend/src/Modules/X/Y.cs:42 | <o que está errado e como reproduzir> | CLAUDE.md regra 4 | |
| 2 | Importante | ... | ... | spec §3 | |
| 3 | Sugestão | ... | ... | — | |

## Navegador

Tela · largura · resultado. Só dados sintéticos; capturas fora do git.

| # | Gravidade | Tela | Achado | Referência | Resposta do Claude |
| - | --------- | ---- | ------ | ---------- | ------------------ |

## Resumo

<n> bloqueia · <n> importantes · <n> sugestões. O branch <pode | não pode> seguir para PR.
```

**Gravidade:** *Bloqueia* = viola regra do `CLAUDE.md`, perde ou corrompe dado, número errado, falha de segurança, teste faltando para comportamento novo. *Importante* = diverge da spec ou do design, caso de borda sem tratamento. *Sugestão* = legibilidade, nome, simplificação.
