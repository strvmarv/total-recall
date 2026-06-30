#!/usr/bin/env pwsh
# total-recall - Kiro UserPromptSubmit hook (Windows / PowerShell).
#
# Re-asserts pinned directives near the live edge on the engine's adaptive
# throttle. All decision logic lives in the CLI (`pinned-floor`); this wrapper
# only forwards the hook payload on stdin and prints the result.
#
# Kiro contract (https://kiro.dev/docs/hooks): on exit 0, raw STDOUT is added to
# context for UserPromptSubmit. Kiro does NOT parse a JSON envelope - so unlike
# the Claude Code / Copilot CLI wrappers we must emit PLAIN TEXT, and we must
# emit NOTHING on a skipped turn (printing the literal "{}" would inject junk).
#
# Transitional behavior: until the engine ships a `kiro` arm in
# PinnedFloorCommand.EnvelopeForHost, `pinned-floor --host kiro` returns the
# no-op envelope "{}". We detect that (and the JSON envelopes from other hosts)
# and suppress it, so the floor stays silent rather than injecting raw JSON.
# Once the kiro arm lands and emits plain text, it passes straight through.
#
# FAIL-SAFE: a UserPromptSubmit hook must never block the user's prompt. Any
# error exits 0 with no output.

$ErrorActionPreference = 'Stop'
try {
    # Forward the hook payload (session_id, cwd, transcript_path, ...) on stdin.
    $payload = [Console]::In.ReadToEnd()

    # Resolve the CLI: prefer the repo-bundled binary, else the global shim.
    $cli = 'total-recall'
    $shim = Get-Command total-recall -ErrorAction SilentlyContinue
    if ($null -eq $shim) {
        # No CLI available - cannot assert the floor. Fail safe, inject nothing.
        exit 0
    }

    $output = ($payload | & $cli pinned-floor --host kiro 2>$null) | Out-String
    $trimmed = $output.Trim()

    # Suppress no-op / JSON-envelope output. A populated kiro arm emits plain
    # text (the reminder preamble + pinned block), which won't start with '{'.
    if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed -eq '{}' -or $trimmed.StartsWith('{')) {
        exit 0
    }

    [Console]::Out.Write($trimmed)
    exit 0
}
catch {
    # Never block a user prompt.
    exit 0
}
