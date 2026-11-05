# ADR-002 — Monólito modular, um schema por módulo

**Decisão:** um processo, nove módulos. Cada módulo é um projeto com schema próprio no Postgres; só o namespace `Contracts` e a classe `<X>Module` são públicos.

**Por quê:** um único deploy no PC de casa, com fronteiras que o compilador (tipos `internal`) e os testes de arquitetura garantem.

**Descartado:** microsserviços — custo operacional sem benefício para dois usuários.
