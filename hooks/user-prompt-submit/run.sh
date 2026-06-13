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

# Cursor's beforeSubmitPrompt cannot inject context; short-circuit so we don't
# spawn the CLI or advance session state pointlessly.
if [ "$host" = "cursor" ]; then
  printf '{}\n'
  exit 0
fi

# Capture the CLI's stdout so a mid-write crash can never produce torn output.
# stdin is still inherited by the child (it reads the hook payload). Emit
# exactly one JSON object: the CLI's output on success, else a no-op.
output=$(total-recall pinned-floor --host "$host" 2>/dev/null)
if [ $? -ne 0 ] || [ -z "$output" ]; then
  printf '{}\n'
else
  printf '%s\n' "$output"
fi
exit 0
