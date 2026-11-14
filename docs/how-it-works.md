# How total-recall works

## What Gets Remembered

Every memory has an entry type that tells total-recall what it is and how to treat it.

| Entry Type | Stored When | Example |
|---|---|---|
| `Correction` | You fix a mistake the AI made | `"Use Array.from() not spread for NodeList — spread fails in our build target"` |
| `Preference` | You state a style or workflow preference | `"Always use const over let unless reassignment is needed"` |
| `Decision` | You make an architecture or design choice | `"Using Zustand for state — Redux was overkill for this app size"` |
| `Surfaced` | The AI captures context automatically | Key facts, constraints, or project-specific patterns noticed during work |
| `Imported` | First-run import from another tool | Your existing Claude Code memories, Copilot CLI session plans, Cursor rules |
| `Compacted` | Tier compaction generates a summary | Multiple related memories merged into a higher-signal entry |
| `Ingested` | You ingest a file or directory | Chunks from READMEs, API docs, architecture notes |

**`Correction` and `Preference` entries get priority treatment.** They surface as actionable hints at every session start and carry higher decay scores — helping them earn promotion into the hot tier and resist eviction once there.

## Tier Model

total-recall uses a three-tier memory model — **Hot / Warm / Cold** — with a **sticky** flag that turns any hot entry into an always-injected pin. New memories land in **Warm** by default and *earn* their way into Hot by proving useful, so the auto-injected context stays high-signal without carrying everything:

- **Warm** (default landing tier, up to 10K entries) — where new memories go unless you say otherwise: `memory_store`, `memory_extract`, `memory_import` / `total-recall memory import` (for entries without a recognised `tier`) and Cortex pulls that omit the tier all land here. Retrieved semantically per query: when you ask about authentication, relevant auth memories surface automatically. An entry is **promoted to Hot** once it earns it — `access_count ≥ 5` **and** `decay_score ≥ 0.7` (both tunable). Unused entries decay and migrate to Cold.
- **Hot** (up to 50 entries, 4000-token budget, 1200 chars/entry) — injected at session start, no query needed. Populated by earned promotion from warm (and by explicit `tier: "hot"` writes, which are capped at 1200 characters — store a concise summary, not an essay). Sticky tokens come off the top of this budget.
- **Sticky (pinned)** — a flag on a hot entry, set via `memory_pin` (or store-and-pin with `memory_store { pinned: true }`). Sticky entries are **unbounded**, injected verbatim and first at session start under a `## Pinned directives (always follow)` header, and are **never** truncated, decayed, demoted, evicted, or compacted. `memory_unpin` clears the flag, leaving the entry in hot as a normal earned resident. **Project-scoped injection** (default on): untagged pins are global and inject everywhere; a pin tagged with a `project` value (lowercase `owner/repo` slug or folder name) injects only when the detected cwd matches that repo. When no git repo is detected, only global pins inject (fail-closed).
- **Cold** (unlimited, hierarchical) — your knowledge base. Ingest entire directories — source trees, documentation, design specs — and they're retrieved when relevant.

> **Upgrading from 3.x?** The old Pinned tier is merged into Hot as the sticky flag, and a one-time migration runs automatically on first `session_start`: existing pins become sticky-hot, previously auto-hot entries move to warm, and the legacy `pinned_*` tables are dropped. The migration is irreversible — back up `~/.total-recall/total-recall.db` first if you want a rollback path.

## Hybrid Search

Retrieval combines **BM25 full-text search** and **cosine vector similarity**, merged by a pure F# ranking function. You get keyword precision when you search by exact terms and semantic recall when you describe what you need in natural language. The BM25/vector weight is tunable via `[search] fts_weight`.

## Embeddings

All memories are embedded with [bge-small-en-v1.5](https://huggingface.co/BAAI/bge-small-en-v1.5) (384 dimensions, CLS pooling, with an asymmetric query prefix for searches), running locally via ONNX — no API calls, no network dependency. The model (~133 MB) is fetched and sha256-verified at release build time and ships bundled inside the npm/release artifact; there is no runtime HuggingFace download. If the bundled model is absent, the binary fails fast with a clear error rather than fetching anything.

If you swap the local embedder, existing vectors are in the old model's space. By default (`embedding.on_model_change = "auto"`) the **sqlite** and **cortex** backends re-embed their local index automatically — a one-time re-embed that runs **in the background** after launch, not on the startup path. The server stays fully usable while it runs; local semantic retrieval is degraded until it finishes, and progress is reported through `session_start`/`status` (you'll see a "re-index in progress (N/M)" notice). It's batched and resumable, so restarting mid-re-index picks up where it left off rather than starting over. This also covers a pre-existing index that was never fingerprint-stamped (e.g. an older cortex database): if it holds vectors but carries no fingerprint, it is re-embedded too rather than silently left in a stale model space. Set `on_model_change = "warn"` to run with the stale vectors (degraded retrieval, recurring warning) or `"block"` to refuse to start. **Postgres** can't auto-migrate: under `auto` it stops with an actionable error — re-ingest into a fresh database or use `"warn"`. For **cortex**, only the local vector index is re-embedded (the remote re-embeds independently); `total-recall reindex-embeddings` runs the same re-embed offline for `warn`/`block` deferrals and manual re-embeds.

For enterprise deployments, swap in a remote embedder (OpenAI, Amazon Bedrock) for higher-dimensional vectors and finer-grained retrieval across shared team knowledge.

## Session Start

Every `session_start` call runs the same sequence:

1. **Import sync** — scans all installed host tools (Claude Code, Copilot CLI, Cursor, Cline, OpenCode, Hermes), deduplicates via content hash, and imports new entries. What is imported from each: Claude Code, the per-project memory files and the top-level `CLAUDE.md`; Copilot CLI, session plans (`plan.md`); Cursor, the personal context rules, `.cursorrules` and `.cursor/rules/*.mdc`; Cline, the task history and global rules files; OpenCode, the global and project `AGENTS.md` and each project's agent and command files; Hermes, `MEMORY.md` and `USER.md` entries, `SOUL.md` and skills.
2. **Sticky + hot tier assembly** — sticky (pinned) entries are injected first, verbatim and untruncated, then current hot entries fill the remaining token budget. When `tiers.pinned.project_scoping` is on (default), only global pins and pins tagged to the detected repo are included; if no repo is detected, only global pins inject (fail-closed).
3. **Hint generation** — surfaces up to 5 high-value warm memories as actionable one-liners: `Correction` and `Preference` entries first, frequently accessed entries (3+ accesses) second, warm→hot promotion candidates third. No LLM calls — pure DB queries.
4. **Tier summary** — counts entries across hot, warm, cold, and all KB collections, plus a sticky count (`tierSummary.pinned` is retained for wire compatibility and now reports sticky-hot entries). A `pinned_budget_pressure` hint fires when sticky pins consume over half the token budget (suggested action: `memory_unpin`).
5. **Session continuity** — reports human-readable time since the last compaction event (proxy for last active session).

Every `session_start` also runs a skill scan: it reads `~/.claude/skills/` plus any directories listed in `[skills] extra_dirs`, persists the content + a locally-computed embedding to a SQLite skill cache, and advertises discovered skills as an `## Available Skills` block in the session context. Scanned skills are invokable on demand via the `skill_get` MCP tool and discoverable via `skill_search` (hybrid semantic + keyword ranking with a usage-decay tie-breaker) — both work entirely offline with no Cortex required. In Cortex mode, scanning and local `skill_get`/`skill_search` always work the same way; whether scanned skills also get **uploaded** to Cortex (so usage events sync back as a multi-machine rollup and skills pulled from other machines merge into the local cache) is controlled by `[skills] auto_import`, which **defaults to disabled** — uploading local skills is opt-in, since a host tool that already syncs skills down from Cortex (e.g. a Cortex skill-sync plugin) would otherwise have them re-uploaded right back as duplicate user-scoped entries.

## Pinned-Directive Floor

Pinned directives are injected once at `session_start`, but in a long session they drift far enough up the transcript that the model stops honoring them. The **pinned floor** re-asserts the pinned block near the live edge on an adaptive throttle, so your pins keep being followed all session long.

A per-turn `UserPromptSubmit` hook runs before each prompt and re-injects the pinned block when **either** trigger trips since the last injection:

- `floor_every_n_turns` user turns have elapsed (default 6), **or**
- ~`floor_growth_tokens` of transcript growth has accumulated (default 6000).

The first turn of a session seeds the throttle and skips (the block was just injected at session start). The re-injected block is rendered verbatim — identical to the session-start block — and prefixed with a short reminder line. Project scoping applies here too: the hook reads the cwd from the hook payload and filters pins by the detected repo (same fail-closed semantics as session start). The hook is **fail-safe: it never blocks or rejects a prompt**. Disable it entirely with `floor_enabled = false`.

Per-host support:

| Host | Per-turn floor | Mechanism |
|---|---|---|
| Claude Code | Active | `UserPromptSubmit` hook → `additionalContext` |
| Copilot CLI | Active (turn-count trigger only) | `userPromptSubmitted` hook (`copilot/hooks.json` → `copilot/hook.js`) → `additionalContext` (observed on 1.0.98-2; Copilot's docs say this event ignores it) |
| Cursor | Active on tool-using turns (turn-count trigger only) | `postToolUse` hook (`cursor/hooks.json` → `cursor/hook.js`) → `additional_context` (Cursor's CLI fires no per-prompt hook, and `beforeSubmitPrompt` cannot inject) |
| Gemini CLI | Active | `BeforeAgent` hook → `hookSpecificOutput.additionalContext` |
| pi | Active (turn-count trigger only) | pi extension: `before_agent_start` → hidden context message; `/total-recall` command |
| Codex CLI | Active (turn-count trigger only) | `UserPromptSubmit` hook (`codex/hooks.json` → `codex/hook.js`) → `additionalContext` |
| Cline | MCP tools only | No hooks; MCP server config via postinstall |
| Windsurf | MCP tools only | No hooks; rules file for session-start instruction |
| Zed | MCP tools only | No hooks; `context_servers` config via postinstall |
| VS Code | MCP tools only | No hooks; `servers` config + `~/.copilot/mcp-config.json` |
