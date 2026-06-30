#!/usr/bin/env pwsh
# total-recall - Kiro SessionStart hook (Windows / PowerShell).
#
# Kiro's command-hook contract (see https://kiro.dev/docs/hooks): on exit 0 the
# hook's *raw STDOUT* is appended to the agent's context for SessionStart and
# UserPromptSubmit. Unlike the Claude Code / Copilot CLI hooks, Kiro does NOT
# parse a JSON envelope - so we emit the skill instructions as plain text.
#
# This injects the same SKILL.md the other hosts inject. The skill tells the
# agent to call the `session_start` MCP tool before its first reply, which is
# what actually loads the pinned block + hot-tier context from the engine.
#
# FAIL-SAFE: a SessionStart hook must never abort the session. Any error exits 0
# with no output, so the session proceeds in degraded (no-memory) mode.

$ErrorActionPreference = 'Stop'
try {
    # Resolve the skill file relative to this script (repo) first, then fall
    # back to the installed npm package layout. This keeps the hook working
    # both from a git checkout and from the published global install.
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $candidates = @(
        (Join-Path $scriptDir '..\..\skills\commands\SKILL.md'),
        (Join-Path $env:APPDATA 'npm\node_modules\@strvmarv\total-recall\skills\commands\SKILL.md')
    )

    $skillFile = $null
    foreach ($c in $candidates) {
        if (Test-Path $c) { $skillFile = (Resolve-Path $c).Path; break }
    }

    if ($null -eq $skillFile) {
        # No skill file found - nothing to inject. Fail safe, session continues.
        exit 0
    }

    # Emit raw skill text. Kiro adds STDOUT verbatim to context on exit 0.
    $content = Get-Content -Path $skillFile -Raw
    [Console]::Out.Write($content)
    exit 0
}
catch {
    # Never block a session start.
    exit 0
}
