#!/usr/bin/env bash
# total-recall UserPromptSubmit hook — re-asserts pinned directives near the
# live edge on an adaptive throttle. All logic lives in the CLI; this wrapper
# only forwards stdin and the host flag, and must NEVER fail the user's prompt.
set -uo pipefail

host="claude-code"
if [ -n "${CURSOR_PLUGIN_ROOT:-}" ]; then
  host="cursor"
elif [ -n "${COPILOT_CLI:-}" ]; then
  host="copilot-cli"
fi

# Forward hook stdin to the command. On ANY failure, emit a no-op object.
total-recall pinned-floor --host "$host" || printf '{}'
exit 0
