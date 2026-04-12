```
╔══════════════════════════════════════════════╗
║  REKALL INC. -- MEMORY IMPLANT SYSTEM v2.84  ║
╠══════════════════════════════════════════════╣
║                                              ║
║  CLIENT: Quaid, Douglas                      ║
║  STATUS: MEMORY EXTRACTION IN PROGRESS       ║
║                                              ║
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

Your AI forgets everything when the session ends. Preferences, decisions, project context, corrections — gone. total-recall fixes that: a shared memory layer that persists across sessions, tools, and devices.

---

## The Problem

Every TUI coding assistant has the same gaps:

- **No memory between sessions** — every new session starts from zero, repeating the same context
- **Siloed by tool** — switching between Claude Code and Copilot CLI means starting from scratch
- **Single-machine** — your context doesn't follow you across devices
- **Context bloat** — stuffing everything into a `CLAUDE.md` wastes tokens every prompt
- **No token visibility** — no way to know what your AI sessions are actually costing you

---

## The Solution

- **Persistent memory** — corrections, preferences, decisions, and project context survive sessions automatically
- **Cross-tool** — one memory store shared across Claude Code, Copilot CLI, Cursor, Cline, OpenCode, and Hermes; existing memories auto-import on first run
- **Cross-device** — point `TOTAL_RECALL_DB_PATH` at a cloud-synced folder and your memory follows you everywhere
- **Smarter context, lower token cost** — a three-tier model (Hot / Warm / Cold) enforces a 4000-token budget per prompt, so you get relevant context without carrying everything
- **Token expenditure tracking** *(coming soon)* — see exactly what each session costs and verify the savings
- **Knowledge base** — ingest your docs, READMEs, API references, and architecture notes; retrieved semantically when relevant
- **Observability** — measure retrieval quality, run benchmarks, and compare config changes with the built-in eval framework

By default, all state is local: SQLite + vector embeddings, no external services, no API keys. For teams, configure a shared Postgres/pgvector backend and remote embedder — same binary, just config.

### How It Works

total-recall uses a three-tier memory model: **Hot** memories (up to 50 entries) are auto-injected into every prompt so your most important context is always present. **Warm** memories (up to 10K entries) are retrieved semantically — when you ask about authentication, relevant auth memories surface automatically. **Cold** storage is an unlimited hierarchical knowledge base: ingest your docs, README files, API references, and architecture notes, and they're retrieved when relevant.

The knowledge base ingests entire directories — source trees, documentation folders, design specs — and chunks them semantically with heading-aware Markdown parsing and regex-based code parsing. Every chunk is embedded with all-MiniLM-L6-v2 (384 dimensions, runs locally via ONNX) so retrieval is purely semantic, no keyword matching required. For enterprise deployments, swap in a remote embedder (OpenAI, Amazon Bedrock) with higher dimensions for finer-grained retrieval across larger corpora.

Platform support is via MCP (Model Context Protocol), which means total-recall works with any MCP-compatible tool. Dedicated importers for Claude Code, Copilot CLI, Cursor, Cline, OpenCode, and Hermes mean your existing memories migrate automatically on first run. An eval framework lets you measure retrieval quality, run benchmarks, and compare configuration changes before committing them.

---

## Prerequisites

- **Node.js >= 20.0.0** — required only for `npm install` and the `bin/start.js` launcher (~60 lines of zero-dep Node). The actual MCP server is a prebuilt **.NET 8 NativeAOT binary** that ships pre-compiled per platform.
- **Internet access on first launch** — only needed if you install via Claude Code's `/plugin` flow with a `source: github` marketplace entry. In that case `bin/start.js` downloads the matching per-RID archive (~22 MB) from GitHub Releases on first run. The npm install path ships all RIDs in the tarball and doesn't need a runtime download.
- **No bundled Bun, no system SQLite, no .NET runtime required.** The AOT binary ships its own `libonnxruntime`, `libe_sqlite3`, and `vec0` (sqlite-vec extension) as sibling files. The `all-MiniLM-L6-v2` ONNX embedding model is bundled in `models/`.
- **Git LFS** — required only if cloning the repo from source (`git lfs install` before clone). The embedding model is stored in LFS. Runtime auto-downloads from HuggingFace if LFS fetch fails.

---

## Installation

### Self-Install (Paste Into Any AI Coding Assistant)

> Install the total-recall memory plugin: fetch and follow the instructions at https://raw.githubusercontent.com/strvmarv/total-recall/main/INSTALL.md

That's it. Your AI assistant will read the instructions and install total-recall for its platform.

### Claude Code

```
/plugin install total-recall@strvmarv-total-recall-marketplace
```

Or if the marketplace isn't registered:

```
/plugin marketplace add strvmarv/total-recall-marketplace
/plugin install total-recall@strvmarv-total-recall-marketplace
```

### npm (Any MCP-Compatible Tool)

```bash
npm install -g @strvmarv/total-recall
```

Then add to your tool's MCP config:

```json
{
  "mcpServers": {
    "total-recall": {
      "command": "total-recall"
    }
  }
}
```

This works with **Copilot CLI**, **OpenCode**, **Cline**, **Cursor**, **Hermes**, and any other MCP-compatible tool.

> **Note:** `npx -y @strvmarv/total-recall` does not work due to an [npm bug](https://github.com/npm/cli/issues/3753) with scoped package binaries. Use the global install (`total-recall` command) instead.

### From Source

```bash
git clone https://github.com/strvmarv/total-recall.git
cd total-recall
npm install                                # pulls sqlite-vec native libs into node_modules/
dotnet build src/TotalRecall.sln           # requires .NET 10 SDK (per global.json)
dotnet test src/TotalRecall.sln            # 944 tests across Core (F#), Cli, Server, Infrastructure
dotnet publish src/TotalRecall.Host/TotalRecall.Host.csproj -c Release -r linux-x64 -p:PublishAot=true
# (swap linux-x64 for your RID: linux-arm64, osx-arm64, or win-x64)
```

The AOT publish output lands in `src/TotalRecall.Host/bin/Release/net8.0/<rid>/publish/` with the binary plus all sibling native libs (`libonnxruntime.*`, `libe_sqlite3.*`, `runtimes/vec0.*`) ready to run.

### First Session

On first `session_start`, total-recall initializes `~/.total-recall/` with a SQLite database and loads the bundled embedding model (included in package, no download needed). Every session then runs:

1. **Import sync** — scans Claude Code, Copilot CLI, Cursor, Cline, OpenCode, and Hermes memory directories, deduplicates and imports new entries
2. **Warm sweep** — if overdue, demotes stale warm entries to cold based on decay
3. **Project docs ingest** — detects README.md, CONTRIBUTING.md, CLAUDE.md, AGENTS.md, and docs/ in cwd and ingests into a project-scoped KB collection
4. **Smoke test** — on version change, runs a 22-query benchmark to validate retrieval quality
5. **Warm-to-hot promotion** — semantically searches warm tier for entries relevant to the current project and promotes them to hot
6. **Hot tier assembly** — enforces token budget, evicts lowest-decay entries, returns hot tier as injectable context
7. **Config snapshot** — captures current config for retrieval quality tracking
8. **Tier summary** — counts entries across all tiers and KB collections for the startup announcement.
9. **Hint generation** — surfaces high-value warm memories (corrections, preferences, frequently accessed) as actionable one-liners for the agent.
10. **Session continuity** — computes time since last session for contextual framing.
11. **Regression detection** — compares retrieval metrics against previous config snapshot and alerts if quality has dropped.

---

## Architecture

```
MCP Server (.NET 8 NativeAOT — C# imperative shell + F# functional core)
├── TotalRecall.Core (F#)        — pure functions: tokenizer, decay, ranking, parsers
├── TotalRecall.Infrastructure   — SQLite/Postgres storage, ONNX/remote embedder, importers, ingestion
├── TotalRecall.Server           — MCP JSON-RPC server, 33 tool handlers, lifecycle
├── TotalRecall.Cli              — CLI commands (status, eval, kb, memory, config, migrate)
└── TotalRecall.Host             — composition root, AOT entry point, migration guard

Tiers:
  Hot (50 entries)  → auto-injected every prompt
  Warm (10K entries) → semantic search per query
  Cold (unlimited)   → hierarchical KB retrieval

Backends (selected by config):
  Local:      SQLite + sqlite-vec + bundled ONNX embedder (default, zero config)
  Enterprise: Postgres/pgvector + remote embedder (OpenAI, Bedrock) + multi-user
```

**Data flow:**

1. `store` — write a memory, assign tier, embed, persist
2. `search` — embed query, vector search across all tiers, return ranked results
3. `compact` — decay scores, promote hot→warm, demote warm→cold
4. `ingest` — chunk files, embed chunks, store in cold tier with metadata

**Local mode:** all state lives in `~/.total-recall/total-recall.db`. The embedding model and the sqlite-vec native extension are bundled with the binary. No network calls required at runtime.

**Enterprise mode:** set a Postgres connection string in config and the same binary switches to Postgres/pgvector with HNSW indexes, tsvector FTS, and per-user ownership/visibility scoping. Pair with a remote embedder for higher-dimensional vectors across shared team knowledge.

---

## Commands

All commands are routed through the `/total-recall:commands` skill:

| Command | MCP Tool | Description |
|---|---|---|
| `/total-recall:commands help` | — | Show command reference table |
| `/total-recall:commands status` | `status` | Dashboard overview |
| `/total-recall:commands search <query>` | `memory_search` | Semantic search across all tiers |
| `/total-recall:commands store <content>` | `memory_store` | Manually store a memory |
| — | `memory_get` | Retrieve a specific entry by ID |
| — | `memory_update` | Update an existing entry's content, tags, or project |
| `/total-recall:commands forget <query>` | `memory_search` + `memory_delete` | Find and delete entries |
| `/total-recall:commands inspect <id>` | `memory_inspect` | Deep dive on single entry with compaction history |
| `/total-recall:commands promote <id>` | `memory_promote` | Move entry to higher tier |
| `/total-recall:commands demote <id>` | `memory_demote` | Move entry to lower tier |
| `/total-recall:commands history` | `memory_history` | Show recent tier movements |
| `/total-recall:commands lineage <id>` | `memory_lineage` | Show compaction ancestry |
| `/total-recall:commands export` | `memory_export` | Export to portable JSON format |
| `/total-recall:commands import <file>` | `memory_import` | Import from export file |
| `/total-recall:commands ingest <path>` | `kb_ingest_file` / `kb_ingest_dir` | Add files/dirs to knowledge base |
| `/total-recall:commands kb search <query>` | `kb_search` | Search knowledge base |
| `/total-recall:commands kb list` | `kb_list_collections` | List KB collections |
| `/total-recall:commands kb refresh <id>` | `kb_refresh` | Re-ingest a collection |
| `/total-recall:commands kb remove <id>` | `kb_remove` | Remove KB entry |
| — | `kb_summarize` | Generate summary for a KB collection |
| `/total-recall:commands compact` | `compact_now` | Force compaction |
| — | `session_start` | Initialize session: sync imports, assemble hot tier |
| — | `session_end` | End session: run compaction |
| — | `session_context` | Get current hot tier entries as context |
| `/total-recall:commands eval` | `eval_report` | Retrieval quality metrics (filterable by config snapshot) |
| `/total-recall:commands eval --benchmark` | `eval_benchmark` | Run synthetic benchmark |
| `/total-recall:commands eval --compare <name>` | `eval_compare` | Compare metrics between two config snapshots |
| `/total-recall:commands eval --snapshot <name>` | `eval_snapshot` | Manually create a named config snapshot |
| `/total-recall:commands eval --grow` | `eval_grow` | Review and accept/reject benchmark candidates from retrieval misses |
| `/total-recall:commands config get <key>` | `config_get` | Read config value |
| `/total-recall:commands config set <key> <val>` | `config_set` | Update config |
| `/total-recall:commands import-host` | `import_host` | Import from host tools |

Memory capture, retrieval, and compaction run automatically in the background — see the "Automatic Behavior" section of the `/total-recall:commands` skill.

> **Note:** `/total-recall:commands` is implemented as a Claude Code skill (at `skills/commands/SKILL.md`), not as a slash-command file under `commands/`. The skill handles all `<subcommand>` arguments internally.

---

## Supported Platforms

| Platform | Support | Notes |
|---|---|---|
| Claude Code | Full | Native plugin, session hooks, auto-import |
| Copilot CLI | Full | Auto-import from existing Copilot memory files |
| OpenCode | MCP | Configure MCP server in opencode config |
| Cline | MCP | Configure MCP server in Cline settings |
| Cursor | Full | MCP server + `.cursor-plugin/` wrapper (SessionStart only; run `/total-recall:commands compact` manually — Cursor has no SessionEnd hook) |
| Hermes | Full | Auto-import from Hermes memory files |

---

## Configuration

Copy `~/.total-recall/config.toml` to override defaults:

```toml
# total-recall configuration

[tiers.hot]
max_entries = 50          # Max entries auto-injected per prompt
token_budget = 4000       # Max tokens for hot tier injection
carry_forward_threshold = 0.7  # Score threshold to stay in hot

[tiers.warm]
max_entries = 10000       # Max entries in warm tier
retrieval_top_k = 5       # Results returned per search
similarity_threshold = 0.65    # Min cosine similarity for retrieval
cold_decay_days = 30      # Days before unused warm entries decay to cold

[tiers.cold]
chunk_max_tokens = 512    # Max tokens per knowledge base chunk
chunk_overlap_tokens = 50 # Overlap between adjacent chunks
lazy_summary_threshold = 5     # Accesses before generating summary

[compaction]
decay_half_life_hours = 168    # Score half-life (168h = 1 week)
warm_threshold = 0.3           # Score below which warm→cold
promote_threshold = 0.7        # Score above which cold→warm
warm_sweep_interval_days = 7   # How often to run warm sweep

[embedding]
model = "all-MiniLM-L6-v2"    # Embedding model name
dimensions = 384               # Embedding dimensions
# provider = "local"           # "local" (default) | "openai" | "bedrock"
# endpoint = "https://api.openai.com/v1"  # OpenAI-compatible base URL
# bedrock_region = "us-east-1"            # Bedrock only
# bedrock_model = "cohere.embed-v4:0"     # Bedrock model ID
# api_key = ""                            # or set TOTAL_RECALL_EMBEDDING_API_KEY env var

# --- Remote storage (optional) ---
# [storage]
# connection_string = "Host=localhost;Database=total_recall;Username=tr;Password=changeme"

# --- User identity (optional, Postgres only) ---
# [user]
# user_id = "alice"               # or set TOTAL_RECALL_USER_ID env var
```

**Relocating the database:** set `TOTAL_RECALL_DB_PATH` to an absolute path or `~/`-prefixed path. See [INSTALL.md](INSTALL.md#relocating-the-database) for cloud-sync and shared-workspace guidance.

**Switching to Postgres:** uncomment the `[storage]` section with your connection string. The binary auto-detects the backend — no code changes, no flag. Pair with `[embedding] provider = "bedrock"` or `"openai"` for remote embeddings. Run `migrate_to_remote` to copy local memories to the shared database with re-embedding.

### Connecting to Cortex

[Total Recall Cortex](https://github.com/radancy-pe/total-recall-cortex) is the shared backend platform that adds team knowledge bases, connectors (Jira, Confluence, GitHub), chat/RAG, and a React UI on top of the plugin's memory layer.

In Cortex mode, the plugin operates as a hybrid:
- **User memories** are stored locally (fast reads/writes) and synced bidirectionally to Cortex
- **Global knowledge** (team KB, connector-ingested data) is queried remotely from Cortex
- **Telemetry** (usage, retrieval events, compaction log) is pushed to Cortex for unified dashboards

Configure in your `config.toml`:

```toml
[storage]
mode = "cortex"

[cortex]
url = "https://your-cortex-instance.example.com"
pat = "tr_your_personal_access_token"
```

Or via environment variables:

```bash
export TOTAL_RECALL_CORTEX_URL="https://your-cortex-instance.example.com"
export TOTAL_RECALL_CORTEX_PAT="tr_your_personal_access_token"
```

Generate a PAT from the Cortex web UI under Settings → Personal Access Tokens.

**Offline resilience:** If Cortex is unreachable, the plugin continues working locally. A persistent sync queue buffers outbound changes and flushes automatically when connectivity is restored.

---

## Extending

### Adding a New Host Tool

Implement the `IImporter` interface defined in `src/TotalRecall.Infrastructure/Importers/IImporter.cs`. The contract: detect the host's presence, scan its memory directories, and import memories/knowledge with deduplication via `ImportLog`. See `src/TotalRecall.Infrastructure/Importers/ClaudeCodeImporter.cs` for a reference implementation, and [CONTRIBUTING.md](CONTRIBUTING.md) for a full walkthrough.

### Adding a New Content Type

Content types (`"memory"` and `"knowledge"`) are defined as a discriminated union in `src/TotalRecall.Core/Types.fs`. Each tier has separate tables per content type (e.g., `hot_memories`, `hot_knowledge`). To add a new content type, extend the F# `ContentType` DU and add a migration step in `src/TotalRecall.Infrastructure/Storage/Schema.cs` (add a new function to the migrations array — the framework runs them sequentially based on `_schema_version`).

### Adding a New Chunking Parser

Chunking lives in `src/TotalRecall.Core/Chunker.fs` (F# pure functions) and per-language parsers in `src/TotalRecall.Core/Parsers.fs`. Add a new parser by extending the relevant union case and wiring it through the dispatch in `Chunker.chunk`. See [CONTRIBUTING.md](CONTRIBUTING.md) for the full walkthrough.

---

## Built With & Inspired By

### [superpowers](https://github.com/obra/superpowers) by [obra](https://github.com/obra)

total-recall's plugin architecture, skill format, hook system, multi-platform wrapper pattern, and development philosophy are directly inspired by and modeled after the **superpowers** plugin. superpowers demonstrated that a zero-dependency, markdown-driven skill system could fundamentally improve how AI coding assistants behave — total-recall extends that same philosophy to memory and knowledge management.

Specific patterns we learned from superpowers:

- **SKILL.md format** with YAML frontmatter and trigger-condition-focused descriptions
- **SessionStart hooks** for injecting core behavior at session start
- **Multi-platform wrappers** (`.claude-plugin/`, `.copilot-plugin/`, `.cursor-plugin/`, `.opencode/`)
- **Subagent architecture** for isolated, focused task execution
- **Zero-dependency philosophy** — no external services, no API keys, no cloud
- **Two-stage review pattern** for quality assurance

If you're building plugins for TUI coding assistants, start with [superpowers](https://github.com/obra/superpowers). It's the foundation this ecosystem needs.

### Core Technologies

- [.NET 8 / NativeAOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/) — single-binary deployment, no runtime dependency
- [F# Core](https://learn.microsoft.com/en-us/dotnet/fsharp/) — pure functional core: tokenizer, parsers, decay, ranking
- [Microsoft.Data.Sqlite](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/) — embedded SQLite with extension loading
- [sqlite-vec](https://github.com/asg017/sqlite-vec) — Vector similarity search in SQLite (loaded as a native extension via `LoadExtension`)
- [Microsoft.ML.OnnxRuntime](https://onnxruntime.ai/docs/get-started/with-csharp.html) — Local ML inference, AOT-compatible
- [Microsoft.ML.Tokenizers](https://learn.microsoft.com/en-us/dotnet/api/microsoft.ml.tokenizers) — canonical BERT BasicTokenization + WordPiece
- [all-MiniLM-L6-v2](https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2) — Sentence embeddings (384d)
- Hand-rolled JSON-RPC stdio MCP server in `TotalRecall.Server` (no SDK dependency)
- [Spectre.Console](https://spectreconsole.net/) — CLI rendering for `total-recall status` / `eval` / `kb list`

---

## License

MIT — see [LICENSE](LICENSE)
