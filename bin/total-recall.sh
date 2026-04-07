#!/usr/bin/env bash
# total-recall MCP server launcher
# Requires Bun — dist/index.js uses bun:sqlite, which node cannot resolve.
# Prefers bundled Bun (installed by scripts/postinstall.js), falls back to system Bun.

BUN_VERSION="1.2.10"

find_bundled_bun() {
  local ext=""
  if [[ "$OSTYPE" == "msys" || "$OSTYPE" == "cygwin" || "$OSTYPE" == "win32" ]]; then
    ext=".exe"
  fi
  local bin="$HOME/.total-recall/bun/$BUN_VERSION/bun${ext}"
  if [ -x "$bin" ]; then
    echo "$bin"
    return 0
  fi
  return 1
}

find_system_bun() {
  if command -v bun &>/dev/null; then
    echo "bun"
    return 0
  fi
  return 1
}

# Find the package entry point
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PACKAGE_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
ENTRY="$PACKAGE_DIR/dist/index.js"

if [ ! -f "$ENTRY" ]; then
  echo "total-recall: error: could not find dist/index.js." >&2
  echo "  Run 'npm run build' (git clone) or 'npm install -g @strvmarv/total-recall'." >&2
  exit 1
fi

# Priority 1: bundled Bun
RUNTIME=$(find_bundled_bun)
if [ -n "$RUNTIME" ]; then
  exec "$RUNTIME" "$ENTRY" "$@"
fi

# Priority 2: system Bun (warn — version may not match)
RUNTIME=$(find_system_bun)
if [ -n "$RUNTIME" ]; then
  echo "total-recall: warning: bundled bun v$BUN_VERSION not found, using system bun. Version mismatch possible." >&2
  echo "  Re-run 'npm install' to download bun v$BUN_VERSION." >&2
  exec "$RUNTIME" "$ENTRY" "$@"
fi

echo "total-recall: error: bun runtime not found." >&2
echo "  Expected bundled bun at ~/.total-recall/bun/$BUN_VERSION/bun (installed by 'npm install')." >&2
echo "  Fix: run 'npm install' inside the plugin directory, or install bun manually (https://bun.sh/install)." >&2
exit 1
