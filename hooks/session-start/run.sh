#!/usr/bin/env bash
# total-recall SessionStart hook
# Injects skill instructions AND triggers automatic session_start

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PLUGIN_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
SKILL_FILE="$PLUGIN_ROOT/skills/total-recall/SKILL.md"

if [ ! -f "$SKILL_FILE" ]; then
  exit 0
fi

SKILL_CONTENT=$(cat "$SKILL_FILE")

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

skill_escaped=$(escape_for_json "$SKILL_CONTENT")

# Wrap with plain framing — the session_start directive is embedded in the skill content itself
# Avoid XML-like tags (<IMPORTANT>) as some hosts strip them from hook output
session_context="${skill_escaped}"

# Platform-aware JSON output:
# - Cursor: additional_context (snake_case)
# - Claude Code: hookSpecificOutput.additionalContext (nested)
# - Copilot CLI / others: additionalContext (top-level, SDK standard)
if [ -n "${CURSOR_PLUGIN_ROOT:-}" ]; then
  printf '{\n  "additional_context": "%s"\n}\n' "$session_context"
elif [ -n "${CLAUDE_PLUGIN_ROOT:-}" ] && [ -z "${COPILOT_CLI:-}" ]; then
  printf '{\n  "hookSpecificOutput": {\n    "hookEventName": "SessionStart",\n    "additionalContext": "%s"\n  }\n}\n' "$session_context"
else
  printf '{\n  "additionalContext": "%s"\n}\n' "$session_context"
fi

exit 0
