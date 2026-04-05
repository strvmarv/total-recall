#!/usr/bin/env bash
# total-recall SessionStart hook
# Injects the core memory skill into the session

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PLUGIN_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
SKILL_FILE="$PLUGIN_ROOT/skills/total-recall/SKILL.md"

if [ -f "$SKILL_FILE" ]; then
  cat "$SKILL_FILE"
fi
