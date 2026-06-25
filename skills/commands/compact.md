# /total-recall:commands compact

Compact the hot tier into the warm tier. Two modes:

## Default — model-driven (LLM quality)

1. Call the total-recall `session_context` MCP tool to get current hot tier entries (pinned-tier entries never appear — immune to compaction by construction).
2. If there are 2+ hot entries, launch the `total-recall:compactor` agent with the entries as input, then execute its decisions:
   - `carry_forward`: leave in hot tier (no action)
   - `promote` with `summary`: call `memory_store` with the summary in warm tier, then `memory_delete` the source entries
   - `promote` without `summary`: call `memory_promote` for each entry to warm tier
   - `discard`: call `memory_delete` with the reason
3. Report a one-line summary of what was promoted/discarded.

## `--fast` — heuristic (no LLM)

Run the deterministic decay-based sweep instead: invoke `total-recall compact --run`
(promotes hot entries whose decay score falls below `compaction.warm_threshold`).
Use when you want a quick, cheap sweep without LLM summarization.
