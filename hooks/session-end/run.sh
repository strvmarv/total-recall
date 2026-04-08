#!/usr/bin/env bash
# total-recall SessionEnd hook
# Injects the session-end directive fragment as additionalContext.
#
# The directive lives in skills/total-recall/session-end.md so it stays
# in one place — the same file can be referenced by the SKILL.md and is
# not duplicated in this shell script. If the fragment is missing we
# exit 0 silently rather than blocking session end.
#
# Payload is plain text (no <IMPORTANT>/XML wrappers) — it is already
# injected inside a system-reminder block by the host, and nested markup
# is redundant / inconsistently handled across hosts. Tool references in
# the fragment use functional names (e.g. "session_context MCP tool")
# instead of hardcoded mcp__ prefixes, because each host namespaces
# plugin MCP tools differently.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PLUGIN_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
FRAGMENT_FILE="$PLUGIN_ROOT/skills/total-recall/session-end.md"

if [ ! -f "$FRAGMENT_FILE" ]; then
  exit 0
fi

FRAGMENT_CONTENT=$(cat "$FRAGMENT_FILE")

# Escape string for JSON embedding using bash parameter substitution
escape_for_json() {
    local s="$1"
    s="${s//\\/\\\\}"
    s="${s//\"/\\\"}"
    s="${s//$'\n'/\\n}"
    s="${s//$'\r'/\\r}"
    s="${s//$'\t'/\\t}"
    printf '%s' "$s"
}

session_context=$(escape_for_json "$FRAGMENT_CONTENT")

# Platform-aware JSON output:
# - Cursor: additional_context (snake_case) — note: Cursor 1.7 has no
#   SessionEnd hook, so this branch is unreachable today but kept for
#   forward compatibility
# - Claude Code: hookSpecificOutput.additionalContext (nested)
# - Copilot CLI / others: additionalContext (top-level, SDK standard)
if [ -n "${CURSOR_PLUGIN_ROOT:-}" ]; then
  printf '{\n  "additional_context": "%s"\n}\n' "$session_context"
elif [ -n "${CLAUDE_PLUGIN_ROOT:-}" ] && [ -z "${COPILOT_CLI:-}" ]; then
  printf '{\n  "hookSpecificOutput": {\n    "hookEventName": "SessionEnd",\n    "additionalContext": "%s"\n  }\n}\n' "$session_context"
else
  printf '{\n  "additionalContext": "%s"\n}\n' "$session_context"
fi

exit 0
