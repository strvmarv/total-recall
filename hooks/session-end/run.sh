#!/usr/bin/env bash
# total-recall SessionEnd hook.
#
# Emits a USER-FACING compaction nudge (systemMessage) when the hot tier has
# accumulated uncompacted entries. Does NOT inject any model directive:
# SessionEnd fires while the session is terminating, so there is no model turn
# to act on injected context, and Claude Code's SessionEnd schema rejects
# hookSpecificOutput/additionalContext outright.
#
# All logic lives in the CLI (`session-end-hint`); this wrapper only forwards
# the host flag and must NEVER fail session teardown (exit 0, "{}" on error).
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PLUGIN_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

host="claude-code"
if [ -n "${CURSOR_PLUGIN_ROOT:-}" ]; then
  host="cursor"
elif [ -n "${COPILOT_CLI:-}" ]; then
  host="copilot-cli"
fi

if ! output=$(node "$PLUGIN_ROOT/bin/start.js" session-end-hint --host "$host" 2>/dev/null) || [ -z "$output" ]; then
  printf '{}\n'
else
  printf '%s\n' "$output"
fi
exit 0
