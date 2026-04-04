# total-recall Phase 3: Skills, Hooks, Platform Wrappers, Dashboard, README

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Transform total-recall from a functional MCP server into a polished, installable plugin for Claude Code, Copilot CLI, OpenCode, Cline, and Cursor. Add skill definitions, hooks, platform manifests, TUI dashboard formatting, and a README that makes people want to fork it.

**Architecture:** Plugin surface layer on top of Phase 1+2 core. Skills are markdown files (SKILL.md) that instruct the host LLM when/how to call MCP tools. Hooks are bash scripts triggered by session lifecycle events. Platform wrappers are JSON manifests. Dashboard formatting is pure string construction from existing status/eval data.

**Tech Stack:** Markdown (skills), JSON (manifests), Bash (hooks) — no new runtime dependencies

**Spec Reference:** `docs/superpowers/specs/2026-04-04-total-recall-design.md` — Sections 3 (skills), 6 (UX/commands)

**Depends on:** Phase 1 + Phase 2 complete

---

## File Structure (New files in Phase 3)

```
total-recall/
  .claude-plugin/
    plugin.json
    marketplace.json
  .copilot-plugin/
    plugin.json
  .cursor-plugin/
    plugin.json
  .opencode/
    INSTALL.md
  skills/
    memory/SKILL.md
    search/SKILL.md
    ingest/SKILL.md
    status/SKILL.md
    forget/SKILL.md
  hooks/
    hooks.json
    hooks-cursor.json
    session-start/
      run.sh
    session-end/
      run.sh
  agents/
    compactor.md
  src/
    dashboard/
      status-formatter.ts
      status-formatter.test.ts
      eval-formatter.ts
      eval-formatter.test.ts
  README.md
  LICENSE
  CONTRIBUTING.md
```

---

### Task 1: Claude Code Plugin Manifest

**Files:**
- Create: `.claude-plugin/plugin.json`

- [ ] **Step 1: Create plugin.json**

```json
{
  "name": "total-recall",
  "description": "Multi-tiered memory and knowledge base with semantic search, auto-compaction, and built-in evaluation. Works across Claude Code, Copilot CLI, OpenCode, Cline, and Cursor.",
  "version": "0.1.0",
  "author": {
    "name": "strvmarv"
  },
  "skills": "./skills/",
  "agents": "./agents/",
  "hooks": "./hooks/hooks.json",
  "mcpServers": {
    "total-recall": {
      "command": "node",
      "args": ["dist/index.js"],
      "env": {}
    }
  }
}
```

- [ ] **Step 2: Commit**

```bash
git add .claude-plugin/
git commit -m "feat: add Claude Code plugin manifest"
```

---

### Task 2: Other Platform Manifests

**Files:**
- Create: `.copilot-plugin/plugin.json`
- Create: `.cursor-plugin/plugin.json`
- Create: `.opencode/INSTALL.md`

- [ ] **Step 1: Create Copilot CLI manifest**

```json
{
  "name": "total-recall",
  "description": "Multi-tiered memory and knowledge base for TUI coding assistants",
  "version": "0.1.0",
  "skills": "./skills/",
  "mcpServers": {
    "total-recall": {
      "command": "node",
      "args": ["dist/index.js"]
    }
  }
}
```

- [ ] **Step 2: Create Cursor manifest**

```json
{
  "name": "total-recall",
  "description": "Multi-tiered memory and knowledge base for TUI coding assistants",
  "version": "0.1.0",
  "skills": "./skills/",
  "hooks": "./hooks/hooks-cursor.json",
  "mcpServers": {
    "total-recall": {
      "command": "node",
      "args": ["dist/index.js"]
    }
  }
}
```

- [ ] **Step 3: Create OpenCode install guide**

```markdown
# total-recall for OpenCode

## Installation

1. Add the MCP server to your OpenCode config:

\`\`\`json
{
  "mcpServers": {
    "total-recall": {
      "command": "node",
      "args": ["/path/to/total-recall/dist/index.js"]
    }
  }
}
\`\`\`

2. Copy the skills directory to your OpenCode plugins:

\`\`\`bash
cp -r skills/ ~/.opencode/plugins/total-recall/skills/
\`\`\`

3. Restart OpenCode.
```

- [ ] **Step 4: Commit**

```bash
git add .copilot-plugin/ .cursor-plugin/ .opencode/
git commit -m "feat: add platform manifests for Copilot CLI, Cursor, and OpenCode"
```

---

### Task 3: Core Memory Skill (Always-On Behavior)

**Files:**
- Create: `skills/memory/SKILL.md`

- [ ] **Step 1: Write the core memory skill**

```markdown
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

- **Correction**: User says "no", "not that", "actually", "use X instead" → store with type "correction"
- **Preference**: User expresses how they want things done → store with type "preference"  
- **Decision**: A non-obvious architectural or design choice is made → store with type "decision"

Do NOT announce that you stored a memory. Do NOT ask permission. Just store it.

### Retrieve (automatic, silent)

On each user message, if the message is a question or task request:

1. Call `memory_search` with the user's message as query, searching warm tier, scoped to the current project
2. If warm results are insufficient (top score < 0.5), also call `memory_search` on cold/knowledge tier
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
- ALWAYS store corrections — they are the highest-value memories
- ALWAYS search warm tier before answering questions about the project
- NEVER modify the user's host tool memory files (Claude Code memory/, CLAUDE.md, etc.)
```

- [ ] **Step 2: Commit**

```bash
git add skills/memory/
git commit -m "feat: add core memory skill for always-on behavior"
```

---

### Task 4: Search, Ingest, Status, Forget Skills

**Files:**
- Create: `skills/search/SKILL.md`
- Create: `skills/ingest/SKILL.md`
- Create: `skills/status/SKILL.md`
- Create: `skills/forget/SKILL.md`

- [ ] **Step 1: Write search skill**

```markdown
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
```

- [ ] **Step 2: Write ingest skill**

```markdown
---
name: ingest
description: Use when user says "/memory ingest", "add to knowledge base", "ingest these docs", or asks to import files or directories into total-recall.
---

# Knowledge Base Ingestion

Add files or directories to the knowledge base for semantic retrieval.

## Process

1. Determine if the path is a file or directory
2. For files: call `kb_ingest_file` with the path
3. For directories: call `kb_ingest_dir` with the path
4. Report: collection name, document count, chunk count, validation results
5. Suggest a test query to verify ingestion worked

## Supported Formats

Markdown, TypeScript, JavaScript, Python, Go, Rust, JSON, YAML, plain text, and more. Code files are split on function/class boundaries. Markdown is split on headings.
```

- [ ] **Step 3: Write status skill**

```markdown
---
name: status
description: Use when user says "/memory status", "/memory eval", "how is memory performing", "show memory dashboard", or asks about total-recall health or metrics.
---

# Memory Status & Evaluation

Show the TUI dashboard or detailed evaluation metrics.

## For /memory status

Call the `status` MCP tool and format the response as a dashboard showing:
- Tier sizes (hot/warm/cold with counts)
- Knowledge base stats (collections, documents, chunks)
- DB size and embedding model info
- Session activity summary

## For /memory eval

Call `eval_report` for live metrics. Show:
- Precision@3, hit rate, miss rate, MRR (7-day rolling)
- Breakdown by tier and content type
- Top misses and false positives
- Compaction health

## For /memory eval --benchmark

Call `eval_benchmark`. Show synthetic benchmark results with pass/fail thresholds.

## For /memory eval --compare <name>

Call `eval_report` with the named config snapshot for comparison. Show side-by-side metrics with deltas and trend arrows.
```

- [ ] **Step 4: Write forget skill**

```markdown
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
```

- [ ] **Step 5: Commit**

```bash
git add skills/
git commit -m "feat: add search, ingest, status, and forget skills"
```

---

### Task 5: Session Hooks

**Files:**
- Create: `hooks/hooks.json`
- Create: `hooks/hooks-cursor.json`
- Create: `hooks/session-start/run.sh`
- Create: `hooks/session-end/run.sh`

- [ ] **Step 1: Create Claude Code hooks config**

```json
{
  "hooks": {
    "SessionStart": [
      {
        "matcher": "startup|clear|compact",
        "hooks": [
          {
            "type": "command",
            "command": "bash hooks/session-start/run.sh",
            "async": false
          }
        ]
      }
    ]
  }
}
```

- [ ] **Step 2: Create Cursor hooks config**

```json
{
  "hooks": {
    "SessionStart": [
      {
        "matcher": "startup|clear|compact",
        "hooks": [
          {
            "type": "command",
            "command": "bash hooks/session-start/run.sh",
            "async": false
          }
        ]
      }
    ]
  }
}
```

- [ ] **Step 3: Create session-start hook script**

```bash
#!/usr/bin/env bash
# total-recall SessionStart hook
# Injects the core memory skill into the session

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PLUGIN_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
SKILL_FILE="$PLUGIN_ROOT/skills/memory/SKILL.md"

if [ -f "$SKILL_FILE" ]; then
  cat "$SKILL_FILE"
fi
```

- [ ] **Step 4: Create session-end hook script**

```bash
#!/usr/bin/env bash
# total-recall SessionEnd hook
# Triggers compaction (called by the MCP tool, not this script)
# This hook just reminds the skill to call session_end

echo "total-recall: session ending, compaction will run"
```

- [ ] **Step 5: Make scripts executable and commit**

```bash
chmod +x hooks/session-start/run.sh hooks/session-end/run.sh
git add hooks/
git commit -m "feat: add SessionStart/End hooks for Claude Code and Cursor"
```

---

### Task 6: Compactor Subagent

**Files:**
- Create: `agents/compactor.md`

- [ ] **Step 1: Write compactor agent definition**

```markdown
---
name: compactor
description: |
  Use this agent at session end to perform intelligent hot-to-warm compaction.
  Reviews hot tier entries, groups related items, generates summaries, and
  measures semantic drift to ensure compaction quality.
model: inherit
---

# Compactor Agent

You are the total-recall compactor. Your job is to review hot tier memory entries at session end and decide what to keep, promote, merge, or discard.

## Input

You receive the current hot tier entries with their decay scores, access counts, and content.

## Process

1. Group related entries (e.g., multiple corrections about the same topic)
2. For groups of 2+ related entries:
   - Generate a concise summary that preserves all key facts
   - The summary should be retrievable by the same queries that would find the originals
3. For individual entries:
   - If decay score > promote_threshold: recommend carry forward
   - If decay score > warm_threshold: recommend promote to warm as-is
   - If decay score < warm_threshold: recommend discard
4. Report: entries processed, summaries generated, facts preserved count

## Output Format

Return a JSON array of decisions:
```json
[
  {"action": "carry_forward", "entry_ids": ["id1"]},
  {"action": "promote", "entry_ids": ["id2", "id3"], "summary": "merged summary text"},
  {"action": "discard", "entry_ids": ["id4"], "reason": "ephemeral session context"}
]
```

## Rules

- NEVER discard corrections or preferences with decay score > 0.2
- ALWAYS preserve the specific details (tool names, version numbers, config values)
- Summaries must be shorter than the combined originals
- If unsure, promote rather than discard
```

- [ ] **Step 2: Commit**

```bash
git add agents/
git commit -m "feat: add compactor subagent for intelligent hot-to-warm compaction"
```

---

### Task 7: TUI Dashboard Formatter

**Files:**
- Create: `src/dashboard/status-formatter.ts`
- Create: `src/dashboard/status-formatter.test.ts`
- Create: `src/dashboard/eval-formatter.ts`
- Create: `src/dashboard/eval-formatter.test.ts`

- [ ] **Step 1: Write failing test for status formatter**

```typescript
// src/dashboard/status-formatter.test.ts
import { describe, it, expect } from "vitest";
import { formatStatusDashboard } from "./status-formatter.js";

describe("formatStatusDashboard", () => {
  it("produces a formatted TUI dashboard string", () => {
    const output = formatStatusDashboard({
      tiers: {
        hot: { memories: 14, knowledge: 2 },
        warm: { memories: 203, knowledge: 45 },
        cold: { memories: 1847, knowledge: 1203 },
      },
      dbSizeBytes: 25497600,
      embeddingModel: "all-MiniLM-L6-v2",
      embeddingDimensions: 384,
      sessionActivity: {
        retrievals: 12,
        used: 9,
        neutral: 2,
        negative: 1,
        memoriesCaptured: 3,
        kbQueries: 4,
        avgKbScore: 0.82,
      },
    });

    expect(output).toContain("total-recall");
    expect(output).toContain("Hot:");
    expect(output).toContain("14");
    expect(output).toContain("Warm:");
    expect(output).toContain("203");
    expect(output).toContain("24.3 MB");
  });
});
```

- [ ] **Step 2: Implement status formatter**

```typescript
// src/dashboard/status-formatter.ts

interface StatusData {
  tiers: {
    hot: { memories: number; knowledge: number };
    warm: { memories: number; knowledge: number };
    cold: { memories: number; knowledge: number };
  };
  dbSizeBytes: number;
  embeddingModel: string;
  embeddingDimensions: number;
  sessionActivity?: {
    retrievals: number;
    used: number;
    neutral: number;
    negative: number;
    memoriesCaptured: number;
    kbQueries: number;
    avgKbScore: number;
  };
}

export function formatStatusDashboard(data: StatusData): string {
  const hotTotal = data.tiers.hot.memories + data.tiers.hot.knowledge;
  const warmTotal = data.tiers.warm.memories + data.tiers.warm.knowledge;
  const coldTotal = data.tiers.cold.memories + data.tiers.cold.knowledge;
  const dbSize = formatBytes(data.dbSizeBytes);

  let output = `\n--- total-recall ------------------------------------\n\n`;
  output += `  Tiers                          Knowledge Base\n`;
  output += `  -----                          --------------\n`;
  output += `  Hot:    ${padLeft(hotTotal, 6)} entries       Memories:  ${data.tiers.hot.memories + data.tiers.warm.memories + data.tiers.cold.memories}\n`;
  output += `  Warm:   ${padLeft(warmTotal, 6)} entries       Knowledge: ${data.tiers.hot.knowledge + data.tiers.warm.knowledge + data.tiers.cold.knowledge}\n`;
  output += `  Cold:   ${padLeft(coldTotal, 6)} entries\n`;
  output += `\n`;
  output += `  DB: ${dbSize}  |  Model: ${data.embeddingModel} (${data.embeddingDimensions}d)\n`;

  if (data.sessionActivity) {
    const sa = data.sessionActivity;
    const usedPct = sa.retrievals > 0 ? Math.round((sa.used / sa.retrievals) * 100) : 0;
    output += `\n`;
    output += `  Session Activity\n`;
    output += `  ----------------\n`;
    output += `  Retrievals this session:  ${sa.retrievals}\n`;
    output += `  |- Used by LLM:           ${sa.used}  (${usedPct}%)\n`;
    output += `  |- Neutral:               ${sa.neutral}\n`;
    output += `  \\- Negative signal:       ${sa.negative}${sa.negative > 0 ? " !" : ""}\n`;
    output += `\n`;
    output += `  Memories captured:         ${sa.memoriesCaptured}\n`;
    output += `  KB queries:                ${sa.kbQueries}  (avg score: ${sa.avgKbScore.toFixed(2)})\n`;
  }

  output += `\n----------------------------------------------------\n`;
  return output;
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function padLeft(n: number, width: number): string {
  return String(n).padStart(width);
}
```

- [ ] **Step 3: Write eval formatter (similar pattern)**

`src/dashboard/eval-formatter.ts` — takes `Metrics` from `src/eval/metrics.ts` and formats it into the TUI eval report shown in the spec (precision, hit rate, miss rate, MRR, by-tier breakdown, top misses).

- [ ] **Step 4: Run tests**

```bash
npx vitest run src/dashboard/
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/dashboard/
git commit -m "feat: add TUI dashboard formatters for status and eval"
```

---

### Task 8: README

**Files:**
- Create: `README.md`
- Create: `LICENSE`

- [ ] **Step 1: Write README**

The README should include:
1. **Hero section** — name, one-line description, badges
2. **The problem** — why existing memory is broken (5 bullets from spec)
3. **The solution** — what total-recall does differently (tiers, KB, portability, observability)
4. **Quick start** — install, first session, what happens automatically
5. **Commands** — full command reference
6. **Architecture** — diagram of MCP server + tiers + host tools
7. **Supported platforms** — Claude Code, Copilot CLI, OpenCode, Cline, Cursor
8. **Configuration** — config.toml reference
9. **Extending** — how to add a new host tool (FieldMapper), new content type, etc.
10. **Built With / Inspired By** — PROMINENT section crediting superpowers

The "Built With / Inspired By" section should read something like:

```markdown
## Built With

total-recall stands on the shoulders of giants:

### [superpowers](https://github.com/obra/superpowers)

total-recall's plugin architecture, skill format, hook system, multi-platform wrapper pattern, and development philosophy are directly inspired by and modeled after the **superpowers** plugin by [obra](https://github.com/obra). superpowers demonstrated that a zero-dependency, markdown-driven skill system could fundamentally improve how AI coding assistants behave — total-recall extends that same philosophy to memory and knowledge management.

Specific patterns borrowed from superpowers:
- **SKILL.md format** with YAML frontmatter and trigger-condition descriptions
- **SessionStart hooks** for injecting core behavior at session start
- **Multi-platform wrappers** (.claude-plugin/, .copilot-plugin/, .cursor-plugin/, .opencode/)
- **Subagent architecture** for isolated, focused task execution
- **Zero-dependency philosophy** — no external services, no API keys, no cloud

If you're building plugins for TUI coding assistants, start with superpowers. It's the foundation this ecosystem needs.
```

- [ ] **Step 2: Create MIT LICENSE**

- [ ] **Step 3: Commit**

```bash
git add README.md LICENSE
git commit -m "docs: add README with architecture docs and superpowers attribution"
```

---

### Task 9: CONTRIBUTING.md

**Files:**
- Create: `CONTRIBUTING.md`

- [ ] **Step 1: Write contribution guide**

Cover:
- How to add a new host tool importer (implement FieldMapper interface)
- How to add a new content type
- How to add a new chunking parser
- How to run tests and benchmarks
- PR requirements (tests pass, benchmark doesn't regress)

- [ ] **Step 2: Commit**

```bash
git add CONTRIBUTING.md
git commit -m "docs: add contributing guide with extension point documentation"
```

---

### Task 10: Final Integration Test and Build Verification

**Files:**
- Create: `src/e2e-phase3.test.ts`

- [ ] **Step 1: Write full system test**

Test should verify:
1. MCP server starts and lists all tools (30+)
2. `session_start` assembles hot tier from imported memories
3. `memory_store` + `memory_search` round-trip works
4. `kb_ingest_dir` + `kb_search` returns correct chunks
5. `session_end` runs compaction without errors
6. `status` returns valid dashboard data
7. `eval_benchmark` produces a report
8. Full build succeeds (`npx tsup`)

- [ ] **Step 2: Run all tests**

```bash
npx vitest run
```

- [ ] **Step 3: Verify build**

```bash
npx tsup && node dist/index.js --help 2>&1 || true
```

- [ ] **Step 4: Final commit**

```bash
git add src/e2e-phase3.test.ts
git commit -m "test: add final integration test verifying full system"
```

---

## Phase 3 Complete

After Phase 3, total-recall is a **complete, installable plugin** with:
- **5 skills** — memory (always-on), search, ingest, status, forget
- **Platform manifests** — Claude Code, Copilot CLI, Cursor, OpenCode
- **Session hooks** — SessionStart injects core skill, SessionEnd triggers compaction
- **Compactor subagent** — intelligent hot-to-warm summarization
- **TUI dashboard** — formatted status and eval output
- **README** — with prominent superpowers attribution
- **CONTRIBUTING.md** — extension point documentation

**The plugin is ready for:** Installation testing on real host tools, community feedback, and iteration.
