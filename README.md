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
- **No token visibility** — no way to know what your AI sessions actually cost

---

## The Solution

- **Persistent memory** — corrections, preferences, decisions, and project context survive sessions automatically
- **Cross-tool** — one memory store shared across Claude Code, Copilot CLI, Cursor, Cline, OpenCode, and Hermes; existing memories auto-import on first run
- **Cross-device** — point `TOTAL_RECALL_DB_PATH` at a cloud-synced folder and your memory follows you everywhere
- **Smarter context, lower token cost** — a three-tier model (Hot / Warm / Cold) enforces a 4000-token budget per prompt, so you get relevant context without carrying everything
- **Token expenditure tracking** — see exactly what each session costs, broken down by host, project, and time window
- **Knowledge base** — ingest your docs, READMEs, API references, and architecture notes; retrieved semantically when relevant
- **Observability** — measure retrieval quality, run benchmarks, and compare config changes with the built-in eval framework

By default, all state is local: SQLite + vector embeddings, no external services, no API keys. For teams, configure a shared Postgres/pgvector backend and remote embedder — same binary, just config.

---

## Quick Start

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

---

## What Gets Remembered

Every memory has an entry type that tells total-recall what it is and how to treat it.

| Entry Type | Stored When | Example |
|---|---|---|
| `Correction` | You fix a mistake the AI made | `"Use Array.from() not spread for NodeList — spread fails in our build target"` |
| `Preference` | You state a style or workflow preference | `"Always use const over let unless reassignment is needed"` |
| `Decision` | You make an architecture or design choice | `"Using Zustand for state — Redux was overkill for this app size"` |
| `Surfaced` | The AI captures context automatically | Key facts, constraints, or project-specific patterns noticed during work |
| `Imported` | First-run import from another tool | Your existing Claude Code memories, Copilot snippets, Cursor history |
| `Compacted` | Tier compaction generates a summary | Multiple related memories merged into a higher-signal entry |
| `Ingested` | You ingest a file or directory | Chunks from READMEs, API docs, architecture notes |

**`Correction` and `Preference` entries get priority treatment.** They surface as actionable hints at every session start and carry higher decay scores — they stay in hot tier longer and are less likely to be evicted.

---

## How It Works

### Three-Tier Model

total-recall uses a three-tier memory model designed to balance signal density with token cost:

- **Hot** (up to 50 entries, 4000-token budget) — auto-injected into every prompt. Your most important corrections, preferences, and recently promoted entries are always present without any query.
- **Warm** (up to 10K entries) — retrieved semantically per query. When you ask about authentication, relevant auth memories surface automatically. Entries decay over time; unused ones migrate to cold.
- **Cold** (unlimited, hierarchical) — your knowledge base. Ingest entire directories — source trees, documentation, design specs — and they're retrieved when relevant.

### Hybrid Search

Retrieval combines **BM25 full-text search** and **cosine vector similarity**, merged by a pure F# ranking function. You get keyword precision when you search by exact terms and semantic recall when you describe what you need in natural language. The BM25/vector weight is tunable via `[search] fts_weight`.

### Embeddings

All memories are embedded with [all-MiniLM-L6-v2](https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2) (384 dimensions), running locally via ONNX — no API calls, no network dependency. The model ships bundled in the npm package. If it's missing (e.g., a git clone without LFS), the binary downloads it from HuggingFace automatically on first run.

For enterprise deployments, swap in a remote embedder (OpenAI, Amazon Bedrock) for higher-dimensional vectors and finer-grained retrieval across shared team knowledge.

### Session Start

Every `session_start` call runs the same sequence:

1. **Import sync** — scans all installed host tools (Claude Code, Copilot CLI, Cursor, Cline, OpenCode, Hermes), deduplicates via content hash, and imports new entries.
2. **Hot tier assembly** — assembles current hot entries as injectable context for the session.
3. **Hint generation** — surfaces up to 5 high-value warm memories as actionable one-liners: `Correction` and `Preference` entries first, frequently accessed entries (3+ accesses) second, recently promoted entries third. No LLM calls — pure DB queries.
4. **Tier summary** — counts entries across hot, warm, cold, and all KB collections.
5. **Session continuity** — reports human-readable time since the last compaction event (proxy for last active session).

Every `session_start` also runs a skill scan: it reads `~/.claude/skills/` plus any directories listed in `[skills] extra_dirs`, and advertises discovered skills as an `## Available Skills` block in the session context. In Cortex mode the scanned skills are also pushed to Cortex and the block is merged with any skills already stored there.

---

## Supported Platforms

| Platform | Support | Notes |
|---|---|---|
| Claude Code | Full | Native plugin, session hooks, auto-import |
| Copilot CLI | Full | Plugin wrapper, session hooks, auto-import from Copilot memory files |
| Cursor | Full | Plugin wrapper, SessionStart hook; run `/total-recall:commands compact` manually — no SessionEnd hook |
| OpenCode | Full | Plugin wrapper, auto-import from OpenCode project and agent files |
| Cline | Full | Auto-import from task history; MCP server config required |
| Hermes | Importer | Auto-import from SOUL.md and skills on first run; no session hooks |

---

## Commands

All commands are routed through the `/total-recall:commands` skill:

| Command | Description |
|---|---|
| `/total-recall:commands help` | Show command reference table |
| `/total-recall:commands status` | Dashboard overview |
| `/total-recall:commands search <query>` | Semantic search across all tiers |
| `/total-recall:commands store <content>` | Manually store a memory |
| `/total-recall:commands forget <query>` | Find and delete entries |
| `/total-recall:commands inspect <id>` | Deep dive on single entry with compaction history |
| `/total-recall:commands promote <id>` | Move entry to higher tier |
| `/total-recall:commands demote <id>` | Move entry to lower tier |
| `/total-recall:commands history` | Show recent tier movements |
| `/total-recall:commands lineage <id>` | Show compaction ancestry |
| `/total-recall:commands export` | Export to portable JSON format |
| `/total-recall:commands import <file>` | Import from export file |
| `/total-recall:commands ingest <path>` | Add files or directories to knowledge base |
| `/total-recall:commands kb search <query>` | Search knowledge base |
| `/total-recall:commands kb list` | List KB collections |
| `/total-recall:commands kb refresh <id>` | Re-ingest a collection |
| `/total-recall:commands kb remove <id>` | Remove KB entry |
| `/total-recall:commands compact` | Force compaction |
| `/total-recall:commands eval` | Retrieval quality metrics |
| `/total-recall:commands eval --benchmark` | Run synthetic benchmark |
| `/total-recall:commands eval --compare <name>` | Compare metrics between two config snapshots |
| `/total-recall:commands eval --snapshot <name>` | Manually create a named config snapshot |
| `/total-recall:commands eval --grow` | Review and accept/reject benchmark candidates from retrieval misses |
| `/total-recall:commands config get <key>` | Read config value |
| `/total-recall:commands config set <key> <val>` | Update config |
| `/total-recall:commands import-host` | Re-run import sync from all host tools |

Memory capture, retrieval, and compaction run automatically in the background — see the "Automatic Behavior" section of the `/total-recall:commands` skill.

> **Note:** `/total-recall:commands` is implemented as a Claude Code skill (at `skills/commands/SKILL.md`), not as a slash-command file under `commands/`. The skill handles all `<subcommand>` arguments internally.

---

## Configuration

The config file lives at `~/.total-recall/config.toml`. All fields have defaults — you only need to override what you want to change.

```toml
# total-recall configuration

[tiers.hot]
max_entries = 50                  # Max entries auto-injected per prompt
token_budget = 4000               # Max tokens for hot tier injection
carry_forward_threshold = 0.7     # Score threshold to stay in hot

[tiers.warm]
max_entries = 10000               # Max entries in warm tier
retrieval_top_k = 5               # Results returned per search
similarity_threshold = 0.65       # Min cosine similarity for retrieval
cold_decay_days = 30              # Days before unused warm entries decay to cold

[tiers.cold]
chunk_max_tokens = 512            # Max tokens per knowledge base chunk
chunk_overlap_tokens = 50         # Overlap between adjacent chunks
lazy_summary_threshold = 5        # Accesses before generating summary

[compaction]
decay_half_life_hours = 168       # Score half-life (168h = 1 week)
warm_threshold = 0.3              # Score below which warm→cold
promote_threshold = 0.7           # Score above which cold→warm
warm_sweep_interval_days = 7      # How often to run warm sweep

[search]
fts_weight = 0.3                  # BM25 weight in hybrid ranking (0.0 = vector only, 1.0 = FTS only)

[scope]
default = "user"                  # Default scope for new entries (e.g., "user", "team")

[usage]
initial_backfill_days = 30        # Days of usage history to backfill on first sync

[regression]
miss_rate_delta = 0.1             # Alert if miss rate increased by this much vs. previous snapshot
latency_ratio = 2.0               # Alert if latency increased by this factor vs. previous snapshot
min_events = 20                   # Minimum retrieval events required before regression check runs

[embedding]
model = "all-MiniLM-L6-v2"       # Embedding model name
dimensions = 384                  # Embedding dimensions
# provider = "local"              # "local" (default) | "openai" | "bedrock"
# endpoint = "https://api.openai.com/v1"   # OpenAI-compatible base URL
# bedrock_region = "us-east-1"             # Bedrock only
# bedrock_model = "cohere.embed-v4:0"      # Bedrock model ID
# api_key = ""                             # or set TOTAL_RECALL_EMBEDDING_API_KEY env var

# --- Skills (optional) ---
# [skills]
# extra_dirs = [
#   "~/my-skills",
#   "/path/to/team-skills"
# ]

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

Total Recall Cortex is the shared backend platform that adds team knowledge bases, connectors (Jira, Confluence, GitHub), chat/RAG, and a React UI on top of the plugin's memory layer.

In Cortex mode, the plugin operates as a hybrid:
- **User memories** are stored locally (fast reads/writes), synced bidirectionally to Cortex every 300 seconds and at session boundaries
- **Global knowledge** (team KB, connector-ingested data) is queried remotely from Cortex
- **Telemetry** (usage, retrieval events, compaction log) is pushed to Cortex for unified dashboards
- **Skills** are synced to Cortex so team members share the same skill library

Configure in your `config.toml`:

```toml
[storage]
mode = "cortex"

[cortex]
url = "https://your-cortex-instance.example.com"
pat = "tr_your_personal_access_token"
sync_interval_seconds = 300       # Background sync interval (default: 300)
```

Or via environment variables:

```bash
export TOTAL_RECALL_CORTEX_URL="https://your-cortex-instance.example.com"
export TOTAL_RECALL_CORTEX_PAT="tr_your_personal_access_token"
```

Generate a PAT from the Cortex web UI under Settings → Personal Access Tokens.

**Offline resilience:** If Cortex is unreachable, the plugin continues working locally. A persistent sync queue buffers outbound changes and flushes automatically when connectivity is restored.

### Skills

total-recall can advertise custom skills at every `session_start` so your AI assistant knows which workflows are available. Skills are discovered from two places:

- **`~/.claude/skills/`** — the standard Claude Code user skills directory (always scanned)
- **`extra_dirs`** — additional directories you configure, scanned on every session start regardless of whether Cortex is available

Configure extra skill directories in `~/.total-recall/config.toml`:

```toml
[skills]
extra_dirs = [
  "~/my-custom-skills",
  "/path/to/shared/team-skills"
]
```

Paths can be absolute or `~/`-prefixed. Skills in `extra_dirs` are always advertised from disk — Cortex is not required.

**Skill format:** Each skill is either a single `.md` file or a directory containing a `SKILL.md` entry point. A minimal single-file skill:

```markdown
---
name: my-skill
description: Does something useful
---

Full skill content here...
```

A bundle (directory with supporting files) uses the same frontmatter in its `SKILL.md`, and can include scripts, templates, or reference files alongside it.

**Merge behavior:** When Cortex is configured and reachable, the session context block merges cortex-stored skills with locally-scanned `extra_dirs` skills, deduplicating by name (Cortex entries take precedence). When Cortex is unavailable or not configured, only local skills appear.

---

## Developer Reference

The MCP server exposes 33 tools in local/Postgres mode and 38 in Cortex mode (adds 5 skill tools). All tool names follow the pattern `<domain>_<action>`.

| Category | Tools |
|---|---|
| Session | `session_start`, `session_end`, `session_context` |
| Memory | `memory_store`, `memory_get`, `memory_update`, `memory_delete`, `memory_inspect`, `memory_search` |
| Tier management | `memory_promote`, `memory_demote`, `memory_history`, `memory_lineage` |
| Import / Export | `memory_export`, `memory_import`, `import_host` |
| Knowledge base | `kb_ingest_file`, `kb_ingest_dir`, `kb_search`, `kb_list_collections`, `kb_refresh`, `kb_remove`, `kb_summarize` |
| Compaction | `compact_now` |
| Eval | `eval_report`, `eval_benchmark`, `eval_compare`, `eval_snapshot`, `eval_grow` |
| Config | `config_get`, `config_set` |
| Status & Usage | `status`, `usage_status`† |
| Migration | `migrate_to_remote` |
| Skills *(Cortex mode)* | `skill_search`, `skill_get`, `skill_list`, `skill_delete`, `skill_import_host` |

†`usage_status` is unavailable in Postgres mode.

Handler implementations live in `src/TotalRecall.Server/Handlers/<ToolName>Handler.cs`. Tool wiring: `src/TotalRecall.Server/ServerComposition.cs → BuildRegistry()`.

---

## Architecture

```
MCP Server (.NET 8 NativeAOT — C# imperative shell + F# functional core)
├── TotalRecall.Core (F#)        — pure functions: tokenizer, decay, hybrid ranking, parsers, chunker
├── TotalRecall.Infrastructure   — SQLite/Postgres storage, ONNX/remote embedder, importers, migrations
├── TotalRecall.Server           — MCP JSON-RPC server, 33 tool handlers (38 in Cortex mode), lifecycle
├── TotalRecall.Cli              — CLI commands (status, eval, kb, memory, config, migrate)
└── TotalRecall.Host             — composition root, AOT entry point, migration guard

Tiers:
  Hot (50 entries)   → auto-injected every prompt
  Warm (10K entries) → BM25 + cosine hybrid search per query
  Cold (unlimited)   → hierarchical KB retrieval

Backends (selected by config):
  Local:    SQLite + sqlite-vec + bundled ONNX embedder (default, zero config)
  Postgres: Postgres/pgvector + HNSW indexes + tsvector FTS + per-user visibility
  Cortex:   Local SQLite + write-local-then-enqueue sync to Cortex; remote queries for global KB
```

**Data flow:**

1. `store` — write a memory, assign tier, embed, persist
2. `search` — embed query, BM25 + cosine vector search across all tiers, merge with F# ranking, return results
3. `compact` — decay scores, promote hot→warm, demote warm→cold
4. `ingest` — chunk files with heading-aware Markdown and regex-based code parsing, embed chunks, store in cold tier

**Local mode:** all state lives in `~/.total-recall/total-recall.db`. The embedding model and the sqlite-vec native extension are bundled with the binary. No network calls required at runtime.

**Cortex mode:** user memories write locally first for low latency. A `RoutingStore` wraps every write: persist locally, enqueue to `sync_queue`. A background sync loop flushes the queue to Cortex every `sync_interval_seconds` (default: 300) and at session boundaries. Global knowledge (team KB, connectors) is read directly from Cortex.

---

## Prerequisites

These apply only if you're building from source. The prebuilt binary is self-contained — no .NET runtime, no system SQLite, no Bun required.

- **.NET 10 SDK** — pinned by `global.json` at the repo root; builds the `net8.0` NativeAOT target
- **npm** — for `npm ci`, which pulls `sqlite-vec` native libs needed by the csproj copy targets
- **Git LFS** — run `git lfs install` before cloning; the ONNX embedding model is stored in LFS. If LFS fetch fails, the binary auto-downloads the model from HuggingFace on first run.

---

## Installation from Source

```bash
git clone https://github.com/strvmarv/total-recall.git
cd total-recall
git lfs pull                               # fetch the ONNX model
npm ci                                     # pulls sqlite-vec native libs into node_modules/
dotnet build src/TotalRecall.sln
dotnet test src/TotalRecall.sln --filter "Category!=Integration"   # ~1000 tests
dotnet publish src/TotalRecall.Host/TotalRecall.Host.csproj -c Release -r win-x64 -p:PublishAot=true
# (swap win-x64 for your RID: linux-x64, linux-arm64, osx-arm64)
```

The publish output lands in `src/TotalRecall.Host/bin/Release/net8.0/<rid>/publish/` with the binary plus all sibling native libs (`libonnxruntime.*`, `libe_sqlite3.*`, `runtimes/vec0.*`) ready to run.

Supported RIDs: `linux-x64`, `linux-arm64`, `osx-arm64`, `win-x64`. Intel Mac (`osx-x64`) is not shipped.

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for the full contributor guide, including how to add a new host importer, extend the chunking pipeline, or add a new MCP tool handler.

---

## Built With & Inspired By

### [superpowers](https://github.com/obra/superpowers) by [obra](https://github.com/obra)

total-recall's plugin architecture, skill format, hook system, multi-platform wrapper pattern, and development philosophy are directly inspired by and modeled after the **superpowers** plugin. superpowers demonstrated that a zero-dependency, markdown-driven skill system could fundamentally improve how AI coding assistants behave — total-recall extends that same philosophy to memory and knowledge management.

If you're building plugins for TUI coding assistants, start with [superpowers](https://github.com/obra/superpowers). It's the foundation this ecosystem needs.

### Core Technologies

- [.NET 8 / NativeAOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/) — single-binary deployment, no runtime dependency
- [F# Core](https://learn.microsoft.com/en-us/dotnet/fsharp/) — pure functional core: tokenizer, parsers, decay, hybrid ranking
- [Microsoft.Data.Sqlite](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/) — embedded SQLite with extension loading
- [sqlite-vec](https://github.com/asg017/sqlite-vec) — vector similarity search in SQLite (loaded as a native extension via `LoadExtension`)
- [Microsoft.ML.OnnxRuntime](https://onnxruntime.ai/docs/get-started/with-csharp.html) — local ML inference, AOT-compatible
- [Microsoft.ML.Tokenizers](https://learn.microsoft.com/en-us/dotnet/api/microsoft.ml.tokenizers) — canonical BERT BasicTokenization + WordPiece
- [all-MiniLM-L6-v2](https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2) — sentence embeddings (384d)
- Hand-rolled JSON-RPC stdio MCP server in `TotalRecall.Server` (no SDK dependency)
- [Spectre.Console](https://spectreconsole.net/) — CLI rendering for `total-recall status` / `eval` / `kb list`

---

## License

MIT — see [LICENSE](LICENSE)
