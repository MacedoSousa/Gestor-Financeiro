# ADR-004 — Auditoria por trigger, encadeada por hash

**Decisão:** `audit.registrar()` (SECURITY DEFINER) grava todo INSERT/UPDATE em `audit.eventos`, append-only, com autor e IP vindos de `set_config('app.*')`. Cada evento guarda `hash = sha256(hash_anterior ‖ conteúdo)` por casal. Colunas cifradas entram só como "alterado".

**Por quê:** nenhuma escrita escapa — nem script manual nem bug no ORM. A aplicação (`gf_app`) não consegue escrever nem apagar auditoria; adulteração por superusuário é detectada por `audit.verificar_cadeia`.

**Descartado:** interceptor do EF (o rascunho tinha bugs: soft delete registrado como "Modified", sem autor/IP, CPF podendo ir para o JSON).
