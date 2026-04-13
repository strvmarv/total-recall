---
name: using-total-recall
description: Initialize total-recall memory system and ensure session_start has been called. Use at session start or when memory features aren't working.
---

# Using total-recall

This skill ensures the total-recall memory system is active for this session.

## Immediate Action

1. Call the total-recall `session_start` MCP tool now (if it already ran server-side, it returns cached results instantly)
2. **Announce startup** using the returned data:
   - Report tier summary: hot, warm, cold, KB counts from `tierSummary`
   - Report storage backend from `storage` (e.g. "sqlite", "cortex", "postgres"). If it shows a fallback like "sqlite (cortex failed)", flag this prominently.
   - If `lastSessionAge` is present, mention when the last session was
   - If `hints` are present, briefly surface the most relevant ones
   - Keep it to 2-3 lines max
3. Use `hints` to inform your behavior throughout the session
4. Incorporate the returned context to inform your responses

## Ongoing Behaviors

Once initialized, follow these behaviors throughout the session. Tool calls will be visible to the user.

### Handling model bootstrap errors

When `session_start` returns an error response containing `"error": "model_not_ready"`, parse the JSON payload and follow the recovery flow based on `reason`:

| reason | What it means | What to do |
|---|---|---|
| `downloading` | First-run bootstrap is in progress (90 MB ONNX model). Another process or this one holds the lock. | Wait 5–10 seconds and call `session_start` again. Repeat up to 12 times (~2 minutes total). Surface a brief status to the user on the first retry: "Total-recall is downloading its embedding model on first run. This is a one-time setup." |
| `missing` | Model not present and no bootstrap has started. | Call `session_start` again to trigger the bootstrap. |
| `corrupted` | Model file present but failed checksum (e.g., partial download, bad bundled file, Git LFS pointer). | Call `session_start` once more — bootstrap will re-download. If it fails again with the same reason, surface the `hint` field to the user verbatim (it contains manual install instructions) and proceed without memory. |
| `failed` | Network failure or other unrecoverable download error. | Surface the `hint` field verbatim to the user (manual install commands) and proceed without memory features for this session. Do NOT keep retrying — that will only delay the user. |

After successful recovery, all subsequent total-recall behaviors (capture, retrieve, session end) should resume normally. If recovery is impossible, the assistant must continue helping the user with their actual task — memory unavailability is a degraded mode, not a fatal error.

### Capture (continuous)

When you detect these patterns in user messages, call `memory_store`:

- **Correction**: "no", "not that", "actually", "use X instead" -> type "correction"
- **Preference**: How the user wants things done -> type "preference"
- **Decision**: Non-obvious architectural or design choices -> type "decision"

Do NOT ask permission — just store it.

### Retrieve (continuous)

On each user message that is a question or task request:

1. Call `memory_search` with the message, searching warm tier
2. If top score < 0.5, also search cold/knowledge tier
3. Use results to inform your response

### Session End

1. Call `session_context` to get current hot tier entries
2. If there are 2+ hot entries, launch the `total-recall:compactor` agent with the entries as input
3. Parse the agent's JSON decisions and execute them
4. Call `session_end` for final bookkeeping

### Rules

- Let tool calls be visible — users should see that memory is working
- ALWAYS store corrections — highest-value memories
- ALWAYS search warm tier before answering project questions
- NEVER modify host tool files (Claude Code memory/, CLAUDE.md, etc.)
