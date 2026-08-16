---
inclusion: always
---

# total-recall memory system

total-recall is an MCP server providing three-tier memory (Hot/Warm/Cold with a **sticky** flag — sticky pins are always injected first and immune to decay) and a hierarchical knowledge base, all local by default.

**IMMEDIATE ACTION REQUIRED — before responding to the user's first message, call the `session_start` MCP tool.** This syncs host imports, loads sticky directives and hot-tier context, and returns the memory state for the session. If `session_start` has already run, the call returns cached results instantly.

## Session start protocol

1. Call `session_start` before your first reply
2. Announce startup using the returned data (2-3 lines max):
   - Tier summary: hot (sticky + earned), warm, cold, KB counts from `tierSummary`
   - Storage backend from `storage`
   - If `lastSessionAge` is present, mention when the last session was
   - If `hints` are present, surface the most relevant ones
   - Example: `total-recall loaded — 2 sticky, 3 hot, 12 warm, 5 cold, 2 KB collections. Storage: sqlite. Last session: 2 hours ago.`
3. Use the `context` field to inform your responses throughout the session

**If `session_start` is blocked by permissions**, tell the user which tool needs to be allowed and proceed in degraded (no-memory) mode — memory unavailability is not fatal.

## Automatic behaviors

### Capture (continuous)
When you detect corrections, preferences, or non-obvious decisions in user messages, call `memory_store` without asking permission.

- Correction (`"no"`, `"not that"`, `"actually"`, `"use X instead"`) → type `correction`
- Preference (how the user wants things done) → type `preference`
- Decision (non-obvious architectural or design choice) → type `decision`

### Retrieve (continuous)
On each user message that is a question or task request:
1. Call `memory_search` with the message, searching warm tier
2. If top score < 0.5, also search cold/knowledge tier
3. Use results to inform your response

### Feedback (continuous)
After using retrieved memories, call `memory_feedback` with the `retrievalId` from the search response:
- Used it → `memory_feedback({ retrievalId, used: true })`
- Nothing relevant → `memory_feedback({ retrievalId, used: false })`

### Sticky directives
Sticky directives are re-asserted automatically at each turn by the `UserPromptSubmit` hook (a command-type hook that shells out to `total-recall pinned-floor --host kiro`). When the user makes a significant task switch, call `session_refresh` once to re-prepend the sticky block.

### Session end
Call `session_end` for final bookkeeping. Run `/total-recall:commands compact` to promote hot entries before closing.

## Key MCP tools

| Tool | Purpose |
|------|---------|
| `session_start` | Sync imports, load context (call first) |
| `session_end` | Final bookkeeping |
| `session_refresh` | Re-inject sticky block on task switch |
| `memory_store` | Store a memory |
| `memory_search` | Semantic search across tiers |
| `memory_feedback` | Record whether a retrieval was used |
| `memory_pin` / `memory_unpin` | Pin/unpin entries |
| `kb_search` | Search the knowledge base |
| `status` | Tier counts and session info |

For the full command reference, use `/total-recall:commands help`.
