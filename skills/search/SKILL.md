---
name: search
description: Use when user says "/memory search", "search my memory", "what do I know about", "find in knowledge base", or asks to look up something in their stored context.
---

# Memory Search

Explicit user-initiated search across all tiers and content types.

## Process

1. Call `memory_search` with the user's query, all tiers enabled, top_k=10
2. Format results grouped by tier, then by content type
3. Show: content preview (first 100 chars), similarity score, source, tags
4. Offer actions: "/memory promote <id>" or "/memory forget <id>"

## Output Format

Group results by tier (hot first, then warm, then cold). Show scores rounded to 2 decimal places. Include source attribution for imported entries.
