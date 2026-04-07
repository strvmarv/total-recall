#!/usr/bin/env bash
# total-recall MCP server launcher
# Prefers bundled Bun, falls back to system Bun, then system Node.

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

find_node() {
  # Check PATH first
  if command -v node &>/dev/null; then
    echo "node"
    return 0
  fi

  # Check nvm
  if [ -d "$HOME/.nvm/versions/node" ]; then
    local latest=$(ls -1d "$HOME/.nvm/versions/node"/v* 2>/dev/null | sort -V | tail -1)
    if [ -n "$latest" ] && [ -x "$latest/bin/node" ]; then
      echo "$latest/bin/node"
      return 0
    fi
  fi

  # Check fnm
  if [ -d "$HOME/.local/share/fnm/node-versions" ]; then
    local latest=$(ls -1d "$HOME/.local/share/fnm/node-versions"/v*/installation 2>/dev/null | sort -V | tail -1)
    if [ -n "$latest" ] && [ -x "$latest/bin/node" ]; then
      echo "$latest/bin/node"
      return 0
    fi
  fi

  # Check Homebrew
  if [ -x "/home/linuxbrew/.linuxbrew/bin/node" ]; then
    echo "/home/linuxbrew/.linuxbrew/bin/node"
    return 0
  fi
  if [ -x "/opt/homebrew/bin/node" ]; then
    echo "/opt/homebrew/bin/node"
    return 0
  fi
  if [ -x "/usr/local/bin/node" ]; then
    echo "/usr/local/bin/node"
    return 0
  fi

  # Check Volta
  if [ -x "$HOME/.volta/bin/node" ]; then
    echo "$HOME/.volta/bin/node"
    return 0
  fi

  # Check nvm4w (Windows nvm — Git Bash exposes C:\ as /c/)
  if [ -x "/c/nvm4w/nodejs/node" ]; then
    echo "/c/nvm4w/nodejs/node"
    return 0
  fi

  # Check MacPorts
  if [ -x "/opt/local/bin/node" ]; then
    echo "/opt/local/bin/node"
    return 0
  fi

  echo ""
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

# Priority 2: system Bun (warn)
RUNTIME=$(find_system_bun)
if [ -n "$RUNTIME" ]; then
  echo "total-recall: warning: bundled bun v$BUN_VERSION not found, using system bun. Version mismatch possible." >&2
  echo "  Re-run 'npm install' to download bun v$BUN_VERSION." >&2
  exec "$RUNTIME" "$ENTRY" "$@"
fi

# Priority 3: system Node (warn)
RUNTIME=$(find_node)
if [ -n "$RUNTIME" ]; then
  echo "total-recall: warning: bun not found, falling back to node. Native addon ABI issues may occur." >&2
  echo "  Install bun (https://bun.sh/install) or re-run 'npm install' to fix this." >&2
  exec "$RUNTIME" "$ENTRY" "$@"
fi

echo "total-recall: error: neither bun nor node found." >&2
echo "  Install bun: https://bun.sh/install" >&2
exit 1
