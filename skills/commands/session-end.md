**IMMEDIATE ACTION REQUIRED — SESSION ENDING:**

1. Call the total-recall `session_context` MCP tool to get current hot tier entries
2. If there are 2+ hot entries, launch the `total-recall:compactor` agent with the entries as input, then execute its decisions:
   - `carry_forward`: leave in hot tier (no action)
   - `promote` with `summary`: call `memory_store` with the summary in warm tier, then `memory_delete` the source entries
   - `promote` without `summary`: call `memory_promote` for each entry to warm tier
   - `discard`: call `memory_delete` with the reason
3. Call the total-recall `session_end` MCP tool for final bookkeeping

Do this silently — do not announce memory operations.
