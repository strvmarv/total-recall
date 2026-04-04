# total-recall Phase 2: Knowledge Base, Compaction, Importers, Eval

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the four major feature systems that make total-recall genuinely useful: hierarchical knowledge base ingestion, automated compaction pipeline, host tool importers (Claude Code + Copilot CLI), and the evaluation framework with synthetic benchmarks.

**Architecture:** Builds on Phase 1's MCP server, SQLite schema, and embedding engine. Adds new modules that integrate with existing `db/entries.ts` and `search/vector-search.ts`. Compaction uses the existing `decay.ts` scoring. Importers follow the `HostImporter` interface pattern. Eval framework adds a new instrumentation layer that wraps all retrieval calls.

**Tech Stack:** Same as Phase 1 + `chokidar` (file watching for importers), `gray-matter` (YAML frontmatter parsing for Claude Code memories)

**Spec Reference:** `docs/superpowers/specs/2026-04-04-total-recall-design.md` — Sections 2 (compaction), 4 (ingestion + importers), 5 (eval)

**Depends on:** Phase 1 complete (all 11 tasks)

---

## File Structure (New files in Phase 2)

```
src/
  ingestion/
    chunker.ts                  # Format-aware semantic chunking
    chunker.test.ts
    markdown-parser.ts          # Markdown heading/code-block aware splitting
    markdown-parser.test.ts
    code-parser.ts              # Function/class boundary splitting
    code-parser.test.ts
    hierarchical-index.ts       # Collection/document/chunk hierarchy management
    hierarchical-index.test.ts
    ingest.ts                   # Public ingestion API (file + directory)
    ingest.test.ts
  compaction/
    compactor.ts                # Hot->warm compaction logic
    compactor.test.ts
    warm-sweep.ts               # Warm->cold decay sweep
    warm-sweep.test.ts
    cold-promotion.ts           # Cold->warm promotion on repeated access
    cold-promotion.test.ts
  importers/
    importer.ts                 # HostImporter interface + registry
    claude-code.ts              # Claude Code importer
    claude-code.test.ts
    copilot-cli.ts              # Copilot CLI importer
    copilot-cli.test.ts
    field-mapper.ts             # Shared JSONL field normalization
    field-mapper.test.ts
    pattern-extractor.ts        # Shared correction/preference mining from sessions
    pattern-extractor.test.ts
  eval/
    event-logger.ts             # Retrieval event instrumentation
    event-logger.test.ts
    outcome-detector.ts         # Post-response outcome analysis
    outcome-detector.test.ts
    metrics.ts                  # Metric computation from events
    metrics.test.ts
    benchmark-runner.ts         # Synthetic benchmark execution
    benchmark-runner.test.ts
  tools/
    kb-tools.ts                 # kb_* MCP tool handlers
    eval-tools.ts               # eval_* MCP tool handlers
    import-tools.ts             # import_host MCP tool handler
    session-tools.ts            # session_start, session_end, session_context
eval/
  corpus/
    memories.jsonl
    corrections.jsonl
    knowledge/
      sample-api-docs.md
      sample-architecture.md
      sample-readme.md
  benchmarks/
    retrieval.jsonl
    compaction.jsonl
    cross-tier.jsonl
```

---

### Task 1: Markdown Chunking Parser

**Files:**
- Create: `src/ingestion/markdown-parser.ts`
- Create: `src/ingestion/markdown-parser.test.ts`

- [ ] **Step 1: Write failing tests for markdown chunking**

```typescript
// src/ingestion/markdown-parser.test.ts
import { describe, it, expect } from "vitest";
import { parseMarkdown } from "./markdown-parser.js";

describe("parseMarkdown", () => {
  it("splits on headings and preserves heading path", () => {
    const md = `# API Reference

Some intro text.

## Authentication

Auth overview here.

### OAuth Flow

OAuth details here with enough text to form a chunk.

### Token Refresh

Token refresh details here.

## Deployment

Deploy info here.`;

    const chunks = parseMarkdown(md, { maxTokens: 200 });

    expect(chunks.length).toBeGreaterThanOrEqual(4);
    const oauthChunk = chunks.find((c) => c.content.includes("OAuth details"));
    expect(oauthChunk).toBeDefined();
    expect(oauthChunk!.headingPath).toEqual(["API Reference", "Authentication", "OAuth Flow"]);

    const deployChunk = chunks.find((c) => c.content.includes("Deploy info"));
    expect(deployChunk).toBeDefined();
    expect(deployChunk!.headingPath).toEqual(["API Reference", "Deployment"]);
  });

  it("never splits mid-code-block", () => {
    const md = `# Setup

\`\`\`typescript
function veryLongFunction() {
  const a = 1;
  const b = 2;
  const c = 3;
  const d = 4;
  const e = 5;
  return a + b + c + d + e;
}
\`\`\`

Some text after.`;

    const chunks = parseMarkdown(md, { maxTokens: 50 });

    // The code block should be in ONE chunk, not split
    const codeChunks = chunks.filter((c) => c.content.includes("veryLongFunction"));
    expect(codeChunks.length).toBe(1);
    expect(codeChunks[0].content).toContain("return a + b");
  });

  it("respects max token limit with overlap", () => {
    const longParagraph = "word ".repeat(300);
    const md = `# Title\n\n${longParagraph}`;

    const chunks = parseMarkdown(md, { maxTokens: 100, overlapTokens: 20 });

    expect(chunks.length).toBeGreaterThan(1);
    // Each chunk should be roughly within the token limit
    for (const chunk of chunks) {
      const approxTokens = chunk.content.split(/\s+/).length;
      // Allow some slack for heading text prepended
      expect(approxTokens).toBeLessThan(150);
    }
  });

  it("returns empty array for empty input", () => {
    expect(parseMarkdown("", { maxTokens: 100 })).toEqual([]);
  });
});
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
npx vitest run src/ingestion/markdown-parser.test.ts
```

Expected: FAIL — module not found.

- [ ] **Step 3: Implement markdown parser**

```typescript
// src/ingestion/markdown-parser.ts

export interface MarkdownChunk {
  content: string;
  headingPath: string[];
  startLine: number;
  endLine: number;
}

interface ParseOptions {
  maxTokens: number;
  overlapTokens?: number;
}

export function parseMarkdown(text: string, opts: ParseOptions): MarkdownChunk[] {
  if (!text.trim()) return [];

  const lines = text.split("\n");
  const sections = splitOnHeadings(lines);
  const chunks: MarkdownChunk[] = [];
  const overlap = opts.overlapTokens ?? 0;

  for (const section of sections) {
    const sectionText = section.lines.join("\n").trim();
    if (!sectionText) continue;

    const approxTokens = estimateTokens(sectionText);

    if (approxTokens <= opts.maxTokens) {
      chunks.push({
        content: sectionText,
        headingPath: section.headingPath,
        startLine: section.startLine,
        endLine: section.endLine,
      });
    } else {
      // Split large sections at paragraph/code-block boundaries
      const subChunks = splitLargeSection(
        section.lines,
        section.headingPath,
        section.startLine,
        opts.maxTokens,
        overlap,
      );
      chunks.push(...subChunks);
    }
  }

  return chunks;
}

interface Section {
  headingPath: string[];
  lines: string[];
  startLine: number;
  endLine: number;
}

function splitOnHeadings(lines: string[]): Section[] {
  const sections: Section[] = [];
  let currentPath: string[] = [];
  let currentLines: string[] = [];
  let startLine = 0;

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const headingMatch = line.match(/^(#{1,6})\s+(.+)$/);

    if (headingMatch) {
      // Flush previous section
      if (currentLines.length > 0) {
        sections.push({
          headingPath: [...currentPath],
          lines: currentLines,
          startLine,
          endLine: i - 1,
        });
      }

      const level = headingMatch[1].length;
      const title = headingMatch[2].trim();

      // Update heading path
      currentPath = currentPath.slice(0, level - 1);
      currentPath[level - 1] = title;
      currentPath = currentPath.slice(0, level);

      currentLines = [line];
      startLine = i;
    } else {
      currentLines.push(line);
    }
  }

  // Flush final section
  if (currentLines.length > 0) {
    sections.push({
      headingPath: [...currentPath],
      lines: currentLines,
      startLine,
      endLine: lines.length - 1,
    });
  }

  return sections;
}

function splitLargeSection(
  lines: string[],
  headingPath: string[],
  globalStartLine: number,
  maxTokens: number,
  overlapTokens: number,
): MarkdownChunk[] {
  const chunks: MarkdownChunk[] = [];
  const blocks = splitIntoBlocks(lines);
  let currentBlock: string[] = [];
  let currentTokens = 0;
  let blockStartLine = globalStartLine;

  for (const block of blocks) {
    const blockTokens = estimateTokens(block.text);

    if (currentTokens + blockTokens > maxTokens && currentBlock.length > 0) {
      chunks.push({
        content: currentBlock.join("\n\n").trim(),
        headingPath,
        startLine: blockStartLine,
        endLine: globalStartLine + block.endLine,
      });

      // Handle overlap: keep last portion of current block
      if (overlapTokens > 0) {
        const overlapText = getLastNTokens(currentBlock.join("\n\n"), overlapTokens);
        currentBlock = overlapText ? [overlapText] : [];
        currentTokens = estimateTokens(currentBlock.join(""));
      } else {
        currentBlock = [];
        currentTokens = 0;
      }
      blockStartLine = globalStartLine + block.startLine;
    }

    currentBlock.push(block.text);
    currentTokens += blockTokens;
  }

  if (currentBlock.length > 0) {
    chunks.push({
      content: currentBlock.join("\n\n").trim(),
      headingPath,
      startLine: blockStartLine,
      endLine: globalStartLine + lines.length - 1,
    });
  }

  return chunks;
}

interface Block {
  text: string;
  startLine: number;
  endLine: number;
  isCode: boolean;
}

function splitIntoBlocks(lines: string[]): Block[] {
  const blocks: Block[] = [];
  let inCodeBlock = false;
  let currentLines: string[] = [];
  let startLine = 0;

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];

    if (line.startsWith("```")) {
      if (inCodeBlock) {
        // End of code block — flush as single block
        currentLines.push(line);
        blocks.push({
          text: currentLines.join("\n"),
          startLine,
          endLine: i,
          isCode: true,
        });
        currentLines = [];
        inCodeBlock = false;
        startLine = i + 1;
      } else {
        // Flush any pending text
        if (currentLines.length > 0 && currentLines.some((l) => l.trim())) {
          blocks.push({
            text: currentLines.join("\n"),
            startLine,
            endLine: i - 1,
            isCode: false,
          });
        }
        currentLines = [line];
        startLine = i;
        inCodeBlock = true;
      }
    } else if (!inCodeBlock && line.trim() === "" && currentLines.length > 0) {
      // Paragraph break
      if (currentLines.some((l) => l.trim())) {
        blocks.push({
          text: currentLines.join("\n"),
          startLine,
          endLine: i - 1,
          isCode: false,
        });
      }
      currentLines = [];
      startLine = i + 1;
    } else {
      currentLines.push(line);
    }
  }

  if (currentLines.length > 0 && currentLines.some((l) => l.trim())) {
    blocks.push({
      text: currentLines.join("\n"),
      startLine,
      endLine: lines.length - 1,
      isCode: inCodeBlock,
    });
  }

  return blocks;
}

function estimateTokens(text: string): number {
  // Rough approximation: ~0.75 tokens per word for English
  return Math.ceil(text.split(/\s+/).filter(Boolean).length * 0.75);
}

function getLastNTokens(text: string, n: number): string {
  const words = text.split(/\s+/).filter(Boolean);
  const wordCount = Math.ceil(n / 0.75);
  return words.slice(-wordCount).join(" ");
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
npx vitest run src/ingestion/markdown-parser.test.ts
```

Expected: All 4 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/ingestion/markdown-parser.ts src/ingestion/markdown-parser.test.ts
git commit -m "feat: add markdown-aware semantic chunking parser"
```

---

### Task 2: Code File Parser

**Files:**
- Create: `src/ingestion/code-parser.ts`
- Create: `src/ingestion/code-parser.test.ts`

- [ ] **Step 1: Write failing tests**

```typescript
// src/ingestion/code-parser.test.ts
import { describe, it, expect } from "vitest";
import { parseCode } from "./code-parser.js";

describe("parseCode", () => {
  it("splits TypeScript on function boundaries", () => {
    const code = `import { foo } from "bar";

export function handleAuth(req: Request): Response {
  const token = req.headers.get("authorization");
  if (!token) return new Response("Unauthorized", { status: 401 });
  return new Response("OK");
}

export function handleRefresh(req: Request): Response {
  const refresh = req.body.refreshToken;
  return new Response(JSON.stringify({ token: "new" }));
}

export class UserService {
  private db: Database;

  constructor(db: Database) {
    this.db = db;
  }

  getUser(id: string) {
    return this.db.get(id);
  }
}`;

    const chunks = parseCode(code, "typescript", { maxTokens: 200 });

    expect(chunks.length).toBeGreaterThanOrEqual(3);
    const authChunk = chunks.find((c) => c.content.includes("handleAuth"));
    expect(authChunk).toBeDefined();
    const classChunk = chunks.find((c) => c.content.includes("UserService"));
    expect(classChunk).toBeDefined();
  });

  it("keeps imports as a separate chunk if large enough", () => {
    const imports = Array.from({ length: 20 }, (_, i) => `import { mod${i} } from "pkg${i}";`).join("\n");
    const code = `${imports}\n\nfunction main() { return 1; }`;

    const chunks = parseCode(code, "typescript", { maxTokens: 50 });
    expect(chunks.length).toBeGreaterThanOrEqual(2);
  });

  it("handles Python function/class boundaries", () => {
    const code = `import os

def authenticate(token: str) -> bool:
    """Check if token is valid."""
    return token == os.environ["SECRET"]

class AuthService:
    def __init__(self, db):
        self.db = db

    def get_user(self, user_id: str):
        return self.db.find(user_id)`;

    const chunks = parseCode(code, "python", { maxTokens: 200 });
    expect(chunks.length).toBeGreaterThanOrEqual(2);
  });
});
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
npx vitest run src/ingestion/code-parser.test.ts
```

Expected: FAIL.

- [ ] **Step 3: Implement code parser**

```typescript
// src/ingestion/code-parser.ts

export interface CodeChunk {
  content: string;
  name: string;
  kind: "import" | "function" | "class" | "block";
  startLine: number;
  endLine: number;
}

interface ParseOptions {
  maxTokens: number;
}

// Language-specific patterns for function/class boundaries
const BOUNDARY_PATTERNS: Record<string, RegExp[]> = {
  typescript: [
    /^(?:export\s+)?(?:async\s+)?function\s+\w+/,
    /^(?:export\s+)?class\s+\w+/,
    /^(?:export\s+)?(?:const|let)\s+\w+\s*=\s*(?:async\s*)?\(/,
    /^(?:export\s+)?interface\s+\w+/,
    /^(?:export\s+)?type\s+\w+/,
    /^(?:export\s+)?enum\s+\w+/,
  ],
  javascript: [
    /^(?:export\s+)?(?:async\s+)?function\s+\w+/,
    /^(?:export\s+)?class\s+\w+/,
    /^(?:export\s+)?(?:const|let|var)\s+\w+\s*=\s*(?:async\s*)?\(/,
  ],
  python: [
    /^(?:async\s+)?def\s+\w+/,
    /^class\s+\w+/,
  ],
  go: [
    /^func\s+(?:\(\w+\s+\*?\w+\)\s+)?\w+/,
    /^type\s+\w+\s+struct/,
    /^type\s+\w+\s+interface/,
  ],
  rust: [
    /^(?:pub\s+)?(?:async\s+)?fn\s+\w+/,
    /^(?:pub\s+)?struct\s+\w+/,
    /^(?:pub\s+)?enum\s+\w+/,
    /^(?:pub\s+)?trait\s+\w+/,
    /^impl\s+/,
  ],
};

const IMPORT_PATTERNS: Record<string, RegExp> = {
  typescript: /^import\s+/,
  javascript: /^(?:import|require)\s*/,
  python: /^(?:import|from)\s+/,
  go: /^import\s+/,
  rust: /^use\s+/,
};

export function parseCode(
  code: string,
  language: string,
  opts: ParseOptions,
): CodeChunk[] {
  const lines = code.split("\n");
  const lang = language.toLowerCase();
  const boundaries = BOUNDARY_PATTERNS[lang] ?? BOUNDARY_PATTERNS.typescript;
  const importPattern = IMPORT_PATTERNS[lang] ?? IMPORT_PATTERNS.typescript;

  const rawSegments = splitOnBoundaries(lines, boundaries, importPattern);
  const chunks: CodeChunk[] = [];

  for (const segment of rawSegments) {
    const approxTokens = estimateTokens(segment.content);

    if (approxTokens <= opts.maxTokens) {
      chunks.push(segment);
    } else {
      // Split oversized segments at blank lines
      const subChunks = splitAtBlankLines(segment, opts.maxTokens);
      chunks.push(...subChunks);
    }
  }

  return chunks.filter((c) => c.content.trim().length > 0);
}

interface RawSegment {
  content: string;
  name: string;
  kind: "import" | "function" | "class" | "block";
  startLine: number;
  endLine: number;
}

function splitOnBoundaries(
  lines: string[],
  boundaries: RegExp[],
  importPattern: RegExp,
): RawSegment[] {
  const segments: RawSegment[] = [];
  let currentLines: string[] = [];
  let currentKind: RawSegment["kind"] = "block";
  let currentName = "imports";
  let startLine = 0;
  let braceDepth = 0;
  let inImports = true;

  for (let i = 0; i < lines.length; i++) {
    const trimmed = lines[i].trimStart();

    // Check if this line is an import
    if (inImports && importPattern.test(trimmed)) {
      currentLines.push(lines[i]);
      currentKind = "import";
      continue;
    }

    // Check for boundary
    let matched = false;
    for (const pattern of boundaries) {
      if (pattern.test(trimmed)) {
        // Flush previous segment
        if (currentLines.length > 0 && currentLines.some((l) => l.trim())) {
          segments.push({
            content: currentLines.join("\n"),
            name: currentName,
            kind: currentKind,
            startLine,
            endLine: i - 1,
          });
        }

        inImports = false;
        currentLines = [lines[i]];
        startLine = i;
        currentName = extractName(trimmed);
        currentKind = trimmed.includes("class") ? "class" : "function";
        matched = true;
        break;
      }
    }

    if (!matched) {
      if (inImports && trimmed && !importPattern.test(trimmed)) {
        // End of import section
        if (currentLines.length > 0) {
          segments.push({
            content: currentLines.join("\n"),
            name: "imports",
            kind: "import",
            startLine,
            endLine: i - 1,
          });
          currentLines = [];
          startLine = i;
        }
        inImports = false;
      }
      currentLines.push(lines[i]);
    }
  }

  // Flush final segment
  if (currentLines.length > 0 && currentLines.some((l) => l.trim())) {
    segments.push({
      content: currentLines.join("\n"),
      name: currentName,
      kind: currentKind,
      startLine,
      endLine: lines.length - 1,
    });
  }

  return segments;
}

function extractName(line: string): string {
  const match = line.match(/(?:function|class|def|fn|struct|enum|trait|interface|type|impl)\s+(\w+)/);
  return match?.[1] ?? "anonymous";
}

function splitAtBlankLines(segment: RawSegment, maxTokens: number): CodeChunk[] {
  const lines = segment.content.split("\n");
  const chunks: CodeChunk[] = [];
  let current: string[] = [];
  let currentTokens = 0;

  for (let i = 0; i < lines.length; i++) {
    const lineTokens = estimateTokens(lines[i]);

    if (currentTokens + lineTokens > maxTokens && current.length > 0) {
      chunks.push({
        content: current.join("\n"),
        name: segment.name,
        kind: segment.kind,
        startLine: segment.startLine,
        endLine: segment.startLine + i,
      });
      current = [];
      currentTokens = 0;
    }

    current.push(lines[i]);
    currentTokens += lineTokens;
  }

  if (current.length > 0) {
    chunks.push({
      content: current.join("\n"),
      name: segment.name,
      kind: segment.kind,
      startLine: segment.startLine,
      endLine: segment.endLine,
    });
  }

  return chunks;
}

function estimateTokens(text: string): number {
  return Math.ceil(text.split(/\s+/).filter(Boolean).length * 0.75);
}
```

- [ ] **Step 4: Run tests**

```bash
npx vitest run src/ingestion/code-parser.test.ts
```

Expected: All 3 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/ingestion/code-parser.ts src/ingestion/code-parser.test.ts
git commit -m "feat: add code-aware chunking parser with multi-language support"
```

---

### Task 3: Unified Chunker (Format Detection + Dispatch)

**Files:**
- Create: `src/ingestion/chunker.ts`
- Create: `src/ingestion/chunker.test.ts`

- [ ] **Step 1: Write failing tests**

```typescript
// src/ingestion/chunker.test.ts
import { describe, it, expect } from "vitest";
import { chunkFile } from "./chunker.js";

describe("chunkFile", () => {
  it("detects markdown and uses markdown parser", () => {
    const chunks = chunkFile("# Title\n\n## Section\n\nContent here.", "docs/readme.md", { maxTokens: 200 });
    expect(chunks.length).toBeGreaterThan(0);
    expect(chunks[0].headingPath).toBeDefined();
  });

  it("detects TypeScript and uses code parser", () => {
    const chunks = chunkFile("export function hello() { return 1; }", "src/index.ts", { maxTokens: 200 });
    expect(chunks.length).toBeGreaterThan(0);
  });

  it("falls back to paragraph splitting for unknown formats", () => {
    const text = "Paragraph one.\n\nParagraph two.\n\nParagraph three.";
    const chunks = chunkFile(text, "notes.txt", { maxTokens: 200 });
    expect(chunks.length).toBeGreaterThan(0);
  });

  it("detects language from file extension", () => {
    const pyCode = "def hello():\n    return 1\n\ndef world():\n    return 2";
    const chunks = chunkFile(pyCode, "utils.py", { maxTokens: 200 });
    expect(chunks.length).toBeGreaterThanOrEqual(1);
  });
});
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
npx vitest run src/ingestion/chunker.test.ts
```

- [ ] **Step 3: Implement chunker**

```typescript
// src/ingestion/chunker.ts
import { extname } from "node:path";
import { parseMarkdown, type MarkdownChunk } from "./markdown-parser.js";
import { parseCode, type CodeChunk } from "./code-parser.js";

export interface Chunk {
  content: string;
  headingPath?: string[];
  name?: string;
  kind?: string;
  startLine: number;
  endLine: number;
}

interface ChunkOptions {
  maxTokens: number;
  overlapTokens?: number;
}

const EXT_TO_LANGUAGE: Record<string, string> = {
  ".ts": "typescript",
  ".tsx": "typescript",
  ".js": "javascript",
  ".jsx": "javascript",
  ".py": "python",
  ".go": "go",
  ".rs": "rust",
  ".java": "typescript", // Similar enough syntax for boundary detection
  ".kt": "typescript",
  ".cs": "typescript",
  ".cpp": "typescript",
  ".c": "typescript",
  ".h": "typescript",
};

const MARKDOWN_EXTS = new Set([".md", ".mdx", ".markdown", ".rst"]);
const CODE_EXTS = new Set(Object.keys(EXT_TO_LANGUAGE));

export function chunkFile(content: string, filePath: string, opts: ChunkOptions): Chunk[] {
  const ext = extname(filePath).toLowerCase();

  if (MARKDOWN_EXTS.has(ext)) {
    return parseMarkdown(content, opts).map(mdToChunk);
  }

  if (CODE_EXTS.has(ext)) {
    const language = EXT_TO_LANGUAGE[ext] ?? "typescript";
    return parseCode(content, language, opts).map(codeToChunk);
  }

  // Fallback: paragraph-based splitting
  return splitParagraphs(content, opts);
}

function mdToChunk(mc: MarkdownChunk): Chunk {
  return {
    content: mc.content,
    headingPath: mc.headingPath,
    startLine: mc.startLine,
    endLine: mc.endLine,
  };
}

function codeToChunk(cc: CodeChunk): Chunk {
  return {
    content: cc.content,
    name: cc.name,
    kind: cc.kind,
    startLine: cc.startLine,
    endLine: cc.endLine,
  };
}

function splitParagraphs(text: string, opts: ChunkOptions): Chunk[] {
  const paragraphs = text.split(/\n\s*\n/).filter((p) => p.trim());
  const chunks: Chunk[] = [];
  let current: string[] = [];
  let currentTokens = 0;
  let startLine = 0;
  let lineCount = 0;

  for (const para of paragraphs) {
    const paraTokens = Math.ceil(para.split(/\s+/).filter(Boolean).length * 0.75);

    if (currentTokens + paraTokens > opts.maxTokens && current.length > 0) {
      chunks.push({
        content: current.join("\n\n"),
        startLine,
        endLine: startLine + lineCount,
      });
      current = [];
      currentTokens = 0;
      startLine += lineCount + 1;
      lineCount = 0;
    }

    current.push(para);
    currentTokens += paraTokens;
    lineCount += para.split("\n").length;
  }

  if (current.length > 0) {
    chunks.push({
      content: current.join("\n\n"),
      startLine,
      endLine: startLine + lineCount,
    });
  }

  return chunks;
}
```

- [ ] **Step 4: Run tests**

```bash
npx vitest run src/ingestion/chunker.test.ts
```

Expected: All 4 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/ingestion/chunker.ts src/ingestion/chunker.test.ts
git commit -m "feat: add unified chunker with format detection and dispatch"
```

---

### Task 4: Hierarchical Index (Collection/Document/Chunk)

**Files:**
- Create: `src/ingestion/hierarchical-index.ts`
- Create: `src/ingestion/hierarchical-index.test.ts`

- [ ] **Step 1: Write failing tests**

```typescript
// src/ingestion/hierarchical-index.test.ts
import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { createTestDb } from "../../tests/helpers/db.js";
import { mockEmbedSemantic } from "../../tests/helpers/embedding.js";
import {
  createCollection,
  addDocumentToCollection,
  getCollection,
  listCollections,
  getDocumentChunks,
} from "./hierarchical-index.js";
import type Database from "better-sqlite3";

describe("hierarchical index", () => {
  let db: Database.Database;

  beforeEach(() => { db = createTestDb(); });
  afterEach(() => { db.close(); });

  it("creates a collection and adds documents with chunks", () => {
    const collectionId = createCollection(db, mockEmbedSemantic, {
      name: "auth-docs",
      sourcePath: "docs/auth/",
    });

    const docId = addDocumentToCollection(db, mockEmbedSemantic, {
      collectionId,
      sourcePath: "docs/auth/oauth-flow.md",
      chunks: [
        { content: "OAuth2 flow description", headingPath: ["Auth", "OAuth Flow"] },
        { content: "Token refresh mechanism", headingPath: ["Auth", "Token Refresh"] },
      ],
    });

    const collection = getCollection(db, collectionId);
    expect(collection).not.toBeNull();
    expect(collection!.name).toBe("auth-docs");

    const chunks = getDocumentChunks(db, docId);
    expect(chunks).toHaveLength(2);
    expect(chunks[0].content).toBe("OAuth2 flow description");
  });

  it("lists all collections with document counts", () => {
    createCollection(db, mockEmbedSemantic, { name: "auth", sourcePath: "docs/auth/" });
    createCollection(db, mockEmbedSemantic, { name: "deploy", sourcePath: "docs/deploy/" });

    const collections = listCollections(db);
    expect(collections).toHaveLength(2);
  });
});
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
npx vitest run src/ingestion/hierarchical-index.test.ts
```

- [ ] **Step 3: Implement hierarchical index**

```typescript
// src/ingestion/hierarchical-index.ts
import type Database from "better-sqlite3";
import { insertEntry, getEntry, listEntries } from "../db/entries.js";
import { insertEmbedding } from "../search/vector-search.js";
import type { Entry } from "../types.js";

type EmbedFn = (text: string) => Float32Array;

interface CreateCollectionOpts {
  name: string;
  sourcePath: string;
}

interface AddDocumentOpts {
  collectionId: string;
  sourcePath: string;
  chunks: Array<{
    content: string;
    headingPath?: string[];
    name?: string;
    kind?: string;
  }>;
}

export function createCollection(
  db: Database.Database,
  embed: EmbedFn,
  opts: CreateCollectionOpts,
): string {
  const id = insertEntry(db, "cold", "knowledge", {
    content: `Collection: ${opts.name}`,
    source: opts.sourcePath,
    source_tool: "manual",
    metadata: {
      type: "collection",
      name: opts.name,
      source_path: opts.sourcePath,
    },
  });

  const vec = embed(`Collection: ${opts.name}`);
  insertEmbedding(db, "cold", "knowledge", id, vec);

  return id;
}

export function addDocumentToCollection(
  db: Database.Database,
  embed: EmbedFn,
  opts: AddDocumentOpts,
): string {
  // Create document entry
  const docContent = opts.chunks.map((c) => c.content).join("\n").slice(0, 500);
  const docId = insertEntry(db, "cold", "knowledge", {
    content: docContent,
    source: opts.sourcePath,
    source_tool: "manual",
    collection_id: opts.collectionId,
    metadata: {
      type: "document",
      source_path: opts.sourcePath,
      chunk_count: opts.chunks.length,
    },
  });

  const docVec = embed(docContent);
  insertEmbedding(db, "cold", "knowledge", docId, docVec);

  // Create chunk entries
  for (const chunk of opts.chunks) {
    const chunkId = insertEntry(db, "cold", "knowledge", {
      content: chunk.content,
      source: opts.sourcePath,
      source_tool: "manual",
      parent_id: docId,
      collection_id: opts.collectionId,
      metadata: {
        type: "chunk",
        heading_path: chunk.headingPath ?? [],
        name: chunk.name,
        kind: chunk.kind,
      },
    });

    const chunkVec = embed(chunk.content);
    insertEmbedding(db, "cold", "knowledge", chunkId, chunkVec);
  }

  return docId;
}

export function getCollection(
  db: Database.Database,
  id: string,
): (Entry & { name: string }) | null {
  const entry = getEntry(db, "cold", "knowledge", id);
  if (!entry) return null;
  const meta = entry.metadata as Record<string, unknown>;
  if (meta.type !== "collection") return null;
  return { ...entry, name: meta.name as string };
}

export function listCollections(db: Database.Database): Array<Entry & { name: string }> {
  const all = listEntries(db, "cold", "knowledge");
  return all
    .filter((e) => (e.metadata as Record<string, unknown>).type === "collection")
    .map((e) => ({ ...e, name: (e.metadata as Record<string, unknown>).name as string }));
}

export function getDocumentChunks(db: Database.Database, docId: string): Entry[] {
  const all = listEntries(db, "cold", "knowledge");
  return all.filter((e) => e.parent_id === docId);
}
```

- [ ] **Step 4: Run tests**

```bash
npx vitest run src/ingestion/hierarchical-index.test.ts
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/ingestion/hierarchical-index.ts src/ingestion/hierarchical-index.test.ts
git commit -m "feat: add hierarchical knowledge base index (collection/document/chunk)"
```

---

### Task 5: Ingestion API (File + Directory)

**Files:**
- Create: `src/ingestion/ingest.ts`
- Create: `src/ingestion/ingest.test.ts`

- [ ] **Step 1: Write failing tests**

```typescript
// src/ingestion/ingest.test.ts
import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { createTestDb } from "../../tests/helpers/db.js";
import { mockEmbedSemantic } from "../../tests/helpers/embedding.js";
import { ingestFile, ingestDirectory } from "./ingest.js";
import { listCollections, getDocumentChunks } from "./hierarchical-index.js";
import { countEntries } from "../db/entries.js";
import { writeFileSync, mkdirSync, rmSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import type Database from "better-sqlite3";

describe("ingestion", () => {
  let db: Database.Database;
  let tmpDir: string;

  beforeEach(() => {
    db = createTestDb();
    tmpDir = join(tmpdir(), `tr-test-${Date.now()}`);
    mkdirSync(tmpDir, { recursive: true });
  });

  afterEach(() => {
    db.close();
    rmSync(tmpDir, { recursive: true, force: true });
  });

  it("ingests a single markdown file", () => {
    const filePath = join(tmpDir, "readme.md");
    writeFileSync(filePath, "# My Project\n\n## Setup\n\nInstall with pnpm.\n\n## Usage\n\nRun the thing.");

    const result = ingestFile(db, mockEmbedSemantic, filePath);

    expect(result.documentId).toBeDefined();
    expect(result.chunkCount).toBeGreaterThan(0);
    expect(countEntries(db, "cold", "knowledge")).toBeGreaterThan(1);
  });

  it("ingests a directory into a collection", () => {
    writeFileSync(join(tmpDir, "auth.md"), "# Auth\n\nAuth docs here.");
    writeFileSync(join(tmpDir, "deploy.md"), "# Deploy\n\nDeploy docs here.");
    mkdirSync(join(tmpDir, "sub"));
    writeFileSync(join(tmpDir, "sub", "nested.md"), "# Nested\n\nNested doc.");

    const result = ingestDirectory(db, mockEmbedSemantic, tmpDir);

    expect(result.collectionId).toBeDefined();
    expect(result.documentCount).toBe(3);
    expect(result.totalChunks).toBeGreaterThan(0);
  });

  it("validates ingested content with self-match test", () => {
    const filePath = join(tmpDir, "api.md");
    writeFileSync(filePath, "# API Reference\n\n## Endpoints\n\nGET /users returns a list of users.");

    const result = ingestFile(db, mockEmbedSemantic, filePath);
    expect(result.validationPassed).toBe(true);
  });
});
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
npx vitest run src/ingestion/ingest.test.ts
```

- [ ] **Step 3: Implement ingestion API**

```typescript
// src/ingestion/ingest.ts
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, basename, dirname, extname } from "node:path";
import type Database from "better-sqlite3";
import { chunkFile } from "./chunker.js";
import {
  createCollection,
  addDocumentToCollection,
} from "./hierarchical-index.js";
import { searchByVector } from "../search/vector-search.js";
import { insertEmbedding } from "../search/vector-search.js";

type EmbedFn = (text: string) => Float32Array;

interface IngestFileResult {
  documentId: string;
  chunkCount: number;
  validationPassed: boolean;
}

interface IngestDirResult {
  collectionId: string;
  documentCount: number;
  totalChunks: number;
}

const INGESTABLE_EXTS = new Set([
  ".md", ".mdx", ".markdown", ".txt", ".rst",
  ".ts", ".tsx", ".js", ".jsx", ".py", ".go", ".rs",
  ".java", ".kt", ".cs", ".cpp", ".c", ".h",
  ".json", ".yaml", ".yml", ".toml",
]);

export function ingestFile(
  db: Database.Database,
  embed: EmbedFn,
  filePath: string,
  collectionId?: string,
): IngestFileResult {
  const content = readFileSync(filePath, "utf-8");
  const chunks = chunkFile(content, filePath, {
    maxTokens: 512,
    overlapTokens: 50,
  });

  // Create a default collection if none provided
  if (!collectionId) {
    collectionId = createCollection(db, embed, {
      name: basename(dirname(filePath)) || basename(filePath),
      sourcePath: dirname(filePath),
    });
  }

  const docId = addDocumentToCollection(db, embed, {
    collectionId,
    sourcePath: filePath,
    chunks: chunks.map((c) => ({
      content: c.content,
      headingPath: c.headingPath,
      name: c.name,
      kind: c.kind,
    })),
  });

  // Validation: check that a chunk can find itself
  let validationPassed = true;
  if (chunks.length > 0) {
    const testChunk = chunks[0];
    const testVec = embed(testChunk.content);
    const results = searchByVector(db, "cold", "knowledge", testVec, { topK: 1 });
    validationPassed = results.length > 0 && results[0].score > 0.5;
  }

  return {
    documentId: docId,
    chunkCount: chunks.length,
    validationPassed,
  };
}

export function ingestDirectory(
  db: Database.Database,
  embed: EmbedFn,
  dirPath: string,
  glob?: string,
): IngestDirResult {
  const collectionId = createCollection(db, embed, {
    name: basename(dirPath),
    sourcePath: dirPath,
  });

  let documentCount = 0;
  let totalChunks = 0;

  function walkDir(dir: string): void {
    const entries = readdirSync(dir);

    for (const entry of entries) {
      const fullPath = join(dir, entry);
      const stat = statSync(fullPath);

      if (stat.isDirectory()) {
        // Skip hidden dirs and node_modules
        if (!entry.startsWith(".") && entry !== "node_modules") {
          walkDir(fullPath);
        }
      } else if (stat.isFile()) {
        const ext = extname(entry).toLowerCase();
        if (INGESTABLE_EXTS.has(ext)) {
          const result = ingestFile(db, embed, fullPath, collectionId);
          documentCount++;
          totalChunks += result.chunkCount;
        }
      }
    }
  }

  walkDir(dirPath);

  return { collectionId, documentCount, totalChunks };
}
```

- [ ] **Step 4: Run tests**

```bash
npx vitest run src/ingestion/ingest.test.ts
```

Expected: All 3 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/ingestion/ingest.ts src/ingestion/ingest.test.ts
git commit -m "feat: add file and directory ingestion with validation"
```

---

### Task 6: Hot->Warm Compaction

**Files:**
- Create: `src/compaction/compactor.ts`
- Create: `src/compaction/compactor.test.ts`

- [ ] **Step 1: Write failing tests**

```typescript
// src/compaction/compactor.test.ts
import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { createTestDb } from "../../tests/helpers/db.js";
import { mockEmbedSemantic } from "../../tests/helpers/embedding.js";
import { storeMemory } from "../memory/store.js";
import { countEntries } from "../db/entries.js";
import { compactHotTier } from "./compactor.js";
import type { TotalRecallConfig } from "../types.js";
import type Database from "better-sqlite3";

const embed = mockEmbedSemantic;

const testConfig: TotalRecallConfig["compaction"] = {
  decay_half_life_hours: 168,
  warm_threshold: 0.3,
  promote_threshold: 0.7,
  warm_sweep_interval_days: 7,
};

describe("compactHotTier", () => {
  let db: Database.Database;

  beforeEach(() => { db = createTestDb(); });
  afterEach(() => { db.close(); });

  it("moves low-decay entries from hot to warm", () => {
    // Create entries with artificially old timestamps
    const oldTime = Date.now() - 30 * 24 * 60 * 60 * 1000; // 30 days ago
    const id = storeMemory(db, embed, { content: "old memory" });

    // Manually set old timestamps to trigger decay
    db.prepare("UPDATE hot_memories SET created_at = ?, last_accessed_at = ?, updated_at = ? WHERE id = ?")
      .run(oldTime, oldTime, oldTime, id);

    const result = compactHotTier(db, embed, testConfig, "test-session");

    expect(result.promoted).toBeGreaterThanOrEqual(0);
    expect(result.discarded).toBeGreaterThanOrEqual(0);
    expect(result.carryForward + result.promoted + result.discarded).toBeGreaterThan(0);
  });

  it("keeps fresh high-score entries in hot tier", () => {
    storeMemory(db, embed, { content: "just added this correction", type: "correction" });

    const before = countEntries(db, "hot", "memory");
    compactHotTier(db, embed, testConfig, "test-session");
    const after = countEntries(db, "hot", "memory");

    // Fresh correction should stay hot (high decay score)
    expect(after).toBe(before);
  });

  it("logs compaction events", () => {
    const oldTime = Date.now() - 60 * 24 * 60 * 60 * 1000;
    storeMemory(db, embed, { content: "very old entry" });
    db.prepare("UPDATE hot_memories SET created_at = ?, last_accessed_at = ?, updated_at = ? WHERE id IS NOT NULL")
      .run(oldTime, oldTime, oldTime);

    compactHotTier(db, embed, testConfig, "test-session");

    const logs = db.prepare("SELECT * FROM compaction_log").all();
    expect(logs.length).toBeGreaterThan(0);
  });
});
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
npx vitest run src/compaction/compactor.test.ts
```

- [ ] **Step 3: Implement compactor**

```typescript
// src/compaction/compactor.ts
import type Database from "better-sqlite3";
import { randomUUID } from "node:crypto";
import { listEntries } from "../db/entries.js";
import { calculateDecayScore } from "../memory/decay.js";
import { promoteEntry } from "../memory/promote-demote.js";
import { deleteEntry } from "../db/entries.js";
import { deleteEmbedding } from "../search/vector-search.js";
import type { TotalRecallConfig, Entry } from "../types.js";

type EmbedFn = (text: string) => Float32Array;

interface CompactionResult {
  carryForward: number;
  promoted: number;
  discarded: number;
}

export function compactHotTier(
  db: Database.Database,
  embed: EmbedFn,
  config: TotalRecallConfig["compaction"],
  sessionId: string,
  configSnapshotId: string = "default",
): CompactionResult {
  const hotEntries = listEntries(db, "hot", "memory");
  const now = Date.now();

  let carryForward = 0;
  let promoted = 0;
  let discarded = 0;

  for (const entry of hotEntries) {
    const entryType = (entry.metadata as Record<string, unknown>).entry_type as string ?? "decision";
    const score = calculateDecayScore(
      {
        last_accessed_at: entry.last_accessed_at,
        created_at: entry.created_at,
        access_count: entry.access_count,
        type: entryType,
      },
      config,
      now,
    );

    const decayScores = JSON.stringify({ [entry.id]: score });

    if (score > config.promote_threshold) {
      // Keep in hot tier (carry forward)
      carryForward++;
    } else if (score > config.warm_threshold) {
      // Promote to warm
      promoteEntry(db, embed, entry.id, "hot", "warm");
      promoted++;

      logCompaction(db, {
        sessionId,
        sourceTier: "hot",
        targetTier: "warm",
        sourceEntryIds: [entry.id],
        targetEntryId: entry.id,
        decayScores,
        reason: "decay_between_thresholds",
        configSnapshotId,
      });
    } else {
      // Discard
      deleteEmbedding(db, "hot", "memory", entry.id);
      deleteEntry(db, "hot", "memory", entry.id);
      discarded++;

      logCompaction(db, {
        sessionId,
        sourceTier: "hot",
        targetTier: null,
        sourceEntryIds: [entry.id],
        targetEntryId: null,
        decayScores,
        reason: "decay_below_threshold",
        configSnapshotId,
      });
    }
  }

  return { carryForward, promoted, discarded };
}

interface CompactionLogEntry {
  sessionId: string;
  sourceTier: string;
  targetTier: string | null;
  sourceEntryIds: string[];
  targetEntryId: string | null;
  semanticDrift?: number;
  factsPreserved?: number;
  factsInOriginal?: number;
  decayScores: string;
  reason: string;
  configSnapshotId: string;
}

function logCompaction(db: Database.Database, entry: CompactionLogEntry): void {
  db.prepare(
    `INSERT INTO compaction_log
     (id, timestamp, session_id, source_tier, target_tier, source_entry_ids,
      target_entry_id, semantic_drift, facts_preserved, facts_in_original,
      preservation_ratio, decay_scores, reason, config_snapshot_id)
     VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
  ).run(
    randomUUID(),
    Date.now(),
    entry.sessionId,
    entry.sourceTier,
    entry.targetTier,
    JSON.stringify(entry.sourceEntryIds),
    entry.targetEntryId,
    entry.semanticDrift ?? null,
    entry.factsPreserved ?? null,
    entry.factsInOriginal ?? null,
    entry.factsPreserved && entry.factsInOriginal
      ? entry.factsPreserved / entry.factsInOriginal
      : null,
    entry.decayScores,
    entry.reason,
    entry.configSnapshotId,
  );
}
```

- [ ] **Step 4: Run tests**

```bash
npx vitest run src/compaction/compactor.test.ts
```

Expected: All 3 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/compaction/compactor.ts src/compaction/compactor.test.ts
git commit -m "feat: add hot->warm compaction with decay scoring and event logging"
```

---

### Task 7: Warm->Cold Decay Sweep and Cold->Warm Promotion

**Files:**
- Create: `src/compaction/warm-sweep.ts`
- Create: `src/compaction/warm-sweep.test.ts`
- Create: `src/compaction/cold-promotion.ts`
- Create: `src/compaction/cold-promotion.test.ts`

- [ ] **Step 1: Write failing tests for warm sweep**

```typescript
// src/compaction/warm-sweep.test.ts
import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { createTestDb } from "../../tests/helpers/db.js";
import { mockEmbedSemantic } from "../../tests/helpers/embedding.js";
import { storeMemory } from "../memory/store.js";
import { countEntries } from "../db/entries.js";
import { sweepWarmTier } from "./warm-sweep.js";
import type Database from "better-sqlite3";

describe("sweepWarmTier", () => {
  let db: Database.Database;

  beforeEach(() => { db = createTestDb(); });
  afterEach(() => { db.close(); });

  it("moves old unaccessed warm entries to cold", () => {
    const id = storeMemory(db, mockEmbedSemantic, {
      content: "old warm entry",
      tier: "warm",
    });

    // Set timestamps to 60 days ago
    const oldTime = Date.now() - 60 * 24 * 60 * 60 * 1000;
    db.prepare("UPDATE warm_memories SET created_at = ?, last_accessed_at = ?, updated_at = ? WHERE id = ?")
      .run(oldTime, oldTime, oldTime, id);

    const before = countEntries(db, "warm", "memory");
    sweepWarmTier(db, mockEmbedSemantic, { coldDecayDays: 30 }, "test-session");
    const after = countEntries(db, "warm", "memory");

    expect(after).toBeLessThan(before);
    expect(countEntries(db, "cold", "memory")).toBeGreaterThan(0);
  });

  it("keeps recently accessed warm entries", () => {
    storeMemory(db, mockEmbedSemantic, {
      content: "fresh warm entry",
      tier: "warm",
    });

    const before = countEntries(db, "warm", "memory");
    sweepWarmTier(db, mockEmbedSemantic, { coldDecayDays: 30 }, "test-session");
    const after = countEntries(db, "warm", "memory");

    expect(after).toBe(before);
  });
});
```

- [ ] **Step 2: Write failing tests for cold promotion**

```typescript
// src/compaction/cold-promotion.test.ts
import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { createTestDb } from "../../tests/helpers/db.js";
import { mockEmbedSemantic } from "../../tests/helpers/embedding.js";
import { storeMemory } from "../memory/store.js";
import { updateEntry, countEntries } from "../db/entries.js";
import { checkAndPromoteCold } from "./cold-promotion.js";
import type Database from "better-sqlite3";

describe("checkAndPromoteCold", () => {
  let db: Database.Database;

  beforeEach(() => { db = createTestDb(); });
  afterEach(() => { db.close(); });

  it("promotes cold entries accessed 3+ times in 7 days", () => {
    const id = storeMemory(db, mockEmbedSemantic, {
      content: "frequently accessed",
      tier: "cold",
    });

    // Simulate 3 accesses
    for (let i = 0; i < 3; i++) {
      updateEntry(db, "cold", "memory", id, { touch: true });
    }

    checkAndPromoteCold(db, mockEmbedSemantic, { accessThreshold: 3, windowDays: 7 });

    expect(countEntries(db, "warm", "memory")).toBe(1);
    // Original should still exist in cold
    expect(countEntries(db, "cold", "memory")).toBe(1);
  });

  it("does not promote entries with fewer than threshold accesses", () => {
    storeMemory(db, mockEmbedSemantic, {
      content: "rarely accessed",
      tier: "cold",
    });

    updateEntry(db, "cold", "memory", storeMemory(db, mockEmbedSemantic, {
      content: "one access",
      tier: "cold",
    }), { touch: true });

    checkAndPromoteCold(db, mockEmbedSemantic, { accessThreshold: 3, windowDays: 7 });

    expect(countEntries(db, "warm", "memory")).toBe(0);
  });
});
```

- [ ] **Step 3: Implement warm sweep**

```typescript
// src/compaction/warm-sweep.ts
import type Database from "better-sqlite3";
import { listEntries } from "../db/entries.js";
import { promoteEntry } from "../memory/promote-demote.js";

type EmbedFn = (text: string) => Float32Array;

interface SweepConfig {
  coldDecayDays: number;
}

interface SweepResult {
  demoted: number;
  kept: number;
}

export function sweepWarmTier(
  db: Database.Database,
  embed: EmbedFn,
  config: SweepConfig,
  sessionId: string,
): SweepResult {
  const entries = listEntries(db, "warm", "memory");
  const now = Date.now();
  const thresholdMs = config.coldDecayDays * 24 * 60 * 60 * 1000;

  let demoted = 0;
  let kept = 0;

  for (const entry of entries) {
    const daysSinceAccess = now - entry.last_accessed_at;

    if (daysSinceAccess > thresholdMs && entry.access_count === 0) {
      // Demote to cold — full content preserved
      promoteEntry(db, embed, entry.id, "warm", "cold", "memory");
      demoted++;
    } else {
      kept++;
    }
  }

  return { demoted, kept };
}
```

- [ ] **Step 4: Implement cold promotion**

```typescript
// src/compaction/cold-promotion.ts
import type Database from "better-sqlite3";
import { listEntries, insertEntry } from "../db/entries.js";
import { insertEmbedding } from "../search/vector-search.js";

type EmbedFn = (text: string) => Float32Array;

interface PromotionConfig {
  accessThreshold: number;
  windowDays: number;
}

interface PromotionResult {
  promoted: number;
}

export function checkAndPromoteCold(
  db: Database.Database,
  embed: EmbedFn,
  config: PromotionConfig,
): PromotionResult {
  const now = Date.now();
  const windowMs = config.windowDays * 24 * 60 * 60 * 1000;

  // Check both cold memories and cold knowledge
  const coldMemories = listEntries(db, "cold", "memory");
  let promoted = 0;

  for (const entry of coldMemories) {
    const recentlyAccessed = (now - entry.last_accessed_at) < windowMs;

    if (recentlyAccessed && entry.access_count >= config.accessThreshold) {
      // Copy to warm (original stays in cold)
      const warmId = insertEntry(db, "warm", "memory", {
        content: entry.content,
        summary: entry.summary,
        source: entry.source,
        source_tool: entry.source_tool ?? undefined,
        project: entry.project,
        tags: entry.tags,
        metadata: {
          ...(entry.metadata as Record<string, unknown>),
          promoted_from: "cold",
          original_cold_id: entry.id,
        },
      });

      const vec = embed(entry.content);
      insertEmbedding(db, "warm", "memory", warmId, vec);
      promoted++;
    }
  }

  return { promoted };
}
```

- [ ] **Step 5: Run all compaction tests**

```bash
npx vitest run src/compaction/
```

Expected: All tests PASS.

- [ ] **Step 6: Commit**

```bash
git add src/compaction/warm-sweep.ts src/compaction/warm-sweep.test.ts src/compaction/cold-promotion.ts src/compaction/cold-promotion.test.ts
git commit -m "feat: add warm->cold decay sweep and cold->warm promotion"
```

---

### Task 8: Retrieval Event Logger

**Files:**
- Create: `src/eval/event-logger.ts`
- Create: `src/eval/event-logger.test.ts`

- [ ] **Step 1: Write failing tests**

```typescript
// src/eval/event-logger.test.ts
import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { createTestDb } from "../../tests/helpers/db.js";
import { logRetrievalEvent, getRetrievalEvents } from "./event-logger.js";
import type Database from "better-sqlite3";

describe("event-logger", () => {
  let db: Database.Database;

  beforeEach(() => { db = createTestDb(); });
  afterEach(() => { db.close(); });

  it("logs a retrieval event and retrieves it", () => {
    logRetrievalEvent(db, {
      sessionId: "sess-1",
      queryText: "auth middleware",
      querySource: "auto",
      results: [
        { entry_id: "e1", tier: "warm", content_type: "memory", score: 0.89, rank: 0 },
      ],
      tiersSearched: ["warm"],
      configSnapshotId: "default",
      latencyMs: 5,
    });

    const events = getRetrievalEvents(db, { sessionId: "sess-1" });
    expect(events).toHaveLength(1);
    expect(events[0].query_text).toBe("auth middleware");
    expect(events[0].top_score).toBe(0.89);
    expect(events[0].top_tier).toBe("warm");
  });

  it("updates outcome after the fact", () => {
    const id = logRetrievalEvent(db, {
      sessionId: "sess-1",
      queryText: "test query",
      querySource: "explicit",
      results: [],
      tiersSearched: ["hot"],
      configSnapshotId: "default",
      latencyMs: 3,
    });

    const { updateOutcome } = await import("./event-logger.js");
    updateOutcome(db, id, { used: true, signal: "positive" });

    const events = getRetrievalEvents(db, { sessionId: "sess-1" });
    expect(events[0].outcome_used).toBe(1);
    expect(events[0].outcome_signal).toBe("positive");
  });
});
```

- [ ] **Step 2: Implement event logger**

```typescript
// src/eval/event-logger.ts
import type Database from "better-sqlite3";
import { randomUUID } from "node:crypto";
import type { QuerySource, RetrievalEventRow } from "../types.js";

interface LogEventOpts {
  sessionId: string;
  queryText: string;
  querySource: QuerySource;
  queryEmbedding?: Buffer;
  results: Array<{
    entry_id: string;
    tier: string;
    content_type: string;
    score: number;
    rank: number;
  }>;
  tiersSearched: string[];
  configSnapshotId: string;
  latencyMs: number;
  totalCandidatesScanned?: number;
}

export function logRetrievalEvent(db: Database.Database, opts: LogEventOpts): string {
  const id = randomUUID();
  const topResult = opts.results.length > 0 ? opts.results[0] : null;

  db.prepare(
    `INSERT INTO retrieval_events
     (id, timestamp, session_id, query_text, query_source, query_embedding,
      results, result_count, top_score, top_tier, top_content_type,
      config_snapshot_id, latency_ms, tiers_searched, total_candidates_scanned)
     VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
  ).run(
    id,
    Date.now(),
    opts.sessionId,
    opts.queryText,
    opts.querySource,
    opts.queryEmbedding ?? null,
    JSON.stringify(opts.results),
    opts.results.length,
    topResult?.score ?? null,
    topResult?.tier ?? null,
    topResult?.content_type ?? null,
    opts.configSnapshotId,
    opts.latencyMs,
    JSON.stringify(opts.tiersSearched),
    opts.totalCandidatesScanned ?? null,
  );

  return id;
}

export function updateOutcome(
  db: Database.Database,
  eventId: string,
  outcome: { used: boolean; signal?: string },
): void {
  db.prepare(
    "UPDATE retrieval_events SET outcome_used = ?, outcome_signal = ? WHERE id = ?",
  ).run(outcome.used ? 1 : 0, outcome.signal ?? "neutral", eventId);
}

interface GetEventsOpts {
  sessionId?: string;
  configSnapshotId?: string;
  days?: number;
  limit?: number;
}

export function getRetrievalEvents(
  db: Database.Database,
  opts: GetEventsOpts,
): RetrievalEventRow[] {
  const conditions: string[] = [];
  const params: unknown[] = [];

  if (opts.sessionId) {
    conditions.push("session_id = ?");
    params.push(opts.sessionId);
  }
  if (opts.configSnapshotId) {
    conditions.push("config_snapshot_id = ?");
    params.push(opts.configSnapshotId);
  }
  if (opts.days) {
    conditions.push("timestamp > ?");
    params.push(Date.now() - opts.days * 24 * 60 * 60 * 1000);
  }

  const where = conditions.length > 0 ? `WHERE ${conditions.join(" AND ")}` : "";
  const limit = opts.limit ? `LIMIT ${opts.limit}` : "LIMIT 1000";

  return db
    .prepare(`SELECT * FROM retrieval_events ${where} ORDER BY timestamp DESC ${limit}`)
    .all(...params) as RetrievalEventRow[];
}
```

- [ ] **Step 3: Run tests**

```bash
npx vitest run src/eval/event-logger.test.ts
```

Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/eval/event-logger.ts src/eval/event-logger.test.ts
git commit -m "feat: add retrieval event logger with outcome tracking"
```

---

### Task 9: Metrics Computation

**Files:**
- Create: `src/eval/metrics.ts`
- Create: `src/eval/metrics.test.ts`

- [ ] **Step 1: Write failing tests**

```typescript
// src/eval/metrics.test.ts
import { describe, it, expect } from "vitest";
import { computeMetrics } from "./metrics.js";
import type { RetrievalEventRow } from "../types.js";

function makeEvent(overrides: Partial<RetrievalEventRow> = {}): RetrievalEventRow {
  return {
    id: "test",
    timestamp: Date.now(),
    session_id: "s1",
    query_text: "test",
    query_source: "auto",
    query_embedding: null,
    results: "[]",
    result_count: 1,
    top_score: 0.8,
    top_tier: "warm",
    top_content_type: "memory",
    outcome_used: 1,
    outcome_signal: "positive",
    config_snapshot_id: "default",
    latency_ms: 5,
    tiers_searched: '["warm"]',
    total_candidates_scanned: 10,
    ...overrides,
  };
}

describe("computeMetrics", () => {
  it("computes precision, hit rate, miss rate, MRR", () => {
    const events = [
      makeEvent({ outcome_used: 1, top_score: 0.9 }),
      makeEvent({ outcome_used: 1, top_score: 0.85 }),
      makeEvent({ outcome_used: 0, top_score: 0.7 }),
      makeEvent({ outcome_used: null, top_score: 0.3 }), // miss
    ];

    const metrics = computeMetrics(events, 0.65);

    expect(metrics.precision).toBeCloseTo(0.67, 1); // 2/3 (excluding null outcome)
    expect(metrics.hitRate).toBeCloseTo(0.67, 1);
    expect(metrics.missRate).toBe(0.25); // 1 out of 4 below threshold
    expect(metrics.avgLatencyMs).toBe(5);
  });

  it("returns zeroes for empty events", () => {
    const metrics = computeMetrics([], 0.65);
    expect(metrics.precision).toBe(0);
    expect(metrics.hitRate).toBe(0);
    expect(metrics.missRate).toBe(0);
  });
});
```

- [ ] **Step 2: Implement metrics**

```typescript
// src/eval/metrics.ts
import type { RetrievalEventRow } from "../types.js";

export interface Metrics {
  precision: number;
  hitRate: number;
  missRate: number;
  mrr: number;
  avgLatencyMs: number;
  totalEvents: number;
  byTier: Record<string, { precision: number; hitRate: number; avgScore: number; count: number }>;
  byContentType: Record<string, { precision: number; hitRate: number; count: number }>;
}

export function computeMetrics(events: RetrievalEventRow[], similarityThreshold: number): Metrics {
  if (events.length === 0) {
    return {
      precision: 0, hitRate: 0, missRate: 0, mrr: 0,
      avgLatencyMs: 0, totalEvents: 0, byTier: {}, byContentType: {},
    };
  }

  let usedCount = 0;
  let injectedCount = 0;
  let hitCount = 0;
  let missCount = 0;
  let mrrSum = 0;
  let latencySum = 0;

  const tierStats: Record<string, { used: number; total: number; scoreSum: number }> = {};
  const typeStats: Record<string, { used: number; total: number }> = {};

  for (const event of events) {
    latencySum += event.latency_ms ?? 0;

    // Miss detection
    if (event.top_score !== null && event.top_score < similarityThreshold) {
      missCount++;
      continue;
    }

    // Only count events with outcome data for precision/hit rate
    if (event.outcome_used !== null) {
      injectedCount++;
      if (event.outcome_used === 1) {
        usedCount++;
        hitCount++;
        mrrSum += 1; // rank 1 since we track top result
      }
    }

    // Per-tier stats
    if (event.top_tier) {
      if (!tierStats[event.top_tier]) tierStats[event.top_tier] = { used: 0, total: 0, scoreSum: 0 };
      tierStats[event.top_tier].total++;
      tierStats[event.top_tier].scoreSum += event.top_score ?? 0;
      if (event.outcome_used === 1) tierStats[event.top_tier].used++;
    }

    // Per-type stats
    if (event.top_content_type) {
      if (!typeStats[event.top_content_type]) typeStats[event.top_content_type] = { used: 0, total: 0 };
      typeStats[event.top_content_type].total++;
      if (event.outcome_used === 1) typeStats[event.top_content_type].used++;
    }
  }

  const precision = injectedCount > 0 ? usedCount / injectedCount : 0;
  const hitRate = injectedCount > 0 ? hitCount / injectedCount : 0;
  const missRate = events.length > 0 ? missCount / events.length : 0;
  const mrr = injectedCount > 0 ? mrrSum / injectedCount : 0;

  const byTier: Metrics["byTier"] = {};
  for (const [tier, stats] of Object.entries(tierStats)) {
    byTier[tier] = {
      precision: stats.total > 0 ? stats.used / stats.total : 0,
      hitRate: stats.total > 0 ? stats.used / stats.total : 0,
      avgScore: stats.total > 0 ? stats.scoreSum / stats.total : 0,
      count: stats.total,
    };
  }

  const byContentType: Metrics["byContentType"] = {};
  for (const [type, stats] of Object.entries(typeStats)) {
    byContentType[type] = {
      precision: stats.total > 0 ? stats.used / stats.total : 0,
      hitRate: stats.total > 0 ? stats.used / stats.total : 0,
      count: stats.total,
    };
  }

  return {
    precision,
    hitRate,
    missRate,
    mrr,
    avgLatencyMs: events.length > 0 ? latencySum / events.length : 0,
    totalEvents: events.length,
    byTier,
    byContentType,
  };
}
```

- [ ] **Step 3: Run tests**

```bash
npx vitest run src/eval/metrics.test.ts
```

Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/eval/metrics.ts src/eval/metrics.test.ts
git commit -m "feat: add retrieval metrics computation (precision, hit rate, MRR)"
```

---

### Task 10: Synthetic Benchmark Runner

**Files:**
- Create: `eval/corpus/memories.jsonl`
- Create: `eval/benchmarks/retrieval.jsonl`
- Create: `src/eval/benchmark-runner.ts`
- Create: `src/eval/benchmark-runner.test.ts`

- [ ] **Step 1: Create seed corpus (subset — 20 entries for smoke testing)**

`eval/corpus/memories.jsonl`:
```jsonl
{"content":"User prefers pnpm over npm for package management","type":"correction","tags":["tooling","packages"]}
{"content":"Always use integration tests with real databases, never mock the data layer","type":"preference","tags":["testing","databases"]}
{"content":"Auth system uses Passport.js with JWT tokens","type":"decision","tags":["auth","architecture"]}
{"content":"Deploy to staging first via GitHub Actions, production requires manual approval","type":"preference","tags":["deployment","ci"]}
{"content":"Use vitest as the test runner, not jest","type":"correction","tags":["testing","tooling"]}
{"content":"Project uses PostgreSQL with Drizzle ORM","type":"decision","tags":["database","orm"]}
{"content":"Error responses should use RFC 7807 Problem Details format","type":"preference","tags":["api","errors"]}
{"content":"All API endpoints require authentication except /health and /docs","type":"decision","tags":["api","auth"]}
{"content":"Use zod for runtime schema validation at API boundaries","type":"preference","tags":["validation","api"]}
{"content":"Redis is used for session storage and rate limiting only","type":"decision","tags":["redis","architecture"]}
{"content":"Feature branches should be named feat/TICKET-description","type":"preference","tags":["git","workflow"]}
{"content":"Never commit .env files, use .env.example as template","type":"correction","tags":["security","git"]}
{"content":"The frontend uses React with TanStack Router","type":"decision","tags":["frontend","react"]}
{"content":"CSS uses Tailwind with the project's custom design tokens","type":"decision","tags":["frontend","css"]}
{"content":"All database migrations go through Drizzle Kit","type":"preference","tags":["database","migrations"]}
{"content":"Logging uses structured JSON via pino","type":"decision","tags":["logging","observability"]}
{"content":"User prefers terse responses without trailing summaries","type":"preference","tags":["communication"]}
{"content":"WebSocket connections use socket.io with Redis adapter for scaling","type":"decision","tags":["websockets","architecture"]}
{"content":"File uploads go to S3 with presigned URLs, never through the API server","type":"decision","tags":["files","architecture"]}
{"content":"Cron jobs are managed by BullMQ with Redis backend","type":"decision","tags":["jobs","architecture"]}
```

- [ ] **Step 2: Create benchmark queries**

`eval/benchmarks/retrieval.jsonl`:
```jsonl
{"query":"which package manager should I use","expected_content_contains":"pnpm","expected_tier":"warm"}
{"query":"testing strategy database mocking","expected_content_contains":"integration tests","expected_tier":"warm"}
{"query":"how does authentication work","expected_content_contains":"Passport","expected_tier":"warm"}
{"query":"deployment pipeline process","expected_content_contains":"staging","expected_tier":"warm"}
{"query":"test runner framework","expected_content_contains":"vitest","expected_tier":"warm"}
{"query":"database ORM setup","expected_content_contains":"Drizzle","expected_tier":"warm"}
{"query":"API error format","expected_content_contains":"RFC 7807","expected_tier":"warm"}
{"query":"which endpoints need auth","expected_content_contains":"/health","expected_tier":"warm"}
{"query":"input validation library","expected_content_contains":"zod","expected_tier":"warm"}
{"query":"what is redis used for","expected_content_contains":"session","expected_tier":"warm"}
{"query":"git branch naming convention","expected_content_contains":"feat/","expected_tier":"warm"}
{"query":"environment variable security","expected_content_contains":".env","expected_tier":"warm"}
{"query":"frontend framework","expected_content_contains":"React","expected_tier":"warm"}
{"query":"styling approach","expected_content_contains":"Tailwind","expected_tier":"warm"}
{"query":"how to run migrations","expected_content_contains":"Drizzle Kit","expected_tier":"warm"}
{"query":"logging format","expected_content_contains":"pino","expected_tier":"warm"}
{"query":"file upload architecture","expected_content_contains":"S3","expected_tier":"warm"}
{"query":"background job queue","expected_content_contains":"BullMQ","expected_tier":"warm"}
{"query":"realtime communication","expected_content_contains":"socket.io","expected_tier":"warm"}
{"query":"user communication preferences","expected_content_contains":"terse","expected_tier":"warm"}
```

- [ ] **Step 3: Write failing test for benchmark runner**

```typescript
// src/eval/benchmark-runner.test.ts
import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { createTestDb } from "../../tests/helpers/db.js";
import { mockEmbedSemantic } from "../../tests/helpers/embedding.js";
import { runBenchmark } from "./benchmark-runner.js";
import type Database from "better-sqlite3";

describe("benchmark-runner", () => {
  let db: Database.Database;

  beforeEach(() => { db = createTestDb(); });
  afterEach(() => { db.close(); });

  it("loads corpus, seeds DB, runs queries, and produces report", () => {
    const result = runBenchmark(db, mockEmbedSemantic, {
      corpusPath: "eval/corpus/memories.jsonl",
      benchmarkPath: "eval/benchmarks/retrieval.jsonl",
    });

    expect(result.totalQueries).toBe(20);
    expect(result.fuzzyMatchRate).toBeGreaterThanOrEqual(0);
    expect(result.fuzzyMatchRate).toBeLessThanOrEqual(1);
    expect(result.exactMatchRate).toBeGreaterThanOrEqual(0);
    expect(result.avgLatencyMs).toBeGreaterThanOrEqual(0);
  });
});
```

- [ ] **Step 4: Implement benchmark runner**

```typescript
// src/eval/benchmark-runner.ts
import { readFileSync } from "node:fs";
import type Database from "better-sqlite3";
import { storeMemory } from "../memory/store.js";
import { searchMemory } from "../memory/search.js";

type EmbedFn = (text: string) => Float32Array;

interface BenchmarkOpts {
  corpusPath: string;
  benchmarkPath: string;
}

interface BenchmarkResult {
  totalQueries: number;
  exactMatchRate: number;
  fuzzyMatchRate: number;
  tierRoutingRate: number;
  avgLatencyMs: number;
  details: Array<{
    query: string;
    expectedContains: string;
    topResult: string | null;
    topScore: number;
    matched: boolean;
    fuzzyMatched: boolean;
  }>;
}

interface CorpusEntry {
  content: string;
  type?: string;
  tags?: string[];
}

interface BenchmarkQuery {
  query: string;
  expected_content_contains: string;
  expected_tier: string;
}

export function runBenchmark(
  db: Database.Database,
  embed: EmbedFn,
  opts: BenchmarkOpts,
): BenchmarkResult {
  // Load and seed corpus
  const corpusLines = readFileSync(opts.corpusPath, "utf-8").trim().split("\n");
  for (const line of corpusLines) {
    const entry = JSON.parse(line) as CorpusEntry;
    storeMemory(db, embed, {
      content: entry.content,
      type: entry.type,
      tier: "warm", // Seed into warm for retrieval testing
      tags: entry.tags,
    });
  }

  // Load benchmark queries
  const benchLines = readFileSync(opts.benchmarkPath, "utf-8").trim().split("\n");
  const queries = benchLines.map((l) => JSON.parse(l) as BenchmarkQuery);

  // Run queries
  let exactMatches = 0;
  let fuzzyMatches = 0;
  let tierMatches = 0;
  let totalLatency = 0;
  const details: BenchmarkResult["details"] = [];

  for (const q of queries) {
    const start = performance.now();
    const results = searchMemory(db, embed, {
      query: q.query,
      tiers: ["warm"],
      topK: 3,
    });
    const latency = performance.now() - start;
    totalLatency += latency;

    const topResult = results[0] ?? null;
    const topContent = topResult?.entry.content ?? "";
    const topScore = topResult?.score ?? 0;

    const exactMatch = topContent.includes(q.expected_content_contains);
    const fuzzyMatch = results.some((r) =>
      r.entry.content.includes(q.expected_content_contains),
    );
    const tierMatch = topResult?.tier === q.expected_tier;

    if (exactMatch) exactMatches++;
    if (fuzzyMatch) fuzzyMatches++;
    if (tierMatch) tierMatches++;

    details.push({
      query: q.query,
      expectedContains: q.expected_content_contains,
      topResult: topContent || null,
      topScore,
      matched: exactMatch,
      fuzzyMatched: fuzzyMatch,
    });
  }

  return {
    totalQueries: queries.length,
    exactMatchRate: queries.length > 0 ? exactMatches / queries.length : 0,
    fuzzyMatchRate: queries.length > 0 ? fuzzyMatches / queries.length : 0,
    tierRoutingRate: queries.length > 0 ? tierMatches / queries.length : 0,
    avgLatencyMs: queries.length > 0 ? totalLatency / queries.length : 0,
    details,
  };
}
```

- [ ] **Step 5: Run tests**

```bash
npx vitest run src/eval/benchmark-runner.test.ts
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add eval/ src/eval/benchmark-runner.ts src/eval/benchmark-runner.test.ts
git commit -m "feat: add synthetic benchmark runner with seed corpus and 20 query pairs"
```

---

### Task 11: Claude Code Importer

**Files:**
- Create: `src/importers/importer.ts`
- Create: `src/importers/claude-code.ts`
- Create: `src/importers/claude-code.test.ts`

- [ ] **Step 1: Write failing tests**

```typescript
// src/importers/claude-code.test.ts
import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { createTestDb } from "../../tests/helpers/db.js";
import { mockEmbedSemantic } from "../../tests/helpers/embedding.js";
import { ClaudeCodeImporter } from "./claude-code.js";
import { countEntries } from "../db/entries.js";
import { writeFileSync, mkdirSync, rmSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import type Database from "better-sqlite3";

describe("ClaudeCodeImporter", () => {
  let db: Database.Database;
  let tmpDir: string;

  beforeEach(() => {
    db = createTestDb();
    tmpDir = join(tmpdir(), `tr-cc-test-${Date.now()}`);
    mkdirSync(join(tmpDir, "projects", "test-project", "memory"), { recursive: true });
  });

  afterEach(() => {
    db.close();
    rmSync(tmpDir, { recursive: true, force: true });
  });

  it("detects Claude Code installation", () => {
    const importer = new ClaudeCodeImporter(tmpDir);
    expect(importer.detect()).toBe(true);
  });

  it("does not detect when directory missing", () => {
    const importer = new ClaudeCodeImporter("/tmp/nonexistent-cc-12345");
    expect(importer.detect()).toBe(false);
  });

  it("imports memory files with YAML frontmatter", () => {
    const memoryDir = join(tmpDir, "projects", "test-project", "memory");
    writeFileSync(
      join(memoryDir, "user_prefs.md"),
      `---
name: user-preferences
description: User coding preferences
type: user
---

User prefers TypeScript and pnpm.`,
    );

    writeFileSync(
      join(memoryDir, "feedback_testing.md"),
      `---
name: testing-feedback
description: Testing approach feedback
type: feedback
---

Use integration tests, avoid mocks for data layer.`,
    );

    writeFileSync(join(memoryDir, "MEMORY.md"), "- [User Prefs](user_prefs.md)\n- [Testing](feedback_testing.md)");

    const importer = new ClaudeCodeImporter(tmpDir);
    const result = importer.importMemories(db, mockEmbedSemantic, "test-project");

    expect(result.imported).toBe(2); // MEMORY.md is index, not imported
    expect(countEntries(db, "warm", "memory")).toBe(2);
  });

  it("imports CLAUDE.md as pinned knowledge", () => {
    writeFileSync(join(tmpDir, "CLAUDE.md"), "# Global Instructions\n\nAlways use strict mode.");

    const importer = new ClaudeCodeImporter(tmpDir);
    const result = importer.importKnowledge(db, mockEmbedSemantic);

    expect(result.imported).toBeGreaterThan(0);
    expect(countEntries(db, "warm", "knowledge")).toBeGreaterThan(0);
  });

  it("deduplicates on re-import", () => {
    const memoryDir = join(tmpDir, "projects", "test-project", "memory");
    writeFileSync(
      join(memoryDir, "pref.md"),
      "---\nname: pref\ndescription: test\ntype: user\n---\nContent here",
    );

    const importer = new ClaudeCodeImporter(tmpDir);
    importer.importMemories(db, mockEmbedSemantic, "test-project");
    importer.importMemories(db, mockEmbedSemantic, "test-project");

    expect(countEntries(db, "warm", "memory")).toBe(1); // Not duplicated
  });
});
```

- [ ] **Step 2: Implement HostImporter interface**

```typescript
// src/importers/importer.ts
import type Database from "better-sqlite3";

type EmbedFn = (text: string) => Float32Array;

export interface ImportResult {
  imported: number;
  skipped: number;
  errors: string[];
}

export interface HostImporter {
  name: string;
  detect(): boolean;
  scan(): { memoryFiles: number; knowledgeFiles: number; sessionFiles: number };
  importMemories(db: Database.Database, embed: EmbedFn, project?: string): ImportResult;
  importKnowledge(db: Database.Database, embed: EmbedFn): ImportResult;
}
```

- [ ] **Step 3: Implement Claude Code importer**

```typescript
// src/importers/claude-code.ts
import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import { join, basename } from "node:path";
import { createHash } from "node:crypto";
import type Database from "better-sqlite3";
import { insertEntry } from "../db/entries.js";
import { insertEmbedding } from "../search/vector-search.js";
import type { HostImporter, ImportResult } from "./importer.js";

type EmbedFn = (text: string) => Float32Array;

interface MemoryFrontmatter {
  name: string;
  description: string;
  type: "user" | "feedback" | "project" | "reference";
}

export class ClaudeCodeImporter implements HostImporter {
  name = "claude-code";
  private basePath: string;

  constructor(basePath?: string) {
    this.basePath = basePath ?? join(process.env.HOME ?? "~", ".claude");
  }

  detect(): boolean {
    return existsSync(this.basePath) && existsSync(join(this.basePath, "projects"));
  }

  scan(): { memoryFiles: number; knowledgeFiles: number; sessionFiles: number } {
    if (!this.detect()) return { memoryFiles: 0, knowledgeFiles: 0, sessionFiles: 0 };

    let memoryFiles = 0;
    let knowledgeFiles = 0;
    let sessionFiles = 0;

    const projectsDir = join(this.basePath, "projects");
    if (existsSync(projectsDir)) {
      for (const project of readdirSync(projectsDir)) {
        const memDir = join(projectsDir, project, "memory");
        if (existsSync(memDir)) {
          memoryFiles += readdirSync(memDir).filter(
            (f) => f.endsWith(".md") && f !== "MEMORY.md",
          ).length;
        }
        // Count session files
        const projectDir = join(projectsDir, project);
        sessionFiles += readdirSync(projectDir).filter((f) => f.endsWith(".jsonl")).length;
      }
    }

    // Check for CLAUDE.md files
    if (existsSync(join(this.basePath, "..", "CLAUDE.md"))) knowledgeFiles++;

    return { memoryFiles, knowledgeFiles, sessionFiles };
  }

  importMemories(db: Database.Database, embed: EmbedFn, project?: string): ImportResult {
    const result: ImportResult = { imported: 0, skipped: 0, errors: [] };
    const projectsDir = join(this.basePath, "projects");
    if (!existsSync(projectsDir)) return result;

    const projectDirs = project
      ? [project]
      : readdirSync(projectsDir).filter((d) =>
          statSync(join(projectsDir, d)).isDirectory(),
        );

    for (const projDir of projectDirs) {
      const memDir = join(projectsDir, projDir, "memory");
      if (!existsSync(memDir)) continue;

      const files = readdirSync(memDir).filter(
        (f) => f.endsWith(".md") && f !== "MEMORY.md",
      );

      for (const file of files) {
        try {
          const filePath = join(memDir, file);
          const raw = readFileSync(filePath, "utf-8");
          const { frontmatter, content } = parseFrontmatter(raw);

          if (!content.trim()) {
            result.skipped++;
            continue;
          }

          // Dedup check
          const contentHash = createHash("sha256").update(content).digest("hex");
          const existing = db
            .prepare("SELECT id FROM import_log WHERE content_hash = ? AND source_tool = ?")
            .get(contentHash, "claude-code");

          if (existing) {
            result.skipped++;
            continue;
          }

          // Map CC type to tier
          const tier = frontmatter?.type === "reference" ? "cold" as const : "warm" as const;
          const contentType = frontmatter?.type === "reference" ? "knowledge" as const : "memory" as const;

          const id = insertEntry(db, tier, contentType, {
            content,
            source: `claude-code/memory/${file}`,
            source_tool: "claude-code",
            project: projDir !== "global" ? projDir : null,
            tags: frontmatter?.type ? [frontmatter.type] : [],
            metadata: {
              entry_type: "imported",
              cc_name: frontmatter?.name,
              cc_description: frontmatter?.description,
              cc_type: frontmatter?.type,
            },
          });

          const vec = embed(content);
          insertEmbedding(db, tier, contentType, id, vec);

          // Log import
          db.prepare(
            `INSERT INTO import_log (id, timestamp, source_tool, source_path, content_hash, target_entry_id, target_tier, target_type)
             VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
          ).run(
            createHash("md5").update(`${filePath}-${Date.now()}`).digest("hex"),
            Date.now(),
            "claude-code",
            filePath,
            contentHash,
            id,
            tier,
            contentType,
          );

          result.imported++;
        } catch (err) {
          result.errors.push(`${file}: ${err instanceof Error ? err.message : String(err)}`);
        }
      }
    }

    return result;
  }

  importKnowledge(db: Database.Database, embed: EmbedFn): ImportResult {
    const result: ImportResult = { imported: 0, skipped: 0, errors: [] };

    // Import global CLAUDE.md
    const globalClaudeMd = join(this.basePath, "..", "CLAUDE.md");
    if (existsSync(globalClaudeMd)) {
      const imported = this.importClaudeMd(db, embed, globalClaudeMd, null);
      if (imported) result.imported++;
      else result.skipped++;
    }

    return result;
  }

  private importClaudeMd(
    db: Database.Database,
    embed: EmbedFn,
    filePath: string,
    project: string | null,
  ): boolean {
    const content = readFileSync(filePath, "utf-8");
    if (!content.trim()) return false;

    const contentHash = createHash("sha256").update(content).digest("hex");
    const existing = db
      .prepare("SELECT id FROM import_log WHERE content_hash = ? AND source_tool = ?")
      .get(contentHash, "claude-code");

    if (existing) return false;

    const id = insertEntry(db, "warm", "knowledge", {
      content,
      source: `claude-code/${basename(filePath)}`,
      source_tool: "claude-code",
      project,
      tags: ["claude-md", "pinned"],
      metadata: { entry_type: "imported", pinned: true },
    });

    const vec = embed(content);
    insertEmbedding(db, "warm", "knowledge", id, vec);

    db.prepare(
      `INSERT INTO import_log (id, timestamp, source_tool, source_path, content_hash, target_entry_id, target_tier, target_type)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
    ).run(
      createHash("md5").update(`${filePath}-${Date.now()}`).digest("hex"),
      Date.now(),
      "claude-code",
      filePath,
      contentHash,
      id,
      "warm",
      "knowledge",
    );

    return true;
  }
}

function parseFrontmatter(raw: string): { frontmatter: MemoryFrontmatter | null; content: string } {
  const match = raw.match(/^---\n([\s\S]*?)\n---\n([\s\S]*)$/);
  if (!match) return { frontmatter: null, content: raw };

  const yamlBlock = match[1];
  const content = match[2].trim();

  // Simple YAML parsing for known fields
  const frontmatter: Partial<MemoryFrontmatter> = {};
  for (const line of yamlBlock.split("\n")) {
    const [key, ...valueParts] = line.split(":");
    const value = valueParts.join(":").trim();
    if (key.trim() === "name") frontmatter.name = value;
    if (key.trim() === "description") frontmatter.description = value;
    if (key.trim() === "type") frontmatter.type = value as MemoryFrontmatter["type"];
  }

  return { frontmatter: frontmatter as MemoryFrontmatter, content };
}
```

- [ ] **Step 4: Run tests**

```bash
npx vitest run src/importers/claude-code.test.ts
```

Expected: All 4 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/importers/
git commit -m "feat: add Claude Code importer with frontmatter parsing and dedup"
```

---

### Task 12: Copilot CLI Importer

**Files:**
- Create: `src/importers/copilot-cli.ts`
- Create: `src/importers/copilot-cli.test.ts`

- [ ] **Step 1: Write failing tests**

```typescript
// src/importers/copilot-cli.test.ts
import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { createTestDb } from "../../tests/helpers/db.js";
import { mockEmbedSemantic } from "../../tests/helpers/embedding.js";
import { CopilotCliImporter } from "./copilot-cli.js";
import { countEntries } from "../db/entries.js";
import { writeFileSync, mkdirSync, rmSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import type Database from "better-sqlite3";

describe("CopilotCliImporter", () => {
  let db: Database.Database;
  let tmpDir: string;

  beforeEach(() => {
    db = createTestDb();
    tmpDir = join(tmpdir(), `tr-copilot-test-${Date.now()}`);
    mkdirSync(join(tmpDir, "session-state", "session-1"), { recursive: true });
  });

  afterEach(() => {
    db.close();
    rmSync(tmpDir, { recursive: true, force: true });
  });

  it("detects Copilot CLI installation", () => {
    const importer = new CopilotCliImporter(tmpDir);
    expect(importer.detect()).toBe(true);
  });

  it("imports plan.md files as cold knowledge", () => {
    writeFileSync(
      join(tmpDir, "session-state", "session-1", "plan.md"),
      "# Implementation Plan\n\n## Step 1\n\nSet up auth middleware.",
    );

    const importer = new CopilotCliImporter(tmpDir);
    const result = importer.importKnowledge(db, mockEmbedSemantic);

    expect(result.imported).toBe(1);
    expect(countEntries(db, "cold", "knowledge")).toBeGreaterThan(0);
  });
});
```

- [ ] **Step 2: Implement Copilot CLI importer**

```typescript
// src/importers/copilot-cli.ts
import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import { join } from "node:path";
import { createHash } from "node:crypto";
import type Database from "better-sqlite3";
import { insertEntry } from "../db/entries.js";
import { insertEmbedding } from "../search/vector-search.js";
import type { HostImporter, ImportResult } from "./importer.js";

type EmbedFn = (text: string) => Float32Array;

export class CopilotCliImporter implements HostImporter {
  name = "copilot-cli";
  private basePath: string;

  constructor(basePath?: string) {
    this.basePath = basePath ?? join(process.env.HOME ?? "~", ".copilot");
  }

  detect(): boolean {
    return existsSync(this.basePath) && existsSync(join(this.basePath, "session-state"));
  }

  scan(): { memoryFiles: number; knowledgeFiles: number; sessionFiles: number } {
    if (!this.detect()) return { memoryFiles: 0, knowledgeFiles: 0, sessionFiles: 0 };

    let knowledgeFiles = 0;
    let sessionFiles = 0;

    const sessionsDir = join(this.basePath, "session-state");
    for (const session of readdirSync(sessionsDir)) {
      const sessionDir = join(sessionsDir, session);
      if (!statSync(sessionDir).isDirectory()) continue;

      const files = readdirSync(sessionDir);
      if (files.includes("plan.md")) knowledgeFiles++;
      if (files.includes("events.jsonl")) sessionFiles++;
    }

    return { memoryFiles: 0, knowledgeFiles, sessionFiles };
  }

  importMemories(_db: Database.Database, _embed: EmbedFn): ImportResult {
    // Copilot CLI has no persistent memory — return empty
    return { imported: 0, skipped: 0, errors: [] };
  }

  importKnowledge(db: Database.Database, embed: EmbedFn): ImportResult {
    const result: ImportResult = { imported: 0, skipped: 0, errors: [] };
    const sessionsDir = join(this.basePath, "session-state");
    if (!existsSync(sessionsDir)) return result;

    for (const session of readdirSync(sessionsDir)) {
      const sessionDir = join(sessionsDir, session);
      if (!statSync(sessionDir).isDirectory()) continue;

      const planPath = join(sessionDir, "plan.md");
      if (existsSync(planPath)) {
        try {
          const content = readFileSync(planPath, "utf-8");
          if (!content.trim()) { result.skipped++; continue; }

          const contentHash = createHash("sha256").update(content).digest("hex");
          const existing = db
            .prepare("SELECT id FROM import_log WHERE content_hash = ? AND source_tool = ?")
            .get(contentHash, "copilot-cli");

          if (existing) { result.skipped++; continue; }

          const id = insertEntry(db, "cold", "knowledge", {
            content,
            source: `copilot-cli/session-state/${session}/plan.md`,
            source_tool: "copilot-cli",
            metadata: { entry_type: "imported", session_id: session },
          });

          const vec = embed(content);
          insertEmbedding(db, "cold", "knowledge", id, vec);

          db.prepare(
            `INSERT INTO import_log (id, timestamp, source_tool, source_path, content_hash, target_entry_id, target_tier, target_type)
             VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
          ).run(
            createHash("md5").update(`${planPath}-${Date.now()}`).digest("hex"),
            Date.now(),
            "copilot-cli",
            planPath,
            contentHash,
            id,
            "cold",
            "knowledge",
          );

          result.imported++;
        } catch (err) {
          result.errors.push(`${session}/plan.md: ${err instanceof Error ? err.message : String(err)}`);
        }
      }
    }

    return result;
  }
}
```

- [ ] **Step 3: Run tests**

```bash
npx vitest run src/importers/copilot-cli.test.ts
```

Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/importers/copilot-cli.ts src/importers/copilot-cli.test.ts
git commit -m "feat: add Copilot CLI importer for plan.md files"
```

---

### Task 13: Register New MCP Tools (KB, Eval, Import, Session)

**Files:**
- Create: `src/tools/kb-tools.ts`
- Create: `src/tools/eval-tools.ts`
- Create: `src/tools/import-tools.ts`
- Create: `src/tools/session-tools.ts`
- Modify: `src/tools/registry.ts` — register new tool sets

- [ ] **Step 1: Implement KB tool handlers**

`src/tools/kb-tools.ts` — registers `kb_ingest_file`, `kb_ingest_dir`, `kb_search`, `kb_list_collections`, `kb_remove`, `kb_refresh`. Each handler calls the corresponding `src/ingestion/` module function. `kb_search` uses hierarchical retrieval (collection -> document -> chunk).

- [ ] **Step 2: Implement eval tool handlers**

`src/tools/eval-tools.ts` — registers `eval_benchmark`, `eval_report`. `eval_benchmark` calls `runBenchmark()`. `eval_report` calls `getRetrievalEvents()` + `computeMetrics()` and formats results.

- [ ] **Step 3: Implement import tool handlers**

`src/tools/import-tools.ts` — registers `import_host`. Detects installed host tools, runs appropriate importer.

- [ ] **Step 4: Implement session tool handlers**

`src/tools/session-tools.ts` — registers `session_start`, `session_end`, `session_context`. `session_start` runs auto-import sync, assembles hot tier. `session_end` runs `compactHotTier()`. `session_context` returns current hot tier entries as formatted text.

- [ ] **Step 5: Update registry to include all new tools**

Modify `src/tools/registry.ts` to import and register tools from all four new files alongside existing memory-tools and system-tools.

- [ ] **Step 6: Run all tests**

```bash
npx vitest run
```

Expected: All tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/tools/
git commit -m "feat: register KB, eval, import, and session MCP tools"
```

---

### Task 14: End-to-End Phase 2 Integration Test

**Files:**
- Create: `src/e2e-phase2.test.ts`

- [ ] **Step 1: Write integration test covering the full Phase 2 flow**

Test should exercise:
1. Ingest a directory of markdown files -> verify collection + chunks created
2. Search knowledge base -> verify hierarchical retrieval returns correct chunks
3. Store hot memories -> run compaction -> verify tier movement
4. Import from a mock Claude Code directory -> verify entries in warm tier
5. Log retrieval events -> compute metrics -> verify reasonable outputs
6. Run benchmark suite -> verify it produces a report

- [ ] **Step 2: Run all tests**

```bash
npx vitest run
```

Expected: All tests pass.

- [ ] **Step 3: Commit**

```bash
git add src/e2e-phase2.test.ts
git commit -m "test: add Phase 2 integration tests covering ingestion, compaction, import, eval"
```

---

## Phase 2 Complete

After Phase 2, total-recall has:
- **Knowledge base ingestion** — markdown + code chunking, hierarchical index (collection/document/chunk)
- **Compaction pipeline** — hot->warm (decay-scored), warm->cold (time sweep), cold->warm (access promotion)
- **Host tool importers** — Claude Code (memory files, CLAUDE.md) + Copilot CLI (plans)
- **Eval framework** — retrieval event logging, outcome tracking, metrics computation, synthetic benchmarks
- **14 additional MCP tools** for KB, eval, import, and session management

**Phase 3 will cover:** Skills, hooks, platform wrappers, TUI dashboard, README.
