# total-recall

**Multi-tiered memory and knowledge base for TUI coding assistants.**

Your AI coding tool forgets everything. total-recall doesn't.

A cross-platform plugin that gives Claude Code, GitHub Copilot CLI, OpenCode, Cline, and Cursor persistent, semantically searchable memory with a hierarchical knowledge base — backed by local SQLite + vector embeddings, zero external dependencies.

---

## The Problem

Every TUI coding assistant has the same gap:

- **No tiering** — all memories treated equally, leading to context bloat or information loss
- **Tool-locked** — switching between Claude Code and Copilot means starting from scratch
- **No knowledge base** — can't ingest your docs and have them retrieved when relevant
- **No semantic search** — memories retrieved by filename, not by meaning
- **No observability** — no way to know if memory is helping or just noise

---

## The Solution

total-recall introduces a three-tier memory model: **Hot** memories (up to 50 entries) are auto-injected into every prompt so your most important context is always present. **Warm** memories (up to 10K entries) are retrieved semantically — when you ask about authentication, relevant auth memories surface automatically. **Cold** storage is unlimited hierarchical knowledge base: ingest your docs, README files, API references, and architecture notes, and they're retrieved when relevant.

The knowledge base ingests entire directories — source trees, documentation folders, design specs — and chunks them semantically with heading-aware Markdown parsing and AST-based code parsing. Every chunk is embedded with `all-MiniLM-L6-v2` (384 dimensions, runs locally via ONNX) so retrieval is purely semantic, no keyword matching required.

Platform support is via MCP (Model Context Protocol), which means total-recall works with any MCP-compatible tool. Dedicated importers for Claude Code and Copilot CLI mean your existing memories migrate automatically on first run. An eval framework lets you measure retrieval quality, run benchmarks, and compare configuration changes before committing them.

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

This works with **Copilot CLI**, **OpenCode**, **Cline**, **Cursor**, and any other MCP-compatible tool.

> **Note:** `npx -y @strvmarv/total-recall` does not work due to an [npm bug](https://github.com/npm/cli/issues/3753) with scoped package binaries. Use the global install (`total-recall` command) instead.

### From Source

```bash
git clone https://github.com/strvmarv/total-recall.git
cd total-recall
npm install && npm run build
```

### First Session

total-recall auto-initializes on first use:

1. Creates `~/.total-recall/` with SQLite database
2. Loads bundled embedding model (included in package, no download needed)
3. Scans for existing memories (Claude Code, Copilot CLI)
4. Auto-ingests project docs (README, docs/, etc.)
5. Reports: `total-recall: initialized · 4 memories imported · 12 docs ingested · system verified`

---

## Architecture

```
MCP Server (Node.js/TypeScript)
├── Always Loaded: SQLite + vec, MCP Tools, Event Logger
├── Lazy Loaded: ONNX Embedder, Compactor, Ingestor
└── Host Importers: Claude Code, Copilot CLI, OpenCode

Tiers:
  Hot (50 entries)  → auto-injected every prompt
  Warm (10K entries) → semantic search per query
  Cold (unlimited)   → hierarchical KB retrieval
```

**Data flow:**

1. `store` — write a memory, assign tier, embed, persist
2. `search` — embed query, vector search warm + cold, return ranked results
3. `compact` — decay scores, demote warm→cold, evict hot→warm, summarize clusters
4. `ingest` — chunk files, embed chunks, store in cold tier with metadata

All state lives in `~/.total-recall/db.sqlite`. The embedding model is bundled with the package. No network calls required.

---

## Commands

All commands use `/total-recall <subcommand>`:

| Command | MCP Tool | Description |
|---|---|---|
| `/total-recall status` | `status` | Dashboard overview |
| `/total-recall search <query>` | `memory_search` | Semantic search across all tiers |
| `/total-recall store <content>` | `memory_store` | Manually store a memory |
| `/total-recall forget <query>` | `memory_search` + `memory_delete` | Find and delete entries |
| `/total-recall inspect <id>` | `memory_inspect` | Deep dive on single entry |
| `/total-recall promote <id>` | `memory_promote` | Move entry to higher tier |
| `/total-recall demote <id>` | `memory_demote` | Move entry to lower tier |
| `/total-recall history` | `memory_history` | Show recent tier movements |
| `/total-recall lineage <id>` | `memory_lineage` | Show compaction ancestry |
| `/total-recall export` | `memory_export` | Export to portable JSON format |
| `/total-recall import <file>` | `memory_import` | Import from export file |
| `/total-recall ingest <path>` | `kb_ingest_file` / `kb_ingest_dir` | Add files/dirs to knowledge base |
| `/total-recall kb search <query>` | `kb_search` | Search knowledge base |
| `/total-recall kb list` | `kb_list_collections` | List KB collections |
| `/total-recall kb refresh <id>` | `kb_refresh` | Re-ingest a collection |
| `/total-recall kb remove <id>` | `kb_remove` | Remove KB entry |
| `/total-recall compact` | `compact_now` | Force compaction |
| `/total-recall eval` | `eval_report` | Live performance metrics |
| `/total-recall eval --benchmark` | `eval_benchmark` | Run synthetic benchmark |
| `/total-recall config get <key>` | `config_get` | Read config value |
| `/total-recall config set <key> <val>` | `config_set` | Update config |
| `/total-recall import-host` | `import_host` | Import from host tools |

Memory capture, retrieval, and compaction run automatically in the background — see the "Automatic Behavior" section of the `/total-recall` skill.

---

## Supported Platforms

| Platform | Support | Notes |
|---|---|---|
| Claude Code | Full | Native plugin, session hooks, auto-import |
| Copilot CLI | Full | Auto-import from existing Copilot memory files |
| OpenCode | MCP | Configure MCP server in opencode config |
| Cline | MCP | Configure MCP server in Cline settings |
| Cursor | Full | MCP server + `.cursor-plugin/` wrapper |

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
```

---

## Extending

### Adding a New Host Tool

Implement the `HostImporter` interface (~50 lines). It requires four methods: `detect()` to check if the tool is present, `scan()` to report what's available, `importMemories()` to migrate existing memories, and `importKnowledge()` to migrate knowledge files. See [CONTRIBUTING.md](CONTRIBUTING.md) for a full example.

### Adding a New Content Type

Add a row to the `content_type` lookup table in `src/db/schema.ts`, then register a type weight for relevance scoring. No other changes needed.

### Adding a New Chunking Parser

Implement the `Chunk[]`-returning parser interface and register it in `src/ingestion/chunker.ts` alongside the existing Markdown and code parsers. See [CONTRIBUTING.md](CONTRIBUTING.md) for the interface definition.

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

- [better-sqlite3](https://github.com/WiseLibs/better-sqlite3) — Fast, synchronous SQLite bindings
- [sqlite-vec](https://github.com/asg017/sqlite-vec) — Vector similarity search in SQLite
- [onnxruntime-node](https://github.com/microsoft/onnxruntime) — Local ML inference
- [all-MiniLM-L6-v2](https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2) — Sentence embeddings (384d)
- [@modelcontextprotocol/sdk](https://github.com/modelcontextprotocol/sdk) — MCP server implementation

---

## License

MIT — see [LICENSE](LICENSE)
