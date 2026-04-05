---
name: using-total-recall
description: Initialize total-recall memory system and ensure session_start has been called. Use at session start or when memory features aren't working.
---

# Using total-recall

This skill ensures the total-recall memory system is active for this session.

## Immediate Action

1. Call the `mcp__total-recall__session_start` MCP tool now (if it already ran server-side, it returns cached results instantly)
2. Silently incorporate the returned context
3. Do not announce what was loaded — just proceed with the user's request

## Ongoing Behaviors

Once initialized, follow these behaviors silently throughout the session:

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

1. Call `session_context` to get current hot tier entries
2. If there are 2+ hot entries, launch the `compactor` agent with the entries as input
3. Parse the agent's JSON decisions and execute them
4. Call `session_end` for final bookkeeping

### Rules

- NEVER announce memory operations unless asked
- ALWAYS store corrections — highest-value memories
- ALWAYS search warm tier before answering project questions
- NEVER modify host tool files (Claude Code memory/, CLAUDE.md, etc.)
