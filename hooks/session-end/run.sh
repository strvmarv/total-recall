#!/usr/bin/env bash
# total-recall SessionEnd hook
# Triggers compaction and session cleanup via MCP tools.
#
# Note: the payload is plain text (no <IMPORTANT>/XML wrappers) — it is
# already injected inside a system-reminder block by the host, and nested
# markup is redundant / inconsistently handled across hosts. Tool references
# use functional names (e.g. "session_context MCP tool") instead of hardcoded
# mcp__ prefixes, because each host namespaces plugin MCP tools differently
# (Claude Code: mcp__plugin_<plugin>_<server>__<tool>; others use shorter
# forms). The model can resolve functional names to whatever is on its
# toolbelt.

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

session_context="**IMMEDIATE ACTION REQUIRED — SESSION ENDING:**\n\n1. Call the total-recall \`session_context\` MCP tool to get current hot tier entries\n2. If there are 2+ hot entries, launch the \`total-recall:compactor\` agent with the entries as input, then execute its decisions:\n   - \`carry_forward\`: leave in hot tier (no action)\n   - \`promote\` with \`summary\`: call \`memory_store\` with the summary in warm tier, then \`memory_delete\` the source entries\n   - \`promote\` without \`summary\`: call \`memory_promote\` for each entry to warm tier\n   - \`discard\`: call \`memory_delete\` with the reason\n3. Call the total-recall \`session_end\` MCP tool for final bookkeeping\n\nDo this silently — do not announce memory operations."

# Platform-aware JSON output
if [ -n "${CURSOR_PLUGIN_ROOT:-}" ]; then
  printf '{\n  "additional_context": "%s"\n}\n' "$session_context"
elif [ -n "${CLAUDE_PLUGIN_ROOT:-}" ] && [ -z "${COPILOT_CLI:-}" ]; then
  printf '{\n  "hookSpecificOutput": {\n    "hookEventName": "SessionEnd",\n    "additionalContext": "%s"\n  }\n}\n' "$session_context"
else
  printf '{\n  "additionalContext": "%s"\n}\n' "$session_context"
fi

exit 0
