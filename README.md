```
╔══════════════════════════════════════════════╗
║  REKALL INC. -- MEMORY IMPLANT SYSTEM v2.84  ║
╠══════════════════════════════════════════════╣
║                                              ║
║  CLIENT: Quaid, Douglas                      ║
║  STATUS: MEMORY EXTRACTION IN PROGRESS       ║
║                                              ║
║  > Loading tier: STICKY ......... [OK]       ║
║  > Loading tier: HOT ............ [OK]       ║
║  > Loading tier: WARM ........... [OK]       ║
║  > Loading tier: COLD ........... [OK]       ║
║  > Semantic index: 384 dimensions  [OK]      ║
║  > Vector search: ONLINE                     ║
║                                              ║
║  ┌──────────────────────────────────┐        ║
║  │ SELECT PACKAGE:                  │        ║
║  │                                  │        ║
║  │  [x] Total Recall -- $899        │        ║
║  │  [ ] Blue Sky on Mars            │        ║
║  │  [ ] Secret Agent                │        ║
║  └──────────────────────────────────┘        ║
║                                              ║
║  "For the Memory of a Lifetime"              ║
╚══════════════════════════════════════════════╝
```

[![CI](https://github.com/strvmarv/total-recall/actions/workflows/dotnet-ci.yml/badge.svg)](https://github.com/strvmarv/total-recall/actions/workflows/dotnet-ci.yml)
[![npm](https://img.shields.io/npm/v/@strvmarv/total-recall)](https://www.npmjs.com/package/@strvmarv/total-recall)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

# total-recall

**Persistent, cross-tool memory for AI coding assistants.**

Your AI assistant forgets everything when the session ends. total-recall keeps your corrections, preferences, decisions and project knowledge in one local store and gives them back to every supported tool, session after session. Everything is local by default: SQLite, a bundled embedding model, no external services and no API keys. Teams can point the same binary at Postgres or Cortex.

## Install

Requires Node.js 20 or later, on Linux (x64 or arm64), macOS (Apple Silicon) or Windows (x64). Intel Macs are not supported.

### Let your assistant install it

Paste this into any AI coding assistant:

> Install the total-recall memory plugin: fetch and follow the instructions at https://raw.githubusercontent.com/strvmarv/total-recall/main/INSTALL.md

The assistant reads the runbook, follows the page for its own harness, asks before it changes anything outside that harness, and tells you what to check afterwards.

### Claude Code

```sh
claude plugin marketplace add strvmarv/total-recall-marketplace
claude plugin install total-recall@strvmarv-total-recall-marketplace
```

Inside a session the same two steps are `/plugin marketplace add strvmarv/total-recall-marketplace` and `/plugin install total-recall@strvmarv-total-recall-marketplace`. Details, permissions and known gaps: [Claude Code](docs/hosts/claude-code.md).

### pi

```sh
pi install npm:@strvmarv/total-recall
```

This runs the package's install script, which can also configure other tools it finds on the machine ([what it changes](docs/npm-postinstall.md)). The form that skips it, the git form and known gaps: [pi](docs/hosts/pi.md).

### Other harnesses

Each page gives the commands, what must be asked of you first, and how to check the result.

| Harness | How it installs | Page |
|---|---|---|
| Codex CLI | Plugin from the total-recall marketplace; trust its hooks once | [Codex CLI](docs/hosts/codex-cli.md) |
| Copilot CLI | Plugin from the total-recall marketplace | [Copilot CLI](docs/hosts/copilot-cli.md) |
| Cursor | Local plugin (Windows only so far); MCP server only elsewhere | [Cursor](docs/hosts/cursor.md) |
| Gemini CLI | npm package: MCP server, a per-prompt hook and a `GEMINI.md` block | [Gemini CLI](docs/hosts/gemini-cli.md) |
| Kiro | npm package: MCP server and a steering file; hooks per workspace | [Kiro](docs/hosts/kiro.md) |
| OpenCode | npm package plus an `mcp` entry added by hand | [OpenCode](docs/hosts/opencode.md) |
| Cline | npm package plus an entry in Cline's MCP settings | [Cline](docs/hosts/cline.md) |
| Windsurf | npm package: MCP server and a rules file | [Windsurf](docs/hosts/windsurf.md) |
| Zed | npm package: a context server entry | [Zed](docs/hosts/zed.md) |
| VS Code | npm package: an `mcp.json` entry | [VS Code](docs/hosts/vscode.md) |
| Hermes | npm package: a memory provider plugin | [Hermes](docs/hosts/hermes.md) |
| Any other MCP client | npm package plus an `mcpServers` entry | [Other MCP clients](docs/hosts/other-mcp.md) |

## Check that it works

Start a new session: MCP tools appear only in a new session. Where the harness delivers the startup instructions, the assistant opens with a short status line like this one:

> total-recall loaded — 2 pinned, 3 hot, 12 warm, 5 cold, 2 KB collections. Storage: cortex. Last session: 2 hours ago.

Then paste this to check it end to end:

```
Check that total-recall is working: call its status tool and tell me the tier counts and the database path.
```

[First run](docs/first-run.md) says what happens the first time. If nothing appears, see your harness's page under [Install](#install); [Hermes](docs/hosts/hermes.md) has its own check.

## Use it

- **It remembers without being asked.** Where the harness delivers the startup instructions ([Supported platforms](docs/platforms.md)), corrections, preferences and decisions are stored as you work, and relevant memories are retrieved for each question. You can also ask the assistant to remember something, or run `/total-recall:commands store <content>`. See [what gets remembered](docs/how-it-works.md#what-gets-remembered).
- **Pin what must always be followed.** `/total-recall:commands pin <id>` makes an entry a pinned directive: injected first at session start and never decayed. See [the tier model](docs/how-it-works.md#tier-model).
- **Give it your docs.** `/total-recall:commands ingest <path>` adds files or a directory to the knowledge base, retrieved when relevant. See the `ingest` and `kb` rows in [Commands](docs/commands.md).
- **Commands.** `/total-recall:commands help` lists them all: search, forget, inspect, promote, compact, eval and more. The form differs by harness (on pi it is `/total-recall <subcommand>`): see [Supported platforms](docs/platforms.md) and [Commands](docs/commands.md).
- **Web UI.** Where the global npm package is installed, `total-recall ui` opens a local dashboard for memories, the knowledge base, token usage and retrieval quality. On a plugin install or in pi, where `total-recall` is not on your PATH, ask the assistant for the command for your install. See [Web UI](docs/web-ui.md).
- **The `develop` skill.** `/develop` takes a change from an idea to a pull request: Define turns the idea into a reviewed GitHub issue, and Deliver turns the issue into a pull request. It never merges. See [the develop skill](docs/develop.md).

## How it works

New memories land in the **warm** tier, where they are searched for every question with a mix of keyword and vector search. Entries that prove useful earn a place in the small **hot** tier, which is injected at session start within a token budget. **Sticky** pins are hot entries you mark by hand: they are injected first, in full, and re-asserted during long sessions where the harness supports it. The **cold** tier holds the knowledge base built from the files you ingest, and memories that decay out of warm. Embeddings come from a model that ships with the binary and runs locally. The full explanation is in [How total-recall works](docs/how-it-works.md).

## Documentation

| Page | What it covers |
|---|---|
| [INSTALL.md](INSTALL.md) | The install runbook an assistant follows |
| [Supported platforms](docs/platforms.md) | What an install delivers on each harness |
| [First run](docs/first-run.md) | Provisioning, the first session, upgrading from 0.7.x |
| [How total-recall works](docs/how-it-works.md) | Entry types, tiers, search, embeddings, session start, the pinned floor |
| [Commands](docs/commands.md) | Every `/total-recall:commands` subcommand |
| [Web UI](docs/web-ui.md) | The local dashboard and its sections |
| [Configuration](docs/configuration.md) | `config.toml`, Postgres, remote embedders |
| [Transcript archive](docs/transcripts.md) | The opt-in archive of conversation transcripts: enabling, archiving and embedding Claude Code sessions, searching them by meaning and words, reading around a hit, listing sessions, status, forgetting |
| [Relocating the database](docs/database-location.md) | Cloud-synced and shared databases |
| [Connecting to Cortex](docs/cortex.md) | The shared backend for teams |
| [Skills](docs/skills.md) | Advertising your own skills at session start |
| [The develop skill](docs/develop.md) | Requirements, trust, account pinning, tool allowlist |
| [MCP tools](docs/tools.md) | The tool reference |
| [Architecture](docs/architecture.md) | Layers, data flow, backends, core technologies |
| [What the install script changes](docs/npm-postinstall.md) | Other tools that installing the npm package can configure |
| [Verifying a release](docs/verifying-a-release.md) | Checking a download's build attestation |
| [Building from source](docs/building.md) | Prerequisites and the build |
| [PRIVACY.md](PRIVACY.md) | What is stored and what can leave the machine |
| [CONTRIBUTING.md](CONTRIBUTING.md) | The contributor guide |
| [CHANGELOG.md](CHANGELOG.md) | Release history |

## Privacy

Your data stays on your machine by default and no memory or usage data goes to any analytics service or third party. It leaves your machine only if you configure a Postgres backend, Cortex or a remote embedder. Two things contact GitHub on their own: a plugin install downloads the engine from GitHub Releases on first launch, and a periodic version check, about once a day, asks `api.github.com` for the newest release (it sends only the version of total-recall you run, in its User-Agent header, GitHub sees your IP address as for any request, and `updates.check = false` turns it off). The detail is in [PRIVACY.md](PRIVACY.md).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for the contributor guide, including how to add a host importer, extend the chunking pipeline or add an MCP tool handler.

## Credits

total-recall's plugin architecture, skill format, hook system and multi-platform wrapper pattern are modeled after [superpowers](https://github.com/obra/superpowers) by [obra](https://github.com/obra). If you are building plugins for coding assistants, start there. The technologies total-recall is built with are listed under [Architecture](docs/architecture.md#core-technologies).

## License

MIT. See [LICENSE](LICENSE).
