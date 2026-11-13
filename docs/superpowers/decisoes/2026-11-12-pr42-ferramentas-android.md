# Decisões do CEO: PR #42, ferramentas do app Android

- Data: 2026-11-12
- Branch: `feat/app-login-animacoes`
- PR: #42, "chore(android): ferramentas do app"
- Quem decide: o CEO. Quem revisou: o `revisor-seguranca`, em três rodadas.

Regra de corte, a partir da 2ª rodada: só um achado **Bloqueia** segura o PR. Um achado Importante ou uma Sugestão nova vira issue, a não ser que seja regressão do próprio PR.

## Decisões permanentes

- **O "PO do app" é o CEO nesta frente.** Não existe papel novo; vale a hierarquia da ADR-012. Registrado em `.claude/rules/android.md`.
- **A revisão cruzada do app é feita pela sessão Claude QA** (worktree `~/Gestor-Financeiro-qa`), não pelo Antigravity. Registrado em `.claude/rules/android.md`.
- **A keystore de release nunca fica nesta máquina.** O build local não assina release. A ADR do app (E1) registra isso; por enquanto a nota está em `android/README.md`.

## 1ª rodada (commits 7112bfb e 155e264): 17 achados

Todos foram aceitos, exceto o 11, que foi adiado. As correções estão nos commits `021f9aa`, `36d387b` e `24af1e7`.

| # | Gravidade | Achado | Decisão | Por quê |
|---|---|---|---|---|
| 1 | Bloqueia | `Bash(adb:*)` dava controle sem pergunta sobre um aparelho de verdade | Aceito: só `adb devices`, `adb install -r android/app/build/outputs/…` e `adb logcat --pid` | O tablet tem dados reais; deny por prefixo não basta |
| 2 | Bloqueia | O grep de senha da skill `android-verificar` imprimia o segredo | Aceito: só `-l` e só em arquivo rastreado | Regra 5 |
| 3 | Importante | `./gradlew:*` rodava qualquer código (`-I`, `--scan`, `properties`, `signingReport`) | Aceito: 10 tarefas nomeadas e hook que barra as opções perigosas. **Parte do wrapper (`distributionSha256Sum` e validação no CI): adiada para a E4 (#43)** | Ainda não existe wrapper em `android/` |
| 4 | Importante | `sdkmanager:*` instalava sem revisão | Aceito: só `--list` e `--list_installed` | Cadeia de fornecimento |
| 5 | Importante | O deny de keystore tinha buracos (Bash, fora do projeto, formatos, commit) | Aceito: `eh_caminho_de_segredo` cobre os formatos, `~/.android`, `~/.gradle/gradle.properties` e a conta de serviço; vale no Bash, no pre-commit e no CI | Regra 5 |
| 6 | Importante | As regras do app ficavam no arquivo do agente | Aceito: movidas para `.claude/rules/android.md` | AGENTS.md: agente não guarda regra |
| 7 | Importante | O fluxo dos agentes contradizia o AGENTS.md (papel "PO do app", PR) | Aceito: o PO é o CEO; PR `--draft`, `gh pr view`, merge só com sinal do CEO | ADR-012 |
| 8 | Importante | O agente apontava para documentos inexistentes e parafraseava a ADR-011 | Aceito: só a remissão à ADR-011; o valor do cabeçalho fica para a spec da E1 | Fonte única |
| 9 | Importante | O `android-revisor` tinha Bash | Aceito: só Read, Grep e Glob | Revisor só lê |
| 10 | Importante | A busca de log não pegava o log dos frameworks | Aceito: HttpLoggingInterceptor (BODY/HEADERS), `redactHeader`, `printStackTrace`, Timber | Regra 5 |
| 11 | Importante | `verificar.sh` (hook `Stop` e CI) não testa `android/` | **Adiado para a E4 (#43)** | Ainda não há código em `android/`; tem de entrar antes do primeiro commit com código |
| 12 | Sugestão | `git diff` da skill dava falso positivo; a regra 8 não citava o cliente Android | Aceito: `git status` só na pasta do cliente gerado; a regra 8 cobre os dois clientes | Um PR de servidor não pode quebrar o app sem ninguém ver |
| 13 | Sugestão | `emulator:*` e `avdmanager:*` eram largos demais | Aceito: só `-avd gf-pixel-api36`, `-accel-check`, `-list-avds` e `avdmanager list` | Menor permissão |
| 14 | Sugestão | A skill não checava `cleartextTrafficPermitted` nem `allowBackup` | Aceito | Regra 5 |
| 15 | Sugestão | O README expunha dados da máquina; `local.properties` estava fora do `.gitignore` | Aceito: "sem KVM: testes no aparelho"; `local.properties` no `.gitignore` | A máquina é a mesma que roda o sistema |
| 16 | Sugestão | A linha `Claude-Session` estava na mensagem do commit 7112bfb | Aceito: tirada no rebase | Não vai para o histórico do `main` |
| 17 | Sugestão | As gravidades do `android-revisor` fugiam do modelo, e ele não conferia o HEAD | Aceito | `modelo-de-revisao.md` |

## 2ª rodada (topo `24af1e7`): 1 bloqueia, 3 importantes, 7 sugestões

| # | Gravidade | Achado | Decisão | Por quê |
|---|---|---|---|---|
| 1 | Bloqueia | O caminho de falha do hook Bash (JSON malformado, sem jq) não tinha teste | Aceito: `23b5343` | Sem o teste, uma mudança deixaria tudo passar sem nenhum teste quebrar |
| 2 | Importante | O gradlew escapava (abreviação, aspas, `gradle` do sistema) | Aceito: lista positiva de tarefas e opções, `889615a` | O hook existe para quando alguém aprova sem ler |
| 3 | Importante | A leitura de segredo pelo Bash escapava (glob, `cd ~/.gradle`, `\`) | Aceito: as três correções, `889615a` | Regra 5 |
| 4 | Importante | Falsos positivos em `-m`/`--body` | Aceito, opção A: mensagem e corpo sempre por arquivo; `.x` do jq livre; regra do gradle só no trecho dele (`889615a`) | Não abre vetor no hook |
| 5 | Sugestão | O pipe do pre-commit pode dar falso negativo com SIGPIPE | Adiado para a #40 | Regra de corte |
| 6 | Sugestão | Variantes `_` do nome da conta de serviço no deny e no `.gitignore` | Adiado para a #40 | Regra de corte |
| 7 | Sugestão | AGENTS.md sem keystore nem o hook `guardar-bash.sh` | Adiado para a #40 | Regra de corte |
| 8 | Sugestão | Parêntese que resumia a ADR-011 | Aceito: só a remissão (`889615a`) | Fonte única |
| 9 | Sugestão | O README diz que as guardas barram `local.properties`, mas elas não barram | Adiado para a #40 | Regra de corte |
| 10 | Sugestão | A dependência do `jq` não está documentada | Adiado para a #40 | Regra de corte |
| 11 | Sugestão | `adb install …/outputs/../` e atrito da skill com as permissões exatas | Adiado para a #40 | Atrito, não falha de segurança |

## 3ª rodada (`24af1e7..889615a`): 0 bloqueia, 5 importantes, 3 sugestões

| # | Gravidade | Achado | Decisão | Por quê |
|---|---|---|---|---|
| 1 | Importante | O gradle ainda escapa (continuação de linha, `{ }`, prefixo com opção, `xargs`) | Adiado para a #41 | Regra de corte |
| 2 | Importante | Atribuições `GRADLE_*`/`JAVA_*` antes do gradle passam | Adiado para a #41 | Defesa em profundidade; o agente pode editar o `build.gradle.kts` |
| 3 | Importante | Glob sem "." no segmento não é checado; faltava `.gradle` nos candidatos | Adiado para a #41 | Regra de corte |
| 4 | Importante | A exceção "palavra que começa com '.'" ficou larga demais (`.keystore`, conta de serviço) | **Aceito neste PR:** `12d0b98`, com dois testes | Regressão do próprio PR |
| 5 | Importante | Falso positivo do `*.gradle*` em comandos comuns | Adiado para a #41. Nota do CEO: glob que começa com "." (`.gr*`, `.k*`) é barrado; glob de extensão sem ponto inicial (`*.gradle.kts`) passa | Regra de corte |
| 6 | Sugestão | O teste do `cd ~/.gradle` com glob não depende do `cd` | Adiado para a #41 | Regra de corte |
| 7 | Sugestão | O cabeçalho do `guardar-bash.sh` descrevia a lista negra antiga | Aceito: `12d0b98` | Pedido junto com o achado 4 |
| 8 | Sugestão | A guarda não falha fechada em erro interno nem no bash 3 | Adiado para a #41, **com prioridade alta** (nota do CEO) | Regra de corte |

## Para onde foi cada pendência

- **#40**: sugestões 5, 6, 7, 9, 10 e 11 da 2ª rodada.
- **#41**: achados 1, 2, 3 e 5 e sugestões 6 e 8 da 3ª rodada.
- **E4** (#43, "E4: verificar.sh e hook Stop testam android/; wrapper com distributionSha256Sum"): achado 11 da 1ª rodada e a parte do wrapper do achado 3 da 1ª rodada.
