#!/usr/bin/env bash
# total-recall MCP server launcher
# Finds node in common locations even when not in PATH

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

  echo ""
  return 1
}

NODE=$(find_node)
if [ -z "$NODE" ]; then
  echo "total-recall: error: node.js not found. Install Node.js 20+ via nvm, fnm, Volta, or your package manager." >&2
  exit 1
fi

# Find the package entry point
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PACKAGE_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
ENTRY="$PACKAGE_DIR/dist/index.js"

# Strategy 1: Local dist/index.js (source install or npm install)
if [ -f "$ENTRY" ]; then
  exec "$NODE" "$ENTRY" "$@"
fi

# Strategy 2: npx (downloads and runs the npm package)
NODE_DIR="$(dirname "$NODE")"
NPX="$NODE_DIR/npx"
if [ -x "$NPX" ]; then
  exec "$NPX" -y @strvmarv/total-recall "$@"
fi

echo "total-recall: error: could not find dist/index.js or npx. Run 'npm run build' or 'npm install -g @strvmarv/total-recall'." >&2
exit 1
