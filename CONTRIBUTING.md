# Contributing to total-recall

---

## Getting Started

```bash
git clone https://github.com/strvmarv/total-recall.git
cd total-recall
npm install
npm run build
```

Run tests to verify your environment:

```bash
npm test
```

Run the type checker:

```bash
npm run typecheck
```

---

## Adding a New Host Tool Importer

Host importers detect a specific tool's presence, scan its memory files, and migrate them into total-recall on first run.

### 1. Implement the `HostImporter` interface

The interface is defined in `src/importers/importer.ts`:

```typescript
export interface HostImporter {
  name: string;
  detect(): boolean;
  scan(): { memoryFiles: number; knowledgeFiles: number; sessionFiles: number };
  importMemories(db: Database.Database, embed: EmbedFn, project?: string): ImportResult;
  importKnowledge(db: Database.Database, embed: EmbedFn): ImportResult;
}
```

Create `src/importers/my-tool.ts`:

```typescript
import { existsSync, readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { homedir } from "node:os";
import { createHash } from "node:crypto";
import type Database from "better-sqlite3";
import type { HostImporter, ImportResult } from "./importer.js";
import { insertEntry } from "../db/entries.js";
import { insertEmbedding } from "../search/vector-search.js";

type EmbedFn = (text: string) => Float32Array;

// Where my-tool stores its memories
const MY_TOOL_DIR = join(homedir(), ".my-tool");

export const myToolImporter: HostImporter = {
  name: "my-tool",

  detect() {
    return existsSync(MY_TOOL_DIR);
  },

  scan() {
    if (!existsSync(MY_TOOL_DIR)) {
      return { memoryFiles: 0, knowledgeFiles: 0, sessionFiles: 0 };
    }
    const files = readdirSync(MY_TOOL_DIR).filter((f) => f.endsWith(".md"));
    return { memoryFiles: files.length, knowledgeFiles: 0, sessionFiles: 0 };
  },

  importMemories(db, embed, project) {
    if (!existsSync(MY_TOOL_DIR)) return { imported: 0, skipped: 0, errors: [] };

    const result: ImportResult = { imported: 0, skipped: 0, errors: [] };

    const files = readdirSync(MY_TOOL_DIR).filter((f) => f.endsWith(".md"));
    for (const file of files) {
      const filePath = join(MY_TOOL_DIR, file);
      try {
        const raw = readFileSync(filePath, "utf-8").trim();
        if (!raw) { result.skipped++; continue; }

        const hash = createHash("sha256").update(raw).digest("hex");

        // Check if already imported (use import_log table)
        const existing = db
          .prepare("SELECT id FROM import_log WHERE content_hash = ?")
          .get(hash) as { id: string } | undefined;
        if (existing) { result.skipped++; continue; }

        const id = crypto.randomUUID();
        const embedding = embed(raw);

        insertEntry(db, {
          id,
          content: raw,
          tier: "warm",
          content_type: "note",
          source_tool: "my-tool",
          source_path: filePath,
          project: project ?? null,
        });
        insertEmbedding(db, id, embedding);

        // Log the import to avoid duplicates on next run
        db.prepare(
          "INSERT INTO import_log (id, source_tool, source_path, content_hash, entry_id, tier, content_type) VALUES (?, ?, ?, ?, ?, ?, ?)"
        ).run(crypto.randomUUID(), "my-tool", filePath, hash, id, "warm", "note");

        result.imported++;
      } catch (err) {
        result.errors.push(`${file}: ${String(err)}`);
      }
    }

    return result;
  },

  importKnowledge(_db, _embed) {
    // my-tool has no separate knowledge files
    return { imported: 0, skipped: 0, errors: [] };
  },
};
```

### 2. Register the importer

Add it to the importer list in `src/index.ts` (or wherever importers are initialized at startup).

### 3. Add tests

Create `src/importers/my-tool.test.ts` mirroring the existing `claude-code.test.ts` structure.

---

## Adding a New Content Type

Content types classify what kind of information a memory or knowledge chunk contains (e.g., `note`, `code`, `doc`, `decision`).

### 1. Add the type to the schema

In `src/db/schema.ts`, find the `content_type` column definition and add your new type to the CHECK constraint or lookup table:

```sql
-- In the entries table or a lookup table:
content_type TEXT CHECK(content_type IN ('note', 'code', 'doc', 'decision', 'my-new-type'))
```

### 2. Add the type to the TypeScript union

In `src/types.ts`, extend the `ContentType` union:

```typescript
export type ContentType = "note" | "code" | "doc" | "decision" | "my-new-type";
```

### 3. Assign a relevance weight (optional)

If your content type should be scored differently during retrieval, add a weight entry in the search relevance config (see `src/search/`).

---

## Adding a New Chunking Parser

The chunker in `src/ingestion/chunker.ts` dispatches to per-format parsers based on file extension. To add support for a new format:

### 1. Implement the parser

Create `src/ingestion/my-format-parser.ts`. Your parser must return `Chunk[]`:

```typescript
import type { Chunk } from "./chunker.js";

export interface ParseOptions {
  maxTokens: number;
  overlapTokens?: number;
}

export function parseMyFormat(content: string, opts: ParseOptions): Chunk[] {
  const chunks: Chunk[] = [];

  // Split your format into logical units.
  // Each chunk needs: content, startLine, endLine.
  // Optionally: headingPath (for outline-like formats), name/kind (for code-like formats).

  let lineNumber = 1;
  for (const section of splitIntoSections(content)) {
    chunks.push({
      content: section.text,
      startLine: lineNumber,
      endLine: lineNumber + section.text.split("\n").length - 1,
    });
    lineNumber += section.text.split("\n").length + 1;
  }

  return chunks;
}

function splitIntoSections(content: string) {
  // Your format-specific splitting logic here
  return content.split(/\n---\n/).map((text) => ({ text }));
}
```

### 2. Register the parser in `chunker.ts`

In `src/ingestion/chunker.ts`, add your file extensions and import:

```typescript
import { parseMyFormat } from "./my-format-parser.js";

const MY_FORMAT_EXTENSIONS = new Set([".myext", ".myfmt"]);
```

Then add a branch in the `chunkFile` function before the fallback:

```typescript
export function chunkFile(content, filePath, opts) {
  // ...existing Markdown and code branches...

  if (MY_FORMAT_EXTENSIONS.has(ext)) {
    return parseMyFormat(content, opts);
  }

  // Fallback: paragraph-based splitting
  return splitByParagraphs(content, opts.maxTokens);
}
```

### 3. Add tests

Create `src/ingestion/my-format-parser.test.ts`. Test at minimum: empty input, single section, multiple sections, sections exceeding `maxTokens`.

---

## Running Tests

Run all tests once:

```bash
npm test
# or
npx vitest run
```

Run in watch mode during development:

```bash
npm run test:watch
# or
npx vitest
```

Run a specific test file:

```bash
npx vitest run src/importers/my-tool.test.ts
```

---

## Running Benchmarks

Once the MCP server is running and connected to your coding assistant, use the eval commands:

```
/total-recall:commands eval                      # Live retrieval metrics for current session
/total-recall:commands eval --benchmark          # Run synthetic benchmark suite
/total-recall:commands eval --snapshot baseline  # Save current config as a named baseline
/total-recall:commands eval --compare baseline   # Compare current config against saved baseline
/total-recall:commands eval --grow               # Add real query misses to benchmark suite
```

A PR that changes retrieval logic, scoring, or compaction thresholds must include a `--benchmark` run showing no regression against the `baseline` snapshot.

---

## PR Requirements

Before opening a pull request:

1. **Tests pass** — `npm test` exits 0 with no failures
2. **Type checker clean** — `npm run typecheck` exits 0
3. **Build succeeds** — `npm run build` exits 0
4. **Benchmark does not regress** — run `/total-recall:commands eval --compare baseline` and include the output in your PR description if you changed retrieval, scoring, or compaction logic
5. **New behavior is tested** — new importers, parsers, and content types all require corresponding test files

If you're adding a new host tool importer, include the `detect()` logic rationale in your PR description — false positives will silently corrupt imports for users who don't have the tool installed.
