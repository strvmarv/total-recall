---
name: forget
description: Use when user says "/memory forget", "remove that memory", "delete memory about", or asks to remove specific entries from total-recall.
---

# Memory Deletion

User-controlled deletion with transparency and confirmation.

## Process

1. Call `memory_search` with the user's query to find matching entries
2. Present matches with: ID, content preview, tier, source, access count
3. If source is from a host tool import, note that the original file is NOT touched
4. Ask user which entries to delete (by number or "all")
5. Call `memory_delete` for each selected entry with a reason
6. Confirm deletion

## Safety

- Never auto-delete without user confirmation
- Never modify host tool source files
- Always log the deletion reason
