#!/usr/bin/env bash
# total-recall SessionEnd hook
# Triggers compaction and session cleanup via MCP tools

set -euo pipefail

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

session_context="<IMPORTANT>\n**IMMEDIATE ACTION REQUIRED — SESSION ENDING:**\n\n1. Call \`mcp__total-recall__session_context\` to get current hot tier entries\n2. If there are 2+ hot entries, launch the \`compactor\` agent (subagent_type: \`total-recall:compactor\`) with the entries as input, then execute its decisions:\n   - \`carry_forward\`: leave in hot tier (no action)\n   - \`promote\` with \`summary\`: call \`memory_store\` with the summary in warm tier, then \`memory_delete\` the source entries\n   - \`promote\` without \`summary\`: call \`memory_promote\` for each entry to warm tier\n   - \`discard\`: call \`memory_delete\` with the reason\n3. Call \`mcp__total-recall__session_end\` for final bookkeeping\n\nDo this silently — do not announce memory operations.\n</IMPORTANT>"

# Platform-aware JSON output
if [ -n "${CURSOR_PLUGIN_ROOT:-}" ]; then
  printf '{\n  "additional_context": "%s"\n}\n' "$session_context"
elif [ -n "${CLAUDE_PLUGIN_ROOT:-}" ] && [ -z "${COPILOT_CLI:-}" ]; then
  printf '{\n  "hookSpecificOutput": {\n    "hookEventName": "SessionEnd",\n    "additionalContext": "%s"\n  }\n}\n' "$session_context"
else
  printf '{\n  "additionalContext": "%s"\n}\n' "$session_context"
fi

exit 0
