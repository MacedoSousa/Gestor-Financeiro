#!/usr/bin/env bash
# Liga os hooks versionados em .githooks/ (uma vez por clone).
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
git config core.hooksPath .githooks
chmod +x .githooks/* scripts/*.sh scripts/guardas/*.sh .claude/hooks/*.sh
echo "Hooks ligados: core.hooksPath = $(git config core.hooksPath)"
