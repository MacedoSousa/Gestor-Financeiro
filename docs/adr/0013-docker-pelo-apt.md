# ADR-013 — Docker pelo apt do Ubuntu, não pelo snap

**Decisão:** o Docker do servidor e das máquinas de desenvolvimento é o do apt do Ubuntu (`docker.io`, `docker-compose-v2`, `docker-buildx`). O Docker do snap não é suportado, e o `scripts/verificar.sh` avisa quando o encontra.

**Por quê:** em 10/11/2026, o Docker do snap passou a ser negado pelo AppArmor (`file_inherit`) ao herdar arquivos do shell. Com isso, `docker exec ... < arquivo` entrega a entrada vazia e `pg_dump > arquivo` grava 0 bytes, inclusive num terminal comum. O backup geraria dump vazio todo dia. O snap também confina o acesso a disco, e o projeto usa `/opt/gestor-financeiro`, `/run` e `/var/backups`. Pelo apt, o Docker recebe as correções de segurança com o `unattended-upgrades`, sem configuração extra.

**Descartado:**
- *Docker do snap* — o confinamento quebra redirecionamentos de arquivo e limita os caminhos.
- *Repositório oficial do Docker (`docker-ce`)* — versões mais novas, mas fora da origem que o `unattended-upgrades` atualiza por padrão.
- *Podman* — compatibilidade com Testcontainers e com o compose exigiria trabalho que não traz ganho aqui.
- *Trocar redirecionamentos por pipe nos scripts* — esconderia o defeito da instalação em vez de corrigi-lo.

## Detalhes

**Troca do snap pelo apt:** parar os containers; copiar os volumes que importam de `/var/snap/docker/common/var-lib-docker/volumes` com `tar` (o aviso "soquete ignorado" é normal); `sudo snap remove --purge docker` (sem `--purge`, o snapshot automático pode travar num soquete); `sudo apt install docker.io docker-compose-v2 docker-buildx`; `sudo usermod -aG docker $USER` e entrar de novo na sessão.

**Armadilha do soquete:** se o `docker` responder "no such file or directory" para `/var/run/docker.sock` com o serviço ativo, a remoção do snap apagou o arquivo do soquete. Basta `sudo systemctl stop docker.service docker.socket && sudo systemctl start docker.socket docker.service`; só o restart do `docker.service` não recria o arquivo.

**Os dados do projeto não dependem da instalação:** o compose usa bind mount em `infra/data/`, que fica no disco em qualquer Docker.
