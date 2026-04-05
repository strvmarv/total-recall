---
name: recall
description: Manage total-recall memory system. Subcommands: status, search, store, forget, inspect, promote, demote, history, lineage, export, import, ingest, kb, compact, eval, config. Also governs automatic memory capture, retrieval, and compaction.
---

# total-recall

Memory and knowledge base management for total-recall.

## Automatic Behavior

These behaviors run silently throughout the session. Do not announce them.

### Session Start

1. Call the `session_start` MCP tool to sync imports and assemble hot tier context
2. Silently incorporate the returned context — do not announce what was loaded

### Capture (silent, continuous)

When you detect these patterns in user messages, call `memory_store`:

- **Correction**: "no", "not that", "actually", "use X instead" -> type "correction"
- **Preference**: How the user wants things done -> type "preference"
- **Decision**: Non-obvious architectural or design choices -> type "decision"

Do NOT announce stores. Do NOT ask permission.

### Retrieve (silent, continuous)

On each user message that is a question or task request:

1. Call `memory_search` with the message, searching warm tier
2. If top score < 0.5, also search cold/knowledge tier
3. Use results to inform your response — do not announce retrievals

### Session End

Call `session_end` for hot tier compaction.

### Rules

- NEVER announce memory operations unless asked
- ALWAYS store corrections — highest-value memories
- ALWAYS search warm tier before answering project questions
- NEVER modify host tool files (Claude Code memory/, CLAUDE.md, etc.)

---

## Commands

`/total-recall <subcommand> [args]`

### status

Call the `status` MCP tool. Format as a dashboard showing:
- Tier sizes (hot/warm/cold with counts for memories and knowledge)
- Session ID
- Total entry count

### search <query>

Call `memory_search` with the query, all tiers enabled, top_k=10. Format results grouped by tier, showing: content preview, similarity score, source, tags. Offer actions: `/total-recall promote <id>` or `/total-recall forget <id>`.

### store <content>

Call `memory_store` with the provided content. Optionally accept flags:
- `--tier hot|warm|cold` (default: hot)
- `--tags tag1,tag2`
- `--type correction|preference|decision`

### forget <query>

1. Call `memory_search` with the query to find matching entries
2. Present matches with: ID, content preview, tier, source, access count
3. If source is from a host tool import, note the original file is NOT touched
4. Ask user which entries to delete (by number or "all")
5. Call `memory_delete` for each selected entry
6. Confirm deletion

Never auto-delete without user confirmation.

### inspect <id>

Call `memory_inspect` with the entry ID. Show full entry details including content, tier, source, tags, access count, decay score, creation/update timestamps, and compaction history.

### promote <id> [--tier hot|warm] [--type memory|knowledge]

Call `memory_promote` with the entry ID and target tier/type. Default target: one tier up, same content type.

### demote <id> [--tier warm|cold] [--type memory|knowledge]

Call `memory_demote` with the entry ID and target tier/type. Default target: one tier down, same content type.

### history

Call `memory_history`. Show recent tier movements from the compaction log as a timeline.

### lineage <id>

Call `memory_lineage` with the entry ID. Show the full compaction ancestry tree.

### export

Call `memory_export`. Optionally accept `--tiers hot,warm,cold` and `--types memory,knowledge` to filter.

### import <path>

Call `memory_import` with the file path.

### ingest <path>

Determine if path is a file or directory:
- File: call `kb_ingest_file`
- Directory: call `kb_ingest_dir`

Report: collection name, document count, chunk count. Suggest a test query to verify.

### kb search <query>

Call `kb_search` with the query. Show results with content preview, score, collection, and source path.

### kb list

Call `kb_list_collections`. Show all collections with document and chunk counts.

### kb refresh <collection>

Call `kb_refresh` with the collection ID. Report re-ingestion results.

### kb remove <id>

Call `kb_remove` with the entry ID. Ask for confirmation first.

### compact

Call `compact_now`. Show results: entries carried forward, promoted to warm, discarded.

### eval [--benchmark] [--compare <name>] [--snapshot <name>]

- No flags: call `eval_report` for live retrieval quality metrics (7-day rolling)
- `--benchmark`: call `eval_benchmark` for synthetic benchmark results
- `--compare <name>`: compare against a saved baseline
- `--snapshot <name>`: save current config as a named baseline

### config [get|set] <key> [value]

- `get`: call `config_get` with the key (or omit for full config)
- `set`: call `config_set` with key and value

### import-host [source]

Call `import_host` to detect and import memories from host tools (Claude Code, Copilot CLI). Optionally restrict to a specific source.

### update

Pull the latest version of the plugin from git. Run:

```bash
cd "$CLAUDE_PLUGIN_ROOT" && git pull origin main
```

Report what changed (files updated, new version). Then tell the user to run `/reload-plugins` to apply the changes.
