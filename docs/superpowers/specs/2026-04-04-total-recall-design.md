# total-recall: Multi-Tiered Memory & Knowledge Base Plugin

**Date:** 2026-04-04
**Status:** Design approved, pending implementation plan
**Author:** gomanjoe + Claude

## Overview

total-recall is a cross-platform plugin for TUI coding assistants (Claude Code, GitHub Copilot CLI, OpenCode, Cline, Cursor) that provides multi-tiered memory with semantic search and a hierarchical knowledge base. It is backed by a local SQLite database with vector embeddings, requires zero configuration to start delivering value, and includes a built-in evaluation framework for tuning and regression detection.

### Core Principles

- **Zero-config magic** — works from session one, no API keys, no external services
- **Portable** — single SQLite file, MCP server as the universal interface
- **Transparent** — every memory is inspectable, traceable to its source, auditable
- **Measurable** — built-in eval framework from day one, not bolted on later
- **Forkable** — clear architecture, obvious extension points, thin host adapters

### What Problem This Solves

Existing TUI coding tools have fragmented, flat, tool-specific memory:

1. **No tiering** — all memories treated equally, leading to context bloat
2. **Tool-locked** — switching tools means losing accumulated context
3. **No knowledge base** — can't ingest docs and retrieve them semantically
4. **No semantic search** — retrieval by filename/index, not relevance
5. **No observability** — no way to know if memory is helping or just noise

total-recall solves all five by providing a unified memory and knowledge layer that sits beneath any MCP-capable coding tool.

---

## 1. System Architecture

### Monolith MCP Server with Lazy Loading

Single Node.js/TypeScript process. Heavy components (ONNX embedding model, compaction logic, ingestion pipeline) load on first use, not at startup.

```
+-----------------------------------------------------------+
|              total-recall MCP Server (Node.js)             |
|                                                           |
|  +-----------------------------------------------------+  |
|  |              Always Loaded                           |  |
|  |  SQLite Manager    MCP Tool Handlers    Event Logger |  |
|  |  (better-sqlite3   (30+ tools)          (every query |  |
|  |   + sqlite-vec)                          tracked)    |  |
|  +-----------------------------------------------------+  |
|                                                           |
|  +-----------------------------------------------------+  |
|  |              Lazy Loaded (on demand)                  |  |
|  |  Embedder          Compactor          Ingestor       |  |
|  |  (ONNX Runtime +   (tier mgmt,       (chunking,     |  |
|  |   all-MiniLM-L6)   LLM summarize)    hierarchical)  |  |
|  +-----------------------------------------------------+  |
|                                                           |
|  +-----------------------------------------------------+  |
|  |              Host Importers                           |  |
|  |  Claude Code    Copilot CLI    OpenCode    (more)    |  |
|  +-----------------------------------------------------+  |
+----------------------------+------------------------------+
                             | MCP Protocol (stdio/SSE)
              +--------------+--------------+
              |              |              |
         Claude Code    Copilot CLI    Cline/Cursor
         (skills+hooks  (plugins+MCP)  (MCP config)
          +MCP)

```

**Why monolith over daemon:** Simple to install, debug, distribute. No IPC, no concurrent write contention. Session-boundary compaction is a feature, not a limitation — the LLM is available for intelligent summarization at that moment. The path to a background daemon exists later if needed (extract lazy modules) but is unlikely to be necessary.

**Why lazy loading:** ONNX model is ~80MB in memory. Loading it takes ~200ms. By deferring until the first embedding call, startup stays fast. Once loaded, the model is cached for the session lifetime.

### File Layout

```
~/.total-recall/
  total-recall.db          # Single SQLite database (everything)
  models/
    all-MiniLM-L6-v2/      # ONNX model files (~80MB)
  config.toml              # User overrides (optional, auto-created)
  exports/                 # Export files from /memory export
```

### Database Schema

**Content tables** — one pair per tier x content type:

```sql
-- Six content tables (hot/warm/cold x memory/knowledge)
-- Each follows this schema:
CREATE TABLE {tier}_{type} (
  id TEXT PRIMARY KEY,
  content TEXT NOT NULL,
  summary TEXT,                    -- LLM-generated for compacted entries
  source TEXT,                     -- "claude-code/memory/user_bg.md"
  source_tool TEXT,                -- "claude-code" | "copilot" | "manual"
  project TEXT,                    -- project scope (NULL = global)
  tags TEXT,                       -- JSON array for filtering
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  last_accessed_at INTEGER NOT NULL,
  access_count INTEGER DEFAULT 0,
  decay_score REAL DEFAULT 1.0,
  parent_id TEXT,                  -- chunk -> document (knowledge only)
  collection_id TEXT,              -- document -> collection (knowledge only)
  metadata TEXT                    -- JSON blob for extensibility
);

-- Paired vector tables
CREATE VIRTUAL TABLE {tier}_{type}_vec
  USING vec0(embedding float[384]);
```

Tables: `hot_memories`, `hot_knowledge`, `warm_memories`, `warm_knowledge`, `cold_memories`, `cold_knowledge`.

Separate tables per tier+type for performance — `sqlite-vec` scans the full virtual table on every query, so smaller tables mean faster retrieval.

**System tables:**

```sql
CREATE TABLE retrieval_events (
  id TEXT PRIMARY KEY,
  timestamp INTEGER NOT NULL,
  session_id TEXT NOT NULL,
  query_text TEXT NOT NULL,
  query_source TEXT NOT NULL,        -- 'auto' | 'explicit' | 'session_start'
  query_embedding BLOB,
  results TEXT NOT NULL,             -- JSON array of {entry_id, tier, content_type, score, rank}
  result_count INTEGER,
  top_score REAL,                    -- denormalized for fast queries
  top_tier TEXT,                     -- denormalized for fast queries
  top_content_type TEXT,             -- denormalized for fast queries
  outcome_used INTEGER,              -- 0/1
  outcome_signal TEXT,               -- 'positive' | 'negative' | 'neutral'
  config_snapshot_id TEXT NOT NULL,
  latency_ms INTEGER,
  tiers_searched TEXT,               -- JSON array
  total_candidates_scanned INTEGER
);

CREATE TABLE compaction_log (
  id TEXT PRIMARY KEY,
  timestamp INTEGER NOT NULL,
  session_id TEXT,
  source_tier TEXT NOT NULL,
  target_tier TEXT,                   -- NULL if discarded
  source_entry_ids TEXT NOT NULL,     -- JSON array
  target_entry_id TEXT,               -- NULL if discarded
  semantic_drift REAL,
  facts_preserved INTEGER,
  facts_in_original INTEGER,
  preservation_ratio REAL,
  decay_scores TEXT,                  -- JSON: {entry_id: score}
  reason TEXT NOT NULL,
  config_snapshot_id TEXT NOT NULL
);

CREATE TABLE config_snapshots (
  id TEXT PRIMARY KEY,
  name TEXT,
  timestamp INTEGER NOT NULL,
  config TEXT NOT NULL               -- JSON of full config at snapshot time
);

CREATE TABLE import_log (
  id TEXT PRIMARY KEY,
  timestamp INTEGER NOT NULL,
  source_tool TEXT NOT NULL,
  source_path TEXT NOT NULL,
  content_hash TEXT NOT NULL,        -- for dedup
  target_entry_id TEXT NOT NULL,
  target_tier TEXT NOT NULL,
  target_type TEXT NOT NULL
);
```

### Embedding Configuration

- **Default model:** `all-MiniLM-L6-v2` via `onnxruntime-node`
- **Dimensions:** 384 (fixed at schema level, changing requires re-embed + table recreation)
- **Pluggable:** config supports alternative models, but dimension change is a migration
- **Performance:** ~5-10ms per embedding on CPU, model cached after first load

---

## 2. Multi-Tiered Memory System

### Tier Definitions

| Tier | Max Size | Injection Strategy | Retrieval Method | Compaction Trigger |
|---|---|---|---|---|
| **Hot** | 50 entries / 4000 tokens (configurable) | Auto-injected into every prompt | In-memory, no embedding search | Session end, or budget exceeded |
| **Warm** | 10,000 entries | Top-K semantic search per query | Embedding similarity, project-scoped | Weekly decay sweep |
| **Cold** | Unlimited | On-demand semantic search | Hierarchical: collection -> document -> chunk | Manual cleanup only |

### Hot Tier — Context Window Guardian

Hot tier is assembled at session start and maintained throughout the session. Its budget is strictly enforced to prevent context bloat.

**Session start assembly:**

1. Load persisted hot entries marked "carry forward" from last session
2. Semantic search warm tier using project context (project name, branch, recent commit messages, file paths in cwd) to pull relevant warm entries
3. Load pinned knowledge (CLAUDE.md equivalents)
4. Budget check: if total tokens exceed `hot_budget`, drop lowest `decay_score` entries

Result: ~2000-4000 tokens of high-signal context injected via system prompt.

**Within-session updates:**

| Event | Action |
|---|---|
| User corrects the LLM | Immediate hot entry (type: correction) |
| User states a preference | Immediate hot entry (type: preference) |
| LLM makes non-obvious decision | Hot entry (type: decision) |
| Warm/cold retrieval surfaces a result | Temporary hot entry (type: surfaced) — doesn't permanently consume budget |
| Hot tier exceeds budget | Lowest decay_score entries evicted to pending_compaction queue |

### Warm Tier — Working Memory

Where most value accumulates. Contains:
- Compacted summaries from hot tier sessions
- Imported host tool memories
- Frequently accessed cold entries promoted up
- Cross-project user preferences and patterns

**Retrieval:** On each user message, semantic search warm tier scoped to:
1. Current project (exact match)
2. Global entries (project = NULL)
3. Related projects (if configured)

Top-K results (default K=5) appended to hot tier as `type: surfaced`.

### Cold Tier — Knowledge Base

Bulk storage for ingested content. Uses hierarchical chunking with three levels:

**Level 0 — Collection:** A group of related documents (e.g., "auth-docs"). Holds a summary embedding covering the entire domain. Generated lazily after 5+ documents.

**Level 1 — Document:** A single ingested file. Holds a summary embedding of the document. Generated lazily on first retrieval.

**Level 2 — Chunk:** A semantically-bounded section of a document (heading, code block, paragraph group). Always generated at ingest time.

**Hierarchical retrieval flow:**

1. Search collection summaries (scan ~10-20 embeddings) -> top-K collections
2. Within matched collections, search document summaries (~5-10 embeddings) -> top-K documents
3. Within matched documents, search chunks (~10-30 embeddings) -> final results

Three small searches instead of one massive scan. Faster, more accurate — document-level context prevents false matches.

### Tier Movement — Compaction Pipeline

**Hot -> Warm (session end):**

For each hot entry, calculate `decay_score`:

```
decay_score = time_factor(age) x freq_factor(accesses) x type_weight

time_factor  = exp(-hours_since_access / decay_half_life_hours)
freq_factor  = 1 + log2(1 + access_count)
type_weight  = { correction: 1.5, preference: 1.3, decision: 1.0, surfaced: 0.8 }
```

| Score Range | Action |
|---|---|
| > `promote_threshold` (0.7) | Stays hot (carry forward to next session) |
| `warm_threshold` (0.3) to `promote_threshold` (0.7) | LLM summarizes related entries into warm entry. Semantic drift measured. |
| < `warm_threshold` (0.3) | Discarded (logged with reason) |

**Warm -> Cold (weekly sweep):**

Recalculate decay scores. Entries below `cold_threshold` with no access in 30+ days move to cold. Full content preserved — no summarization loss.

**Cold -> Warm (promotion):**

When a cold entry is retrieved 3+ times in 7 days, a copy is promoted to warm. Original stays in cold.

### Configuration Defaults

```toml
[tiers.hot]
max_entries = 50
token_budget = 4000
carry_forward_threshold = 0.7

[tiers.warm]
max_entries = 10000
retrieval_top_k = 5
similarity_threshold = 0.65
cold_decay_days = 30

[tiers.cold]
chunk_max_tokens = 512
chunk_overlap_tokens = 50
lazy_summary_threshold = 5

[compaction]
decay_half_life_hours = 168
warm_threshold = 0.3
promote_threshold = 0.7
warm_sweep_interval_days = 7

[embedding]
model = "all-MiniLM-L6-v2"
dimensions = 384
```

All values tunable. Every change auto-creates a config snapshot for the eval framework.

---

## 3. MCP Tools and Skill Layer

### MCP Tools — The Hands

Callable functions exposed over MCP protocol. Any host tool can invoke them.

**Memory operations:**

| Tool | Description |
|---|---|
| `memory_store` | Store new entry in hot tier `{content, type, project?, tags?}` |
| `memory_search` | Semantic search across tiers `{query, tiers?, content_types?, top_k?, project?}` |
| `memory_get` | Retrieve specific entry by ID |
| `memory_update` | Modify existing entry `{id, content?, tags?, tier?}` |
| `memory_delete` | Remove entry (logged, not hard delete) `{id, reason?}` |
| `memory_promote` | Move entry to higher tier `{id, target_tier}` |
| `memory_demote` | Move entry to lower tier `{id, target_tier}` |

**Knowledge base operations:**

| Tool | Description |
|---|---|
| `kb_ingest_file` | Ingest single file `{path, collection?}` |
| `kb_ingest_dir` | Ingest directory recursively `{path, glob?, collection?}` |
| `kb_search` | Hierarchical search `{query, collection?, top_k?}` |
| `kb_list_collections` | List collections with stats |
| `kb_remove` | Remove document or collection `{id, cascade?}` |
| `kb_refresh` | Re-ingest changed files in collection |

**System operations:**

| Tool | Description |
|---|---|
| `status` | TUI dashboard: tier sizes, health, activity |
| `eval_benchmark` | Run synthetic benchmark suite `{compare_to?, snapshot?}` |
| `eval_report` | Live retrieval performance metrics `{days?, config_snapshot?}` |
| `compact_now` | Force compaction on specified tier |
| `config_get` | Read config value |
| `config_set` | Update config (auto-snapshots) |
| `import_host` | Import from detected host tool `{source, scope?}` |
| `export` | Export to portable format `{format, tiers?, types?}` |

**Session lifecycle (called by hooks):**

| Tool | Description |
|---|---|
| `session_start` | Assemble hot tier, detect context, sync imports |
| `session_end` | Run compaction, log session stats |
| `session_context` | Return current hot tier as injectable context |

### Skills — The Brain

Skills define policy: when and how tools get called. Platform-specific markdown files.

**`total-recall:memory`** — Core skill, injected at SessionStart. Governs automatic behavior:
- Session start: call `session_start`, inject hot tier context
- During conversation: detect corrections/preferences -> `memory_store`; query warm tier on each user message; fall back to KB search when warm has no results
- Session end: call `session_end` for compaction

**`total-recall:search`** — Explicit search via `/memory search <query>`. Searches all tiers, formats results grouped by tier/type with scores and source attribution.

**`total-recall:ingest`** — Knowledge base management via `/memory ingest <path>`. Detects file type, chunks semantically, embeds, validates with test queries.

**`total-recall:status`** — Observability via `/memory status` and `/memory eval`. TUI dashboard, live metrics, benchmark runner, regression detection.

**`total-recall:forget`** — Data control via `/memory forget <query>`. Search, present matches, user selects, delete with reason tracking. Never touches host tool source files.

### Platform Wrappers

```
total-recall/
  .claude-plugin/
    plugin.json              # Claude Code manifest
  .copilot-plugin/
    plugin.json              # Copilot CLI manifest
  .cursor-plugin/
    plugin.json              # Cursor manifest
  .opencode/
    INSTALL.md               # OpenCode setup
  skills/
    memory/SKILL.md
    search/SKILL.md
    ingest/SKILL.md
    status/SKILL.md
    forget/SKILL.md
  hooks/
    hooks.json               # Claude Code hooks
    hooks-cursor.json        # Cursor hooks
    session-start/           # SessionStart hook script
  agents/
    compactor.md             # Subagent for LLM-driven compaction
  server/                    # MCP server (TypeScript)
    index.ts
    tools/
    db/
    embedding/
    compaction/
    ingestion/
    importers/
    eval/
  eval/
    corpus/                  # Seed data for benchmarks
    benchmarks/              # Query -> expected result pairs
  package.json
```

### Hook -> Skill -> Tool Flow

1. **SessionStart hook** fires -> injects `total-recall:memory` skill content -> calls `session_start` MCP tool -> returns hot tier context as system prompt
2. **Skill** monitors conversation -> detects corrections/preferences -> calls `memory_store` -> triggers `memory_search` on user messages -> logs retrieval events
3. **Session end hook** fires -> calls `session_end` -> compactor reviews hot tier -> promotes/discards/merges -> logs compaction events -> updates retrieval event outcomes

---

## 4. Ingestion Pipeline and Host Tool Importers

### Ingestion Pipeline

**Format detection:**

| Format | Parser | Chunk Boundaries |
|---|---|---|
| `.md` | Markdown (heading-aware) | Headings, code blocks, paragraph groups |
| `.ts/.js/.py/.go` etc. | Code (AST-aware where possible) | Function/class boundaries |
| `.txt` | Paragraph-based | Double newlines |
| `.pdf` | PDF extraction | Page/section boundaries |
| `.json/.yaml` | Key-path aware | Top-level keys |
| `.jsonl` | Per-line entry | Each line |
| Unknown | Fixed-size fallback | Every N tokens with overlap |

**Chunking rules:**
- Max chunk: 512 tokens (configurable)
- Overlap: 50 tokens at boundaries
- Never split mid-code-block
- Never split mid-paragraph if avoidable
- Preserve heading path per chunk (e.g., `["API Reference", "Auth", "OAuth Flow"]`)

**Processing flow:**

1. Detect format, select parser
2. Split into semantic chunks with heading paths
3. Generate embedding per chunk via ONNX MiniLM
4. Insert into `cold_knowledge` + `cold_knowledge_vec`
5. Link chunks to parent document via `parent_id`
6. Link documents to collection via `collection_id`
7. Document and collection summaries generated lazily on first retrieval

**Post-ingest validation:** Three auto-generated test queries per document:
1. Document title as query -> should match
2. Random chunk content -> should self-match
3. Semantic paraphrase -> should fuzzy match

### Host Tool Importers

Common interface:

```typescript
interface HostImporter {
  name: string;
  detect(): Promise<boolean>;
  scan(): Promise<ImportScanResult>;
  importMemories(): Promise<ImportResult>;
  importKnowledge(): Promise<ImportResult>;
  importHistory(consent: boolean): Promise<ImportResult>;
  watch(): AsyncIterable<FileChangeEvent>;
}
```

**Claude Code importer:**

| Source | Mapping | Tier |
|---|---|---|
| `memory/*.md` (type: user) | Memory | Warm |
| `memory/*.md` (type: feedback) | Memory | Warm |
| `memory/*.md` (type: project) | Memory | Warm |
| `memory/*.md` (type: reference) | Knowledge | Cold |
| `CLAUDE.md` (global) | Knowledge, pinned | Warm |
| `CLAUDE.md` (project) | Knowledge, pinned, project-scoped | Warm |
| `*.jsonl` sessions (opt-in) | Mined for corrections/preferences/decisions | Warm |

Ongoing sync: watches `~/.claude/projects/*/memory/` for changes. New files imported, modified files updated and re-embedded, deleted files flagged as orphaned (not auto-deleted).

**Copilot CLI importer:**

| Source | Mapping | Tier |
|---|---|---|
| `session-state/*/events.jsonl` | Mined for patterns (opt-in) | Warm |
| `session-state/*/plan.md` | Knowledge, project-scoped | Cold |
| `session-state/*/checkpoints/` | Inspected for decisions | Cold |

No persistent memory exists in Copilot CLI — session mining is the primary value.

**OpenCode / Cline / Cursor importers:**

Follow the same `HostImporter` interface. Each is a thin adapter over the shared mining pipeline:

```
Raw JSONL -> FieldMapper (per tool, ~50 lines) -> NormalizedMsg (common format)
NormalizedMsg -> PatternExtractor (shared LLM logic) -> Memory Entries
```

Adding support for a new host tool = writing one FieldMapper. This is the primary community extension point.

### Import Safety Principles

1. **Never modify source data** — total-recall reads, never writes to host tool directories
2. **All imports traceable** — `import_log` table tracks source path, timestamp, content hash
3. **Session history requires explicit consent** — prompted on first run, choice persisted
4. **Deduplication on every sync** — content hash prevents duplicates; updates don't duplicate
5. **Orphan detection** — deleted source files flagged, user decides whether to keep or remove

---

## 5. Evaluation Framework and Observability

### Retrieval Event Tracking

Every retrieval is logged with full context:

```typescript
interface RetrievalEvent {
  id: string;
  timestamp: number;
  session_id: string;
  query_text: string;
  query_source: 'auto' | 'explicit' | 'session_start';
  query_embedding: Float32Array;
  results: {
    entry_id: string;
    tier: 'hot' | 'warm' | 'cold';
    content_type: 'memory' | 'knowledge';
    score: number;
    rank: number;
    content_preview: string;
  }[];
  latency_ms: number;
  tiers_searched: string[];
  total_candidates_scanned: number;
  outcome: {
    injected: boolean;
    used: boolean;
    feedback_signal?: 'positive' | 'negative' | 'neutral';
  };
  config_snapshot_id: string;
}
```

### Outcome Detection

Four signals for determining whether a retrieval was useful:

1. **Direct reference** — LLM response contains key terms from retrieved chunk. Confidence: HIGH.
2. **Behavioral alignment** — LLM actions align with retrieved correction/preference. Confidence: MEDIUM.
3. **Explicit acknowledgment** — LLM attributes response to retrieved context. Confidence: HIGH.
4. **Negative signal** — LLM contradicts retrieved content, or user corrects on something in context. Confidence: HIGH (negative).

No signal detected = outcome: neutral.

### Core Metrics

**Retrieval quality:**

| Metric | Formula | What It Means |
|---|---|---|
| Precision@K | (results marked 'used') / (results injected) | Of what we surfaced, how much was useful? |
| Hit Rate | (queries with >=1 used result) / (total queries) | How often did we find something useful? |
| Miss Rate | (queries with best score < threshold) / (total) | How often did we come up empty? |
| MRR | avg(1 / rank of first used result) | Is the best result showing up first? |

**Compaction quality:**

| Metric | Formula | Target |
|---|---|---|
| Preservation Ratio | avg(facts_preserved / facts_in_original) | > 0.90 |
| Semantic Drift | avg(cosine_distance(original, summary)) | < 0.15 |
| Post-Compact Delta | precision_after - precision_before | >= 0.00 |

**System health:**
- Tier distribution over time (hot stable, warm/cold growing)
- Promotion/demotion rate (spikes indicate config issues)
- Embedding latency p50/p95/p99 (should stay < 20ms)
- DB size growth (MB/week, by tier)

### Synthetic Benchmark Suite

Ships with total-recall:

```
eval/
  corpus/
    memories.jsonl         # ~200 synthetic memories across tiers
    knowledge/             # Sample docs (markdown, code, API refs)
    corrections.jsonl      # Simulated user corrections
  benchmarks/
    retrieval.jsonl        # query -> expected top-K entry IDs
    compaction.jsonl       # pre-compaction state -> expected post-compaction
    cross-tier.jsonl       # queries that should hit specific tiers
  runner.ts                # Benchmark harness
```

**Three uses:**

1. **Development validation** — run in CI on every PR. Regression = build failure.
2. **First-install calibration** — 20-query smoke test at first startup (~2 seconds) to verify embedding model and vector search are functional.
3. **Tuning without real data** — change config, run benchmark, compare instantly.

### Evolving Benchmarks (`--grow`)

The benchmark suite starts synthetic but grows from real usage:

- Recent misses with no matching entry -> skip (coverage gap, not quality issue)
- False positives (high score, negative outcome) -> add as negative benchmark
- True positives (confirmed useful) -> add to prevent regression
- Boundary cases (just above/below threshold) -> add as edge tests

After a month: 150 synthetic + ~50 real-world cases.

### A/B Config Comparison

Every retrieval event is tagged with `config_snapshot_id`. Changing a config value auto-creates a new snapshot. Comparison queries are `GROUP BY config_snapshot_id`.

Commands:
- `/memory eval --snapshot <name>` — save current config as named baseline
- `/memory eval --compare <name>` — compare current metrics against baseline
- `/memory eval --rollback <name>` — revert config to named snapshot
- `/memory eval --grow` — harvest real usage into benchmark suite

### TUI Dashboard

`/memory status` displays: tier sizes (with visual bars), session activity (retrievals, outcomes, captures), 7-day rolling health metrics with trends, last compaction summary, and warnings for regressions or negative signals.

`/memory eval` displays: full metrics broken down by tier and content type, top misses, top false positives, compaction health, and actionable suggestions.

---

## 6. First-Run Experience and User Commands

### First Install (Zero Config)

1. **Initialize** (< 1s) — create `~/.total-recall/`, schema, default config. Download ONNX model if not bundled (~80MB, one-time).
2. **Detect environment** — scan for Claude Code, Copilot CLI, OpenCode. Report what's found.
3. **Auto-import** (silent, non-destructive) — memory files and CLAUDE.md from detected hosts. Session history skipped (needs consent).
4. **Auto-ingest project docs** — README.md, docs/ directory, CONTRIBUTING.md, project metadata files (.env.example, package.json etc. — not .env or lock files).
5. **Smoke test** — 20 benchmark queries, < 2 seconds.
6. **Report** — single line: `total-recall: initialized - 4 memories imported - 12 docs ingested - system verified`

### Session 2+ Startup

1. Load hot tier carry-forwards
2. Sync check: new/changed files in watched host directories
3. Assemble hot tier (project-scoped warm + carry-forwards + pinned)
4. Budget check, trim if needed
5. Inject silently
6. Report: `total-recall: 14 hot - 2 new memories synced - warm retrieval ready`

Target: < 500ms.

### User Commands

```
/memory status                    Dashboard overview
/memory search <query>            Semantic search across all tiers
/memory ingest <path>             Add files/dirs to knowledge base
/memory forget <query>            Find and delete entries
/memory compact                   Force compaction with preview
/memory inspect <id>              Deep dive on single entry
/memory promote <id>              Move entry to higher tier
/memory demote <id>               Move entry to lower tier
/memory export                    Export to portable format
/memory import <file>             Import from export file
/memory eval                      Live performance metrics
/memory eval --benchmark          Run synthetic benchmark
/memory eval --compare <name>     Compare configs
/memory eval --snapshot <name>    Save current config as baseline
/memory eval --grow               Add real misses to benchmark
/memory config get <key>          Read config value
/memory config set <key> <value>  Update config (auto-snapshots)
/memory history                   Show recent tier movements
/memory lineage <id>              Show compaction ancestry
```

**Progressive disclosure:** Most users use `/memory status` and `/memory ingest`. Power users access `/memory eval` and config tuning. The system works automatically for the first group while being fully transparent and controllable for the second.

---

## 7. Technology Stack

| Component | Technology | Rationale |
|---|---|---|
| Language | TypeScript | Broadest contributor base, native Claude Code plugin compat |
| Runtime | Node.js | Required for onnxruntime-node, ships with most dev machines |
| Database | better-sqlite3 + sqlite-vec | Synchronous SQLite (no async overhead), vector search in-process |
| Embeddings | onnxruntime-node + all-MiniLM-L6-v2 | Local inference, no API dependency, 384d, ~5-10ms/embed |
| MCP SDK | @modelcontextprotocol/sdk | Standard MCP server implementation |
| Config | TOML | Human-readable, widely understood |
| Build | tsup or esbuild | Fast bundling for distribution |

### Dependencies (Minimal)

- `better-sqlite3` — SQLite bindings
- `sqlite-vec` — Vector extension for SQLite
- `onnxruntime-node` — Local ML inference
- `@modelcontextprotocol/sdk` — MCP protocol
- Dev/build tooling (tsup, vitest, typescript)

No external services. No API keys. No Docker. No cloud.

---

## 8. Extension Points (For Forks)

| Extension | How | Effort |
|---|---|---|
| Add a new host tool | Write a `FieldMapper` (~50 lines) implementing the `HostImporter` interface | Small |
| Swap embedding model | Change `config.toml` model path, update dimensions, re-embed. Migration CLI provided. | Medium |
| Add a new content type | Add new table pair (tier x type), register in schema, add type weight for decay | Medium |
| Custom chunking strategy | Implement a `ChunkParser` for the new format, register in format detection | Small |
| Web UI | Read from same SQLite DB, total-recall exposes read-only query tools | Large (later) |
| API-based embeddings | Implement `EmbeddingProvider` interface, configure in config.toml | Small |

---

## 9. Out of Scope (v1)

- Web UI for visualization (TUI dashboard is v1, web inspector is later)
- Bidirectional sync with host tools (one-way import only)
- Multi-user / team shared memory
- Cloud sync / backup
- Real-time collaborative memory
- Aider integration (no plugin system to hook into)
- Custom fine-tuned embedding models

---

## 10. Success Criteria

1. **Session 1 value** — auto-ingest project docs, knowledge base searchable within 30 seconds of first install
2. **Session 2 value** — corrections from session 1 persist and influence behavior
3. **Week 1 value** — warm tier has accumulated meaningful project context, retrieval precision@3 > 0.60
4. **Month 1 value** — benchmark suite has grown with real usage, retrieval precision@3 > 0.75, user can demonstrate measurable improvement via `/memory eval --compare`
5. **Forkability** — new host tool support addable in < 100 lines, clear architecture documented, eval framework makes changes safe
