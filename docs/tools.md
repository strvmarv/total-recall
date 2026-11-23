# MCP tools

The MCP server exposes 42 core tools in every backend mode; local SQLite and Cortex modes add usage, cache, skill, feedback, and archive tools (53 in SQLite mode and 54 in Cortex mode). Most tool names follow the pattern `<domain>_<action>` (`status` and `insights`, for example, do not).

| Category | Tools |
|---|---|
| Session | `session_start`, `session_end`, `session_context`, `session_refresh` |
| Memory | `memory_store`, `memory_get`, `memory_get_all`, `memory_update`, `memory_delete`, `memory_inspect`, `memory_search`, `memory_list`, `memory_recent`, `memory_extract`, `memory_feedback`† |
| Tier management | `memory_promote`, `memory_demote`, `memory_pin`, `memory_unpin`, `memory_history`, `memory_lineage` |
| Import / Export | `memory_export`, `memory_import`, `import_host` |
| Knowledge base | `kb_ingest_file`, `kb_ingest_dir`, `kb_search`, `kb_list_collections`, `kb_refresh`, `kb_remove`, `kb_summarize`, `kb_resolve` |
| Compaction | `compact_now` |
| Eval | `eval_report`, `eval_benchmark`, `eval_compare`, `eval_snapshot`, `eval_grow` |
| Config | `config_get`, `config_set` |
| Insights | `insights` |
| Status & Usage | `status`, `usage_status`† |
| Cache | `cache_check`†, `cache_store`† |
| Migration | `migrate_to_remote` |
| Transcript archive† | `transcript_search`, `transcript_get`, `transcript_sessions` |
| Skills† | `skill_search`, `skill_get`, `skill_list`, `skill_import_host`, `skill_delete` *(skill_delete: Cortex mode only)* |

†Unavailable in Postgres mode (local SQLite + Cortex modes only).

Sticky (pinned) surface: `memory_pin` sets the sticky flag on an entry, moving it into the hot tier (with optional `scope: "project" | "global"`); `memory_unpin` clears the flag, leaving the entry in hot as an earned resident; `memory_store` accepts `pinned: true` to store-and-pin new content directly as sticky-hot; `memory_promote` / `memory_demote` reject a sticky entry as source or target (unpin it first). `memory_list` accepts a `sticky: true` filter, and `status` reports a sticky count (surfaced as `tierSummary.pinned` for wire compatibility). **Project-scoped injection** (enabled by `tiers.pinned.project_scoping`, default on): a pin is tagged to a repo by setting its `project` field to the lowercase `owner/repo` slug (e.g. `acme-org/widget-service`) or bare folder name when no remote is configured — at injection time `ProjectResolver` detects the current repo from cwd (pure filesystem walk) and `PinnedScope.OptsFor` filters accordingly. Untagged (null-project) pins are global and always inject. When no repo is detected the injection is fail-closed to globals only.

Retrieval-quality feedback: `memory_search` returns `{ retrievalId, results }` and `kb_search` returns a top-level `retrievalId`. The assistant can call `memory_feedback` with that `retrievalId` to confirm whether the retrieval was actually used; un-acted retrievals are inferred as misses after a grace window. This drives the `eval_report` metrics and the web UI's "Retrieval quality" card. `memory_feedback` is intentionally assistant-only — it is not exposed to the web UI.

Transcript archive: the three `transcript_*` tools are registered in SQLite and Cortex modes. The archive is off by default; while it is off each answers `{"enabled": false, ...}` with the hint to run `total-recall transcripts enable` in a terminal. `transcript_search` searches the archive by meaning and by words (`query` is required, optional `scope`, `project`, `session`, `since`, `include_automated`, `top_k`; see [Searching the archive](transcripts.md#searching-the-archive)). `transcript_get` reads the exchanges around a chunk `id` (from a search hit), or a range of a `session` given as `<host>:<host_session_id>` with `from_exchange` and `to_exchange`, with `context` (0 to 10, default 1) exchanges on each side, within `max_tokens` (200 to 20,000, default 2,000); it flags `truncated` and says how to read on (`nextFromExchange`, `nextToExchange`). `transcript_sessions` lists archived sessions newest first (optional `project`, `since` (active since), `before`, `limit`, `include_automated`; it reports `total`, `more` and `automatedHidden`). Reading needs no model and sends nothing anywhere; see [Reading the archive](transcripts.md#reading-the-archive). All three are assistant-only and not exposed to the web UI.

Handler implementations live in `src/TotalRecall.Server/Handlers/<ToolName>Handler.cs`. Tool wiring: `src/TotalRecall.Server/ServerComposition.cs → BuildRegistry()`.
