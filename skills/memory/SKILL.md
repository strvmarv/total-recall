---
name: memory
description: Use at every session start to enable automatic memory capture, retrieval, and context injection. Triggers on SessionStart, detects corrections and preferences during conversation, queries warm tier on user messages, and compacts at session end.
---

# Automatic Memory Management

## Overview

This skill governs total-recall's always-on memory behavior. It runs silently, capturing corrections, preferences, and decisions into the hot tier, retrieving relevant warm memories per query, and compacting at session end.

## At Session Start

1. Call the `session_start` MCP tool. It will:
   - Sync any new imports from detected host tools
   - Assemble the hot tier (carry-forwards + relevant warm memories + pinned knowledge)
   - Return injectable context

2. Silently incorporate the returned context into your working memory. Do not announce what was loaded unless the user asks.

## During Conversation

### Capture (automatic, silent)

When you detect any of these patterns in the user's message, call `memory_store`:

- **Correction**: User says "no", "not that", "actually", "use X instead" -> store with type "correction"
- **Preference**: User expresses how they want things done -> store with type "preference"
- **Decision**: A non-obvious architectural or design choice is made -> store with type "decision"

Do NOT announce that you stored a memory. Do NOT ask permission. Just store it.

### Retrieve (automatic, silent)

On each user message, if the message is a question or task request:

1. Call `memory_search` with the user's message as query, searching warm tier, scoped to the current project
2. If warm results are insufficient (top score < 0.5), also search cold/knowledge tier
3. Use retrieved context to inform your response
4. Do NOT announce what you retrieved unless the user asks "what do you remember about X"

### When User Asks About Memory

If the user asks "what do you know about...", "what do you remember...", or "show me memories about...":
- Call `memory_search` explicitly with their query
- Present results transparently with tier, score, and source

## At Session End

Call `session_end` MCP tool. It will:
- Run hot tier compaction (decay-scored promotion/discard)
- Log compaction events
- Update retrieval event outcomes

## Key Rules

- NEVER announce memory operations unless asked
- ALWAYS store corrections -- they are the highest-value memories
- ALWAYS search warm tier before answering questions about the project
- NEVER modify the user's host tool memory files (Claude Code memory/, CLAUDE.md, etc.)
