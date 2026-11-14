# Supported platforms

What an install delivers on each harness. Each row says only what that harness's page says; follow the link for the install steps, the evidence and the full list of known gaps.

| Harness | MCP tools | Startup instructions | Pinned-directive floor | Commands | Notes |
|---|---|---|---|---|---|
| [Claude Code](hosts/claude-code.md) | Yes (plugin `.mcp.json`) | Yes (`SessionStart` hook) | Yes, on every prompt (`UserPromptSubmit` hook) | `/total-recall:commands` | Native plugin with session hooks. The MCP-only path gives the tools and nothing else. In a permission mode that never asks, the tools are blocked until the allow rule is in place. |
| [pi](hosts/pi.md) | Yes (registered by the extension) | Yes (hidden message with the first prompt, again after a compaction) | Yes, turn-count trigger only | `/total-recall <subcommand>` | `pi install` package. Known gaps: no `session_end`, `setup` and `update` have no pi meaning (use `pi update`), `compact --fast`. |
| [Codex CLI](hosts/codex-cli.md) | Yes (bundled `codex/mcp.json`) | Yes (`SessionStart` hook) | Yes, turn-count trigger only (`UserPromptSubmit` hook) | Commands skill (`$total-recall:total-recall:commands`) | `codex plugin add` plugin. Known gaps: hooks need a one-time trust review, no `SessionEnd` nudge; Windows is the verified platform. |
| [Copilot CLI](hosts/copilot-cli.md) | Yes (plugin `.mcp.json`) | Yes (`sessionStart` hook) | Yes, turn-count trigger only (`userPromptSubmitted` hook) | `/total-recall:commands` | `copilot plugin install` plugin. Known gaps: server-side project scope, startup instructions not re-injected after `/compact`, no `SessionEnd` nudge; observed on Windows. The MCP-only path gives the tools and nothing else. |
| [Cursor](hosts/cursor.md) | Yes (local plugin, workspace cwd) | Yes (`sessionStart` hook) | On tool-using turns only, turn-count trigger (`postToolUse` hook) | Skills `commands` and `using-total-recall` | Local plugin, Windows only so far; on macOS and Linux only the MCP-only option. Known gaps: the first `session_start` can hit Cursor's 60 s MCP limit, no custom store on the plugin path, no `SessionEnd` nudge. The MCP-only path gives the tools and nothing else. |
| [Gemini CLI](hosts/gemini-cli.md) | Yes | Yes (a block in `GEMINI.md`) | Yes (`BeforeAgent` hook; the token-growth trigger is active) | Not stated on the page | Configured by the npm install script or by hand. |
| [Kiro](hosts/kiro.md) | Yes | Yes (always-included steering file) | Only with the workspace hooks copied; turn-count trigger only | Not stated on the page | The install script does not copy the hooks; they are per workspace. |
| [OpenCode](hosts/opencode.md) | Yes (local MCP command under the `mcp` key) | No (a user step) | No | Optional skills copy (whether OpenCode reads it was not observed) | Configured by hand. |
| [Cline](hosts/cline.md) | Yes | No (a user step) | No | Not stated on the page | The install script writes the entry to the settings file under `~/.cline/data` and under VS Code's `saoudrizwan.claude-dev` folder when they exist; for a Cline it did not find, the user adds it through Cline's own "MCP Settings". |
| [Windsurf](hosts/windsurf.md) | Yes | Yes (rules file) | No | Not stated on the page | Configured by the npm install script or by hand. |
| [Zed](hosts/zed.md) | Yes (context server) | No (a user step) | No | Not stated on the page | Configured by the npm install script or by hand. |
| [VS Code](hosts/vscode.md) | Yes | No (a user step) | No | Not stated on the page | Configured by the npm install script or by hand. |
| [Hermes](hosts/hermes.md) | No: the model gets one `memory` tool, backed by total-recall | The memory provider calls `session_start` itself and adds the hot-tier context to the system prompt | No per-turn floor was found in the plugin | Not stated on the page | A Hermes memory provider, not an MCP entry. |
| [Any other MCP client](hosts/other-mcp.md) | Yes | No (a user step) | No | No | The tools and nothing else. |

How the per-turn floor works, and its mechanism on the hosts that page's table lists, is in [How total-recall works](how-it-works.md#pinned-directive-floor). What is imported from each tool on first run is in that page's [session start](how-it-works.md#session-start) section.
