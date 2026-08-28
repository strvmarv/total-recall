#!/usr/bin/env bash
# total-recall UserPromptSubmit hook — re-asserts pinned directives near the
# live edge on an adaptive throttle. All logic lives in the CLI; this wrapper
# only forwards stdin and the host flag, and must NEVER fail the user's prompt.
set -uo pipefail

# Resolve the plugin-bundled CLI the same way the MCP server does
# (node "$PLUGIN_ROOT/bin/start.js"), NOT a bare `total-recall` on PATH.
# A PATH lookup can resolve to a separately-installed global of a different
# version — or to nothing at all. A stale global without the `pinned-floor`
# command makes this hook fail and silently disable the floor. Deriving the
# root from this script's own location pins the hook to the same versioned
# binary the plugin ships, matching session-start/session-end.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PLUGIN_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

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
# Note: start.js downloads the platform binary from GitHub Releases on first
# run if binaries/ is absent — a one-time post-install event, not per-prompt
# overhead; once present it fast-paths immediately.
output=$(node "$PLUGIN_ROOT/bin/start.js" pinned-floor --host "$host" 2>/dev/null)
rc=$?

# The capture above only proves *something* came back, not that it's a
# complete, well-formed JSON object. A killed/interrupted child (hook
# timeout, slow render on a large pinned block, a pipe hiccup) can still
# flush a partial payload before dying — bash forwards that partial text
# as if it were whole, which Claude Code then rejects with "Unterminated
# string". Validate with `node` (already required to run the CLI, unlike
# `jq` which isn't guaranteed present) before forwarding; anything that
# isn't a JSON object (partial, or well-formed but non-object like `null`
# or `[]`) falls back to the same no-op as an empty capture. Chunks are
# concatenated once via Buffer.concat rather than repeated string `+=`,
# which is quadratic and this is specifically the large-payload path.
if [ "$rc" -ne 0 ] || [ -z "$output" ]; then
  printf '{}\n'
elif printf '%s' "$output" | node -e '
const chunks = [];
process.stdin.on("data", c => chunks.push(c));
process.stdin.on("end", () => {
  try {
    const parsed = JSON.parse(Buffer.concat(chunks).toString("utf8"));
    process.exit(parsed !== null && typeof parsed === "object" && !Array.isArray(parsed) ? 0 : 1);
  } catch { process.exit(1); }
});
' 2>/dev/null; then
  printf '%s\n' "$output"
else
  printf '{}\n'
fi
exit 0
