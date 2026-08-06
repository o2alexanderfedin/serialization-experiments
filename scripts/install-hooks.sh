#!/usr/bin/env bash
# Point git at the versioned hooks in .githooks/ so every clone enforces git-flow.
set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel)"
cd "$REPO_ROOT"

chmod +x .githooks/* 2>/dev/null || true
git config core.hooksPath .githooks

echo "✅ core.hooksPath -> .githooks"
echo "   Active hooks:"
for hook in .githooks/*; do
  [ -f "$hook" ] && echo "     - $(basename "$hook")"
done
echo ""
echo "🔒 Protected branches: main, develop (commit + push blocked)"
echo "✨ Use: git flow feature start <name>"
