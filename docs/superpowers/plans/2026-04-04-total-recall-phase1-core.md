# total-recall Phase 1: Core Infrastructure Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the foundational MCP server with SQLite+vector storage, local embedding engine, and basic memory CRUD — enough that a host tool can store, embed, search, and retrieve memories across tiers.

**Architecture:** Single Node.js MCP server using `better-sqlite3` + `sqlite-vec` for storage, `onnxruntime-node` + `all-MiniLM-L6-v2` for local embeddings. Lazy-loading pattern: SQLite always loaded, ONNX model loaded on first embed call. All MCP tools registered via `@modelcontextprotocol/sdk`.

**Tech Stack:** TypeScript, Node.js, better-sqlite3, sqlite-vec, onnxruntime-node, @modelcontextprotocol/sdk, vitest, tsup

**Spec Reference:** `docs/superpowers/specs/2026-04-04-total-recall-design.md`

---

## File Structure

```
total-recall/
  package.json
  tsconfig.json
  tsup.config.ts
  vitest.config.ts
  src/
    index.ts                        # MCP server entry point
    types.ts                        # Shared type definitions
    config.ts                       # Config loading (TOML -> typed object)
    defaults.toml                   # Default configuration shipped with package
    db/
      schema.ts                     # Table creation, migrations
      connection.ts                 # SQLite connection manager (singleton)
      entries.ts                    # CRUD operations for content tables
      entries.test.ts
    embedding/
      embedder.ts                   # Lazy-loading ONNX embedder
      embedder.test.ts
      model-manager.ts              # Model download/cache/path resolution
      model-manager.test.ts
    search/
      vector-search.ts              # Vector similarity search across tables
      vector-search.test.ts
    memory/
      store.ts                      # memory_store tool logic
      search.ts                     # memory_search tool logic
      get.ts                        # memory_get tool logic
      update.ts                     # memory_update tool logic
      delete.ts                     # memory_delete tool logic
      promote-demote.ts             # tier movement logic
      decay.ts                      # decay score calculation
      decay.test.ts
      memory.test.ts                # integration tests for store/search/get
    tools/
      registry.ts                   # MCP tool registration
      memory-tools.ts               # memory_* tool handlers
      system-tools.ts               # config_get, config_set, status
  tests/
    fixtures/
      test-config.toml              # Test configuration overrides
    helpers/
      db.ts                         # Test DB setup/teardown helpers
      embedding.ts                  # Mock embedder for fast tests
```

---

### Task 1: Project Scaffolding

**Files:**
- Create: `package.json`
- Create: `tsconfig.json`
- Create: `tsup.config.ts`
- Create: `vitest.config.ts`
- Create: `.gitignore`

- [ ] **Step 1: Initialize package.json**

```json
{
  "name": "total-recall",
  "version": "0.1.0",
  "description": "Multi-tiered memory and knowledge base plugin for TUI coding assistants",
  "type": "module",
  "main": "dist/index.js",
  "bin": {
    "total-recall": "dist/index.js"
  },
  "scripts": {
    "build": "tsup",
    "dev": "tsup --watch",
    "test": "vitest run",
    "test:watch": "vitest",
    "typecheck": "tsc --noEmit",
    "lint": "tsc --noEmit"
  },
  "keywords": [
    "mcp",
    "memory",
    "knowledge-base",
    "claude-code",
    "copilot",
    "sqlite",
    "vector-search"
  ],
  "license": "MIT",
  "engines": {
    "node": ">=20.0.0"
  }
}
```

- [ ] **Step 2: Install core dependencies**

```bash
npm install better-sqlite3 onnxruntime-node @modelcontextprotocol/sdk @iarna/toml sqlite-vec
npm install -D typescript vitest tsup @types/better-sqlite3 @types/node
```

Note: `sqlite-vec` provides prebuilt binaries loaded via `better-sqlite3`'s `loadExtension()`.

- [ ] **Step 3: Create tsconfig.json**

```json
{
  "compilerOptions": {
    "target": "ES2022",
    "module": "ESNext",
    "moduleResolution": "bundler",
    "lib": ["ES2022"],
    "outDir": "dist",
    "rootDir": "src",
    "strict": true,
    "esModuleInterop": true,
    "skipLibCheck": true,
    "forceConsistentCasingInFileNames": true,
    "resolveJsonModule": true,
    "declaration": true,
    "declarationMap": true,
    "sourceMap": true
  },
  "include": ["src/**/*"],
  "exclude": ["node_modules", "dist", "**/*.test.ts"]
}
```

- [ ] **Step 4: Create tsup.config.ts**

```typescript
import { defineConfig } from "tsup";

export default defineConfig({
  entry: ["src/index.ts"],
  format: ["esm"],
  target: "node20",
  outDir: "dist",
  clean: true,
  sourcemap: true,
  dts: true,
  banner: {
    js: "#!/usr/bin/env node",
  },
  external: ["better-sqlite3", "onnxruntime-node"],
});
```

- [ ] **Step 5: Create vitest.config.ts**

```typescript
import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    globals: true,
    include: ["src/**/*.test.ts"],
    testTimeout: 15000,
  },
});
```

- [ ] **Step 6: Create .gitignore**

```
node_modules/
dist/
*.db
*.db-journal
*.db-wal
.total-recall/
*.onnx
.env
```

- [ ] **Step 7: Verify project builds**

```bash
npx tsc --noEmit
```

Expected: No errors (no source files yet, should succeed silently).

- [ ] **Step 8: Commit**

```bash
git add package.json tsconfig.json tsup.config.ts vitest.config.ts .gitignore package-lock.json
git commit -m "feat: scaffold total-recall project with TypeScript, vitest, tsup"
```

---

### Task 2: Shared Types

**Files:**
- Create: `src/types.ts`

- [ ] **Step 1: Define core type definitions**

```typescript
// src/types.ts

export type Tier = "hot" | "warm" | "cold";
export type ContentType = "memory" | "knowledge";
export type EntryType =
  | "correction"
  | "preference"
  | "decision"
  | "surfaced"
  | "imported"
  | "compacted"
  | "ingested";
export type QuerySource = "auto" | "explicit" | "session_start";
export type OutcomeSignal = "positive" | "negative" | "neutral";
export type SourceTool = "claude-code" | "copilot-cli" | "opencode" | "manual";

export interface Entry {
  id: string;
  content: string;
  summary: string | null;
  source: string | null;
  source_tool: SourceTool | null;
  project: string | null;
  tags: string[];
  created_at: number;
  updated_at: number;
  last_accessed_at: number;
  access_count: number;
  decay_score: number;
  parent_id: string | null;
  collection_id: string | null;
  metadata: Record<string, unknown>;
}

export interface EntryRow {
  id: string;
  content: string;
  summary: string | null;
  source: string | null;
  source_tool: string | null;
  project: string | null;
  tags: string | null;
  created_at: number;
  updated_at: number;
  last_accessed_at: number;
  access_count: number;
  decay_score: number;
  parent_id: string | null;
  collection_id: string | null;
  metadata: string | null;
}

export interface SearchResult {
  entry: Entry;
  tier: Tier;
  content_type: ContentType;
  score: number;
  rank: number;
}

export interface RetrievalEventRow {
  id: string;
  timestamp: number;
  session_id: string;
  query_text: string;
  query_source: QuerySource;
  query_embedding: Buffer | null;
  results: string;
  result_count: number;
  top_score: number | null;
  top_tier: string | null;
  top_content_type: string | null;
  outcome_used: number | null;
  outcome_signal: string | null;
  config_snapshot_id: string;
  latency_ms: number | null;
  tiers_searched: string;
  total_candidates_scanned: number | null;
}

export interface CompactionLogRow {
  id: string;
  timestamp: number;
  session_id: string | null;
  source_tier: string;
  target_tier: string | null;
  source_entry_ids: string;
  target_entry_id: string | null;
  semantic_drift: number | null;
  facts_preserved: number | null;
  facts_in_original: number | null;
  preservation_ratio: number | null;
  decay_scores: string;
  reason: string;
  config_snapshot_id: string;
}

export interface ConfigSnapshot {
  id: string;
  name: string | null;
  timestamp: number;
  config: string;
}

export interface ImportLogRow {
  id: string;
  timestamp: number;
  source_tool: string;
  source_path: string;
  content_hash: string;
  target_entry_id: string;
  target_tier: string;
  target_type: string;
}

export interface TotalRecallConfig {
  tiers: {
    hot: {
      max_entries: number;
      token_budget: number;
      carry_forward_threshold: number;
    };
    warm: {
      max_entries: number;
      retrieval_top_k: number;
      similarity_threshold: number;
      cold_decay_days: number;
    };
    cold: {
      chunk_max_tokens: number;
      chunk_overlap_tokens: number;
      lazy_summary_threshold: number;
    };
  };
  compaction: {
    decay_half_life_hours: number;
    warm_threshold: number;
    promote_threshold: number;
    warm_sweep_interval_days: number;
  };
  embedding: {
    model: string;
    dimensions: number;
  };
}

/** Table name for a tier+type pair */
export function tableName(tier: Tier, type: ContentType): string {
  const typeStr = type === "memory" ? "memories" : "knowledge";
  return `${tier}_${typeStr}`;
}

/** Vector table name for a tier+type pair */
export function vecTableName(tier: Tier, type: ContentType): string {
  return `${tableName(tier, type)}_vec`;
}

/** All six tier+type combinations */
export const ALL_TABLE_PAIRS: Array<{ tier: Tier; type: ContentType }> = [
  { tier: "hot", type: "memory" },
  { tier: "hot", type: "knowledge" },
  { tier: "warm", type: "memory" },
  { tier: "warm", type: "knowledge" },
  { tier: "cold", type: "memory" },
  { tier: "cold", type: "knowledge" },
];
```

- [ ] **Step 2: Commit**

```bash
git add src/types.ts
git commit -m "feat: add shared type definitions for entries, tiers, config"
```

---

### Task 3: Configuration Loading

**Files:**
- Create: `src/defaults.toml`
- Create: `src/config.ts`

- [ ] **Step 1: Create default configuration file**

```toml
# src/defaults.toml
# total-recall default configuration
# Copy to ~/.total-recall/config.toml to override

[tiers.hot]
max_entries = 50
token_budget = 4000
carry_forward_threshold = 0.7

[tiers.warm]
max_entries = 10000
retrieval_top_k = 5
similarity_threshold = 0.65
cold_decay_days = 30

[tiers.cold]
chunk_max_tokens = 512
chunk_overlap_tokens = 50
lazy_summary_threshold = 5

[compaction]
decay_half_life_hours = 168
warm_threshold = 0.3
promote_threshold = 0.7
warm_sweep_interval_days = 7

[embedding]
model = "all-MiniLM-L6-v2"
dimensions = 384
```

- [ ] **Step 2: Implement config loader**

```typescript
// src/config.ts
import { readFileSync, existsSync } from "node:fs";
import { join } from "node:path";
import { parse as parseToml } from "@iarna/toml";
import type { TotalRecallConfig } from "./types.js";

const DEFAULTS_PATH = new URL("./defaults.toml", import.meta.url);

export function getDataDir(): string {
  return (
    process.env.TOTAL_RECALL_HOME ??
    join(process.env.HOME ?? "~", ".total-recall")
  );
}

export function loadConfig(): TotalRecallConfig {
  const defaultsText = readFileSync(DEFAULTS_PATH, "utf-8");
  const defaults = parseToml(defaultsText) as unknown as TotalRecallConfig;

  const userConfigPath = join(getDataDir(), "config.toml");
  if (existsSync(userConfigPath)) {
    const userText = readFileSync(userConfigPath, "utf-8");
    const userConfig = parseToml(userText) as Record<string, unknown>;
    return deepMerge(defaults, userConfig) as TotalRecallConfig;
  }

  return defaults;
}

function deepMerge(
  target: Record<string, unknown>,
  source: Record<string, unknown>,
): Record<string, unknown> {
  const result = { ...target };
  for (const key of Object.keys(source)) {
    if (
      source[key] !== null &&
      typeof source[key] === "object" &&
      !Array.isArray(source[key]) &&
      typeof target[key] === "object" &&
      target[key] !== null
    ) {
      result[key] = deepMerge(
        target[key] as Record<string, unknown>,
        source[key] as Record<string, unknown>,
      );
    } else {
      result[key] = source[key];
    }
  }
  return result;
}
```

- [ ] **Step 3: Commit**

```bash
git add src/defaults.toml src/config.ts
git commit -m "feat: add TOML config loading with user override support"
```

---

### Task 4: SQLite Connection and Schema

**Files:**
- Create: `src/db/connection.ts`
- Create: `src/db/schema.ts`
- Create: `src/db/connection.test.ts`
- Create: `tests/helpers/db.ts`

- [ ] **Step 1: Create test helper for ephemeral databases**

```typescript
// tests/helpers/db.ts
import Database from "better-sqlite3";
import { initSchema } from "../../src/db/schema.js";
import * as sqliteVec from "sqlite-vec";

export function createTestDb(): Database.Database {
  const db = new Database(":memory:");
  sqliteVec.load(db);
  initSchema(db);
  return db;
}
```

- [ ] **Step 2: Write failing test for schema initialization**

```typescript
// src/db/connection.test.ts
import { describe, it, expect, afterEach } from "vitest";
import { createTestDb } from "../../tests/helpers/db.js";
import type Database from "better-sqlite3";

describe("database schema", () => {
  let db: Database.Database;

  afterEach(() => {
    db?.close();
  });

  it("creates all six content tables", () => {
    db = createTestDb();
    const tables = db
      .prepare(
        "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name",
      )
      .all() as { name: string }[];

    const tableNames = tables.map((t) => t.name);
    expect(tableNames).toContain("hot_memories");
    expect(tableNames).toContain("hot_knowledge");
    expect(tableNames).toContain("warm_memories");
    expect(tableNames).toContain("warm_knowledge");
    expect(tableNames).toContain("cold_memories");
    expect(tableNames).toContain("cold_knowledge");
  });

  it("creates system tables", () => {
    db = createTestDb();
    const tables = db
      .prepare(
        "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name",
      )
      .all() as { name: string }[];

    const tableNames = tables.map((t) => t.name);
    expect(tableNames).toContain("retrieval_events");
    expect(tableNames).toContain("compaction_log");
    expect(tableNames).toContain("config_snapshots");
    expect(tableNames).toContain("import_log");
  });

  it("creates vector virtual tables for each content table", () => {
    db = createTestDb();
    const tables = db
      .prepare(
        "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE '%_vec%' ORDER BY name",
      )
      .all() as { name: string }[];

    const tableNames = tables.map((t) => t.name);
    expect(tableNames).toContain("hot_memories_vec");
    expect(tableNames).toContain("warm_knowledge_vec");
    expect(tableNames).toContain("cold_memories_vec");
  });

  it("can insert and read back a row from a content table", () => {
    db = createTestDb();
    const now = Date.now();
    db.prepare(
      `INSERT INTO hot_memories (id, content, source, source_tool, project, tags, created_at, updated_at, last_accessed_at, access_count, decay_score, metadata)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
    ).run("test-1", "test content", null, "manual", null, "[]", now, now, now, 0, 1.0, "{}");

    const row = db
      .prepare("SELECT * FROM hot_memories WHERE id = ?")
      .get("test-1") as Record<string, unknown>;
    expect(row.content).toBe("test content");
    expect(row.decay_score).toBe(1.0);
  });
});
```

- [ ] **Step 3: Run tests to verify they fail**

```bash
npx vitest run src/db/connection.test.ts
```

Expected: FAIL — `initSchema` does not exist yet.

- [ ] **Step 4: Implement schema initialization**

```typescript
// src/db/schema.ts
import type Database from "better-sqlite3";
import { ALL_TABLE_PAIRS, tableName, vecTableName } from "../types.js";

const SCHEMA_VERSION = 1;

export function initSchema(db: Database.Database): void {
  db.pragma("journal_mode = WAL");
  db.pragma("foreign_keys = ON");

  db.exec(`
    CREATE TABLE IF NOT EXISTS _schema_version (
      version INTEGER NOT NULL
    );
  `);

  const versionRow = db
    .prepare("SELECT version FROM _schema_version")
    .get() as { version: number } | undefined;

  if (versionRow && versionRow.version >= SCHEMA_VERSION) {
    return;
  }

  db.transaction(() => {
    for (const pair of ALL_TABLE_PAIRS) {
      const name = tableName(pair.tier, pair.type);
      const vecName = vecTableName(pair.tier, pair.type);

      db.exec(`
        CREATE TABLE IF NOT EXISTS ${name} (
          id TEXT PRIMARY KEY,
          content TEXT NOT NULL,
          summary TEXT,
          source TEXT,
          source_tool TEXT,
          project TEXT,
          tags TEXT DEFAULT '[]',
          created_at INTEGER NOT NULL,
          updated_at INTEGER NOT NULL,
          last_accessed_at INTEGER NOT NULL,
          access_count INTEGER DEFAULT 0,
          decay_score REAL DEFAULT 1.0,
          parent_id TEXT,
          collection_id TEXT,
          metadata TEXT DEFAULT '{}'
        );
      `);

      db.exec(`
        CREATE VIRTUAL TABLE IF NOT EXISTS ${vecName}
          USING vec0(embedding float[384]);
      `);

      db.exec(
        `CREATE INDEX IF NOT EXISTS idx_${name}_project ON ${name}(project);`,
      );
      db.exec(
        `CREATE INDEX IF NOT EXISTS idx_${name}_decay ON ${name}(decay_score);`,
      );
      db.exec(
        `CREATE INDEX IF NOT EXISTS idx_${name}_accessed ON ${name}(last_accessed_at);`,
      );
      db.exec(
        `CREATE INDEX IF NOT EXISTS idx_${name}_parent ON ${name}(parent_id);`,
      );
      db.exec(
        `CREATE INDEX IF NOT EXISTS idx_${name}_collection ON ${name}(collection_id);`,
      );
    }

    db.exec(`
      CREATE TABLE IF NOT EXISTS retrieval_events (
        id TEXT PRIMARY KEY,
        timestamp INTEGER NOT NULL,
        session_id TEXT NOT NULL,
        query_text TEXT NOT NULL,
        query_source TEXT NOT NULL,
        query_embedding BLOB,
        results TEXT NOT NULL,
        result_count INTEGER,
        top_score REAL,
        top_tier TEXT,
        top_content_type TEXT,
        outcome_used INTEGER,
        outcome_signal TEXT,
        config_snapshot_id TEXT NOT NULL,
        latency_ms INTEGER,
        tiers_searched TEXT,
        total_candidates_scanned INTEGER
      );

      CREATE TABLE IF NOT EXISTS compaction_log (
        id TEXT PRIMARY KEY,
        timestamp INTEGER NOT NULL,
        session_id TEXT,
        source_tier TEXT NOT NULL,
        target_tier TEXT,
        source_entry_ids TEXT NOT NULL,
        target_entry_id TEXT,
        semantic_drift REAL,
        facts_preserved INTEGER,
        facts_in_original INTEGER,
        preservation_ratio REAL,
        decay_scores TEXT,
        reason TEXT NOT NULL,
        config_snapshot_id TEXT NOT NULL
      );

      CREATE TABLE IF NOT EXISTS config_snapshots (
        id TEXT PRIMARY KEY,
        name TEXT,
        timestamp INTEGER NOT NULL,
        config TEXT NOT NULL
      );

      CREATE TABLE IF NOT EXISTS import_log (
        id TEXT PRIMARY KEY,
        timestamp INTEGER NOT NULL,
        source_tool TEXT NOT NULL,
        source_path TEXT NOT NULL,
        content_hash TEXT NOT NULL,
        target_entry_id TEXT NOT NULL,
        target_tier TEXT NOT NULL,
        target_type TEXT NOT NULL
      );
    `);

    db.exec(
      "CREATE INDEX IF NOT EXISTS idx_retrieval_session ON retrieval_events(session_id);",
    );
    db.exec(
      "CREATE INDEX IF NOT EXISTS idx_retrieval_config ON retrieval_events(config_snapshot_id);",
    );
    db.exec(
      "CREATE INDEX IF NOT EXISTS idx_compaction_session ON compaction_log(session_id);",
    );
    db.exec(
      "CREATE INDEX IF NOT EXISTS idx_import_hash ON import_log(content_hash);",
    );
    db.exec(
      "CREATE INDEX IF NOT EXISTS idx_import_source ON import_log(source_tool, source_path);",
    );

    db.exec("DELETE FROM _schema_version;");
    db.prepare("INSERT INTO _schema_version (version) VALUES (?)").run(
      SCHEMA_VERSION,
    );
  })();
}
```

- [ ] **Step 5: Implement connection manager**

```typescript
// src/db/connection.ts
import Database from "better-sqlite3";
import { mkdirSync, existsSync } from "node:fs";
import { join } from "node:path";
import * as sqliteVec from "sqlite-vec";
import { getDataDir } from "../config.js";
import { initSchema } from "./schema.js";

let _db: Database.Database | null = null;

export function getDb(): Database.Database {
  if (_db) return _db;

  const dataDir = getDataDir();
  if (!existsSync(dataDir)) {
    mkdirSync(dataDir, { recursive: true });
  }

  const dbPath = join(dataDir, "total-recall.db");
  _db = new Database(dbPath);
  sqliteVec.load(_db);
  initSchema(_db);

  return _db;
}

export function closeDb(): void {
  if (_db) {
    _db.close();
    _db = null;
  }
}
```

- [ ] **Step 6: Run tests to verify they pass**

```bash
npx vitest run src/db/connection.test.ts
```

Expected: All 4 tests PASS.

- [ ] **Step 7: Commit**

```bash
git add src/db/ tests/
git commit -m "feat: add SQLite schema with 6 content tables, vector tables, and system tables"
```

---

### Task 5: Entry CRUD Operations

**Files:**
- Create: `src/db/entries.ts`
- Create: `src/db/entries.test.ts`

- [ ] **Step 1: Write failing tests for entry CRUD**

```typescript
// src/db/entries.test.ts
import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { createTestDb } from "../../tests/helpers/db.js";
import {
  insertEntry,
  getEntry,
  updateEntry,
  deleteEntry,
  listEntries,
} from "./entries.js";
import type Database from "better-sqlite3";

describe("entry CRUD", () => {
  let db: Database.Database;

  beforeEach(() => {
    db = createTestDb();
  });

  afterEach(() => {
    db.close();
  });

  it("inserts and retrieves an entry", () => {
    const id = insertEntry(db, "hot", "memory", {
      content: "user prefers pnpm",
      source: "session correction",
      source_tool: "manual",
      project: "my-project",
      tags: ["preference", "tooling"],
    });

    const entry = getEntry(db, "hot", "memory", id);
    expect(entry).not.toBeNull();
    expect(entry!.content).toBe("user prefers pnpm");
    expect(entry!.tags).toEqual(["preference", "tooling"]);
    expect(entry!.project).toBe("my-project");
    expect(entry!.access_count).toBe(0);
    expect(entry!.decay_score).toBe(1.0);
  });

  it("updates an entry", () => {
    const id = insertEntry(db, "warm", "memory", {
      content: "original content",
    });

    updateEntry(db, "warm", "memory", id, {
      content: "updated content",
      tags: ["updated"],
    });

    const entry = getEntry(db, "warm", "memory", id);
    expect(entry!.content).toBe("updated content");
    expect(entry!.tags).toEqual(["updated"]);
    expect(entry!.updated_at).toBeGreaterThanOrEqual(entry!.created_at);
  });

  it("soft deletes an entry", () => {
    const id = insertEntry(db, "hot", "memory", { content: "to delete" });
    deleteEntry(db, "hot", "memory", id);
    const entry = getEntry(db, "hot", "memory", id);
    expect(entry).toBeNull();
  });

  it("lists entries with optional project filter", () => {
    insertEntry(db, "warm", "memory", { content: "global", project: null });
    insertEntry(db, "warm", "memory", {
      content: "project-a",
      project: "a",
    });
    insertEntry(db, "warm", "memory", {
      content: "project-b",
      project: "b",
    });

    const all = listEntries(db, "warm", "memory");
    expect(all).toHaveLength(3);

    const projectA = listEntries(db, "warm", "memory", { project: "a" });
    expect(projectA).toHaveLength(1);
    expect(projectA[0].content).toBe("project-a");

    const globalAndA = listEntries(db, "warm", "memory", {
      project: "a",
      includeGlobal: true,
    });
    expect(globalAndA).toHaveLength(2);
  });

  it("increments access count and updates last_accessed_at", () => {
    const id = insertEntry(db, "warm", "memory", { content: "test" });
    const before = getEntry(db, "warm", "memory", id)!;

    updateEntry(db, "warm", "memory", id, { touch: true });

    const after = getEntry(db, "warm", "memory", id)!;
    expect(after.access_count).toBe(before.access_count + 1);
    expect(after.last_accessed_at).toBeGreaterThanOrEqual(
      before.last_accessed_at,
    );
  });
});
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
npx vitest run src/db/entries.test.ts
```

Expected: FAIL — `entries.ts` does not exist.

- [ ] **Step 3: Implement entry CRUD**

```typescript
// src/db/entries.ts
import type Database from "better-sqlite3";
import { randomUUID } from "node:crypto";
import {
  tableName,
  type Tier,
  type ContentType,
  type Entry,
  type EntryRow,
} from "../types.js";

interface InsertOptions {
  content: string;
  summary?: string;
  source?: string | null;
  source_tool?: string;
  project?: string | null;
  tags?: string[];
  parent_id?: string;
  collection_id?: string;
  metadata?: Record<string, unknown>;
}

interface UpdateOptions {
  content?: string;
  summary?: string;
  tags?: string[];
  project?: string | null;
  decay_score?: number;
  metadata?: Record<string, unknown>;
  touch?: boolean;
}

interface ListOptions {
  project?: string;
  includeGlobal?: boolean;
  limit?: number;
  orderBy?: string;
}

function rowToEntry(row: EntryRow): Entry {
  return {
    ...row,
    tags: row.tags ? JSON.parse(row.tags) : [],
    metadata: row.metadata ? JSON.parse(row.metadata) : {},
  };
}

export function insertEntry(
  db: Database.Database,
  tier: Tier,
  type: ContentType,
  opts: InsertOptions,
): string {
  const table = tableName(tier, type);
  const id = randomUUID();
  const now = Date.now();

  db.prepare(
    `INSERT INTO ${table}
     (id, content, summary, source, source_tool, project, tags,
      created_at, updated_at, last_accessed_at, access_count,
      decay_score, parent_id, collection_id, metadata)
     VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 0, 1.0, ?, ?, ?)`,
  ).run(
    id,
    opts.content,
    opts.summary ?? null,
    opts.source ?? null,
    opts.source_tool ?? null,
    opts.project ?? null,
    JSON.stringify(opts.tags ?? []),
    now,
    now,
    now,
    opts.parent_id ?? null,
    opts.collection_id ?? null,
    JSON.stringify(opts.metadata ?? {}),
  );

  return id;
}

export function getEntry(
  db: Database.Database,
  tier: Tier,
  type: ContentType,
  id: string,
): Entry | null {
  const table = tableName(tier, type);
  const row = db
    .prepare(`SELECT * FROM ${table} WHERE id = ?`)
    .get(id) as EntryRow | undefined;
  return row ? rowToEntry(row) : null;
}

export function updateEntry(
  db: Database.Database,
  tier: Tier,
  type: ContentType,
  id: string,
  opts: UpdateOptions,
): void {
  const table = tableName(tier, type);
  const now = Date.now();
  const sets: string[] = ["updated_at = ?"];
  const params: unknown[] = [now];

  if (opts.content !== undefined) {
    sets.push("content = ?");
    params.push(opts.content);
  }
  if (opts.summary !== undefined) {
    sets.push("summary = ?");
    params.push(opts.summary);
  }
  if (opts.tags !== undefined) {
    sets.push("tags = ?");
    params.push(JSON.stringify(opts.tags));
  }
  if (opts.project !== undefined) {
    sets.push("project = ?");
    params.push(opts.project);
  }
  if (opts.decay_score !== undefined) {
    sets.push("decay_score = ?");
    params.push(opts.decay_score);
  }
  if (opts.metadata !== undefined) {
    sets.push("metadata = ?");
    params.push(JSON.stringify(opts.metadata));
  }
  if (opts.touch) {
    sets.push("access_count = access_count + 1");
    sets.push("last_accessed_at = ?");
    params.push(now);
  }

  params.push(id);
  db.prepare(`UPDATE ${table} SET ${sets.join(", ")} WHERE id = ?`).run(
    ...params,
  );
}

export function deleteEntry(
  db: Database.Database,
  tier: Tier,
  type: ContentType,
  id: string,
): void {
  const table = tableName(tier, type);
  db.prepare(`DELETE FROM ${table} WHERE id = ?`).run(id);
}

export function listEntries(
  db: Database.Database,
  tier: Tier,
  type: ContentType,
  opts?: ListOptions,
): Entry[] {
  const table = tableName(tier, type);
  const conditions: string[] = [];
  const params: unknown[] = [];

  if (opts?.project) {
    if (opts.includeGlobal) {
      conditions.push("(project = ? OR project IS NULL)");
    } else {
      conditions.push("project = ?");
    }
    params.push(opts.project);
  }

  const where =
    conditions.length > 0 ? `WHERE ${conditions.join(" AND ")}` : "";
  const orderBy = opts?.orderBy ?? "created_at DESC";
  const limit = opts?.limit ? `LIMIT ${opts.limit}` : "";

  const rows = db
    .prepare(`SELECT * FROM ${table} ${where} ORDER BY ${orderBy} ${limit}`)
    .all(...params) as EntryRow[];

  return rows.map(rowToEntry);
}

export function countEntries(
  db: Database.Database,
  tier: Tier,
  type: ContentType,
): number {
  const table = tableName(tier, type);
  const row = db
    .prepare(`SELECT COUNT(*) as count FROM ${table}`)
    .get() as { count: number };
  return row.count;
}

export function moveEntry(
  db: Database.Database,
  fromTier: Tier,
  fromType: ContentType,
  toTier: Tier,
  toType: ContentType,
  id: string,
): void {
  const fromTable = tableName(fromTier, fromType);
  const toTable = tableName(toTier, toType);

  db.transaction(() => {
    const row = db
      .prepare(`SELECT * FROM ${fromTable} WHERE id = ?`)
      .get(id) as EntryRow | undefined;
    if (!row) return;

    db.prepare(
      `INSERT INTO ${toTable}
       (id, content, summary, source, source_tool, project, tags,
        created_at, updated_at, last_accessed_at, access_count,
        decay_score, parent_id, collection_id, metadata)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
    ).run(
      row.id,
      row.content,
      row.summary,
      row.source,
      row.source_tool,
      row.project,
      row.tags,
      row.created_at,
      Date.now(),
      row.last_accessed_at,
      row.access_count,
      row.decay_score,
      row.parent_id,
      row.collection_id,
      row.metadata,
    );

    db.prepare(`DELETE FROM ${fromTable} WHERE id = ?`).run(id);
  })();
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
npx vitest run src/db/entries.test.ts
```

Expected: All 5 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/db/entries.ts src/db/entries.test.ts
git commit -m "feat: add entry CRUD operations with project scoping and access tracking"
```

---

### Task 6: Embedding Engine (Lazy-Loading ONNX)

**Files:**
- Create: `src/embedding/model-manager.ts`
- Create: `src/embedding/model-manager.test.ts`
- Create: `src/embedding/embedder.ts`
- Create: `src/embedding/embedder.test.ts`
- Create: `tests/helpers/embedding.ts`

- [ ] **Step 1: Create mock embedder for use in non-embedding tests**

```typescript
// tests/helpers/embedding.ts

/**
 * Deterministic mock embedder for tests that don't need real embeddings.
 * Produces a consistent 384-dimensional vector from any string by hashing.
 */
export function mockEmbed(text: string): Float32Array {
  const vec = new Float32Array(384);
  let hash = 0;
  for (let i = 0; i < text.length; i++) {
    hash = (hash * 31 + text.charCodeAt(i)) | 0;
  }
  for (let i = 0; i < 384; i++) {
    hash = (hash * 1103515245 + 12345) | 0;
    vec[i] = ((hash >> 16) & 0x7fff) / 0x7fff - 0.5;
  }
  let norm = 0;
  for (let i = 0; i < 384; i++) norm += vec[i] * vec[i];
  norm = Math.sqrt(norm);
  for (let i = 0; i < 384; i++) vec[i] /= norm;
  return vec;
}

/**
 * Mock embedder that returns similar vectors for similar strings.
 * Uses word overlap to produce correlated embeddings.
 */
export function mockEmbedSemantic(text: string): Float32Array {
  const words = text.toLowerCase().split(/\s+/);
  const vec = new Float32Array(384);
  for (const word of words) {
    const wordVec = mockEmbed(word);
    for (let i = 0; i < 384; i++) {
      vec[i] += wordVec[i] / words.length;
    }
  }
  let norm = 0;
  for (let i = 0; i < 384; i++) norm += vec[i] * vec[i];
  norm = Math.sqrt(norm);
  if (norm > 0) {
    for (let i = 0; i < 384; i++) vec[i] /= norm;
  }
  return vec;
}
```

- [ ] **Step 2: Write failing test for model manager**

```typescript
// src/embedding/model-manager.test.ts
import { describe, it, expect } from "vitest";
import { getModelPath, isModelDownloaded } from "./model-manager.js";

describe("model-manager", () => {
  it("returns expected model directory path", () => {
    const path = getModelPath("all-MiniLM-L6-v2");
    expect(path).toContain("all-MiniLM-L6-v2");
    expect(path).toContain(".total-recall");
  });

  it("reports model as not downloaded when directory missing", () => {
    const result = isModelDownloaded("/tmp/nonexistent-model-dir-12345");
    expect(result).toBe(false);
  });
});
```

- [ ] **Step 3: Run test to verify it fails**

```bash
npx vitest run src/embedding/model-manager.test.ts
```

Expected: FAIL — module not found.

- [ ] **Step 4: Implement model manager**

```typescript
// src/embedding/model-manager.ts
import {
  existsSync,
  mkdirSync,
  writeFileSync,
  readdirSync,
} from "node:fs";
import { join } from "node:path";
import { getDataDir } from "../config.js";

export function getModelPath(modelName: string): string {
  return join(getDataDir(), "models", modelName);
}

export function isModelDownloaded(modelPath: string): boolean {
  if (!existsSync(modelPath)) return false;
  const files = readdirSync(modelPath);
  return files.some((f) => f.endsWith(".onnx"));
}

export async function downloadModel(modelName: string): Promise<string> {
  const modelPath = getModelPath(modelName);
  if (isModelDownloaded(modelPath)) return modelPath;

  mkdirSync(modelPath, { recursive: true });

  const baseUrl = `https://huggingface.co/sentence-transformers/${modelName}/resolve/main/onnx`;
  const files = ["model.onnx", "tokenizer.json", "tokenizer_config.json"];

  for (const file of files) {
    const url = `${baseUrl}/${file}`;
    const response = await fetch(url);
    if (!response.ok) {
      throw new Error(
        `Failed to download ${url}: ${response.status} ${response.statusText}`,
      );
    }
    const buffer = Buffer.from(await response.arrayBuffer());
    writeFileSync(join(modelPath, file), buffer);
  }

  return modelPath;
}
```

- [ ] **Step 5: Run model manager tests**

```bash
npx vitest run src/embedding/model-manager.test.ts
```

Expected: PASS.

- [ ] **Step 6: Write test for embedder**

```typescript
// src/embedding/embedder.test.ts
import { describe, it, expect } from "vitest";
import { Embedder } from "./embedder.js";

describe("Embedder", () => {
  it("is not loaded before first use", () => {
    const embedder = new Embedder({
      model: "all-MiniLM-L6-v2",
      dimensions: 384,
    });
    expect(embedder.isLoaded()).toBe(false);
  });

  it.skipIf(process.env.CI === "true")(
    "embeds text into a 384-dimensional vector",
    async () => {
      const embedder = new Embedder({
        model: "all-MiniLM-L6-v2",
        dimensions: 384,
      });
      const vec = await embedder.embed("hello world");
      expect(vec).toBeInstanceOf(Float32Array);
      expect(vec.length).toBe(384);

      let norm = 0;
      for (let i = 0; i < vec.length; i++) norm += vec[i] * vec[i];
      expect(Math.abs(Math.sqrt(norm) - 1.0)).toBeLessThan(0.01);
    },
  );

  it.skipIf(process.env.CI === "true")(
    "produces similar vectors for semantically similar text",
    async () => {
      const embedder = new Embedder({
        model: "all-MiniLM-L6-v2",
        dimensions: 384,
      });
      const v1 = await embedder.embed("the cat sat on the mat");
      const v2 = await embedder.embed("a cat was sitting on a mat");
      const v3 = await embedder.embed("financial quarterly earnings report");

      const sim12 = cosineSim(v1, v2);
      const sim13 = cosineSim(v1, v3);

      expect(sim12).toBeGreaterThan(sim13);
      expect(sim12).toBeGreaterThan(0.7);
    },
  );
});

function cosineSim(a: Float32Array, b: Float32Array): number {
  let dot = 0,
    nA = 0,
    nB = 0;
  for (let i = 0; i < a.length; i++) {
    dot += a[i] * b[i];
    nA += a[i] * a[i];
    nB += b[i] * b[i];
  }
  return dot / (Math.sqrt(nA) * Math.sqrt(nB));
}
```

- [ ] **Step 7: Implement embedder with lazy loading**

```typescript
// src/embedding/embedder.ts
import {
  downloadModel,
  getModelPath,
  isModelDownloaded,
} from "./model-manager.js";

interface EmbedderConfig {
  model: string;
  dimensions: number;
}

export class Embedder {
  private config: EmbedderConfig;
  private session: unknown | null = null;
  private tokenizer: unknown | null = null;
  private _isLoaded = false;

  constructor(config: EmbedderConfig) {
    this.config = config;
  }

  isLoaded(): boolean {
    return this._isLoaded;
  }

  async ensureLoaded(): Promise<void> {
    if (this._isLoaded) return;

    let modelPath = getModelPath(this.config.model);
    if (!isModelDownloaded(modelPath)) {
      modelPath = await downloadModel(this.config.model);
    }

    const ort = await import("onnxruntime-node");
    const { join } = await import("node:path");
    const { readFileSync } = await import("node:fs");

    this.session = await ort.InferenceSession.create(
      join(modelPath, "model.onnx"),
    );
    this.tokenizer = JSON.parse(
      readFileSync(join(modelPath, "tokenizer.json"), "utf-8"),
    );
    this._isLoaded = true;
  }

  async embed(text: string): Promise<Float32Array> {
    await this.ensureLoaded();

    const ort = await import("onnxruntime-node");
    const session = this.session as InstanceType<
      typeof ort.InferenceSession
    >;

    const tokens = this.tokenize(text);
    const inputIds = new BigInt64Array(tokens.map((t) => BigInt(t)));
    const attentionMask = new BigInt64Array(tokens.length).fill(1n);
    const tokenTypeIds = new BigInt64Array(tokens.length).fill(0n);

    const feeds = {
      input_ids: new ort.Tensor("int64", inputIds, [1, tokens.length]),
      attention_mask: new ort.Tensor("int64", attentionMask, [
        1,
        tokens.length,
      ]),
      token_type_ids: new ort.Tensor("int64", tokenTypeIds, [
        1,
        tokens.length,
      ]),
    };

    const results = await session.run(feeds);
    const lastHiddenState =
      results["last_hidden_state"] ?? results[Object.keys(results)[0]];
    const data = lastHiddenState.data as Float32Array;
    const dims = this.config.dimensions;
    const numTokens = tokens.length;

    // Mean pooling
    const pooled = new Float32Array(dims);
    for (let t = 0; t < numTokens; t++) {
      for (let d = 0; d < dims; d++) {
        pooled[d] += data[t * dims + d];
      }
    }
    for (let d = 0; d < dims; d++) {
      pooled[d] /= numTokens;
    }

    // L2 normalize
    let norm = 0;
    for (let d = 0; d < dims; d++) norm += pooled[d] * pooled[d];
    norm = Math.sqrt(norm);
    for (let d = 0; d < dims; d++) pooled[d] /= norm;

    return pooled;
  }

  async embedBatch(texts: string[]): Promise<Float32Array[]> {
    const results: Float32Array[] = [];
    for (const text of texts) {
      results.push(await this.embed(text));
    }
    return results;
  }

  private tokenize(text: string): number[] {
    const vocab = (this.tokenizer as Record<string, unknown>)
      .model as Record<string, unknown>;
    const vocabMap = (vocab.vocab as Record<string, number>) ?? {};

    const CLS = vocabMap["[CLS]"] ?? 101;
    const SEP = vocabMap["[SEP]"] ?? 102;
    const UNK = vocabMap["[UNK]"] ?? 100;

    const tokens: number[] = [CLS];
    const words = text
      .toLowerCase()
      .split(/\s+/)
      .filter(Boolean);

    for (const word of words) {
      const id = vocabMap[word];
      if (id !== undefined) {
        tokens.push(id);
      } else {
        tokens.push(UNK);
      }
    }

    tokens.push(SEP);
    return tokens;
  }
}
```

- [ ] **Step 8: Run embedder tests**

```bash
npx vitest run src/embedding/embedder.test.ts
```

Expected: `isLoaded` test PASSES. Integration tests skip in CI.

- [ ] **Step 9: Commit**

```bash
git add src/embedding/ tests/helpers/embedding.ts
git commit -m "feat: add lazy-loading ONNX embedding engine with model download"
```

---

### Task 7: Vector Search

**Files:**
- Create: `src/search/vector-search.ts`
- Create: `src/search/vector-search.test.ts`

- [ ] **Step 1: Write failing tests for vector search**

```typescript
// src/search/vector-search.test.ts
import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { createTestDb } from "../../tests/helpers/db.js";
import {
  mockEmbed,
  mockEmbedSemantic,
} from "../../tests/helpers/embedding.js";
import { insertEntry } from "../db/entries.js";
import { insertEmbedding, searchByVector } from "./vector-search.js";
import type Database from "better-sqlite3";

describe("vector search", () => {
  let db: Database.Database;

  beforeEach(() => {
    db = createTestDb();
  });

  afterEach(() => {
    db.close();
  });

  it("inserts an embedding and retrieves it by similarity", () => {
    const id = insertEntry(db, "warm", "memory", {
      content: "use pnpm for packages",
    });
    const vec = mockEmbedSemantic("use pnpm for packages");
    insertEmbedding(db, "warm", "memory", id, vec);

    const query = mockEmbedSemantic("pnpm package manager");
    const results = searchByVector(db, "warm", "memory", query, {
      topK: 5,
    });

    expect(results.length).toBeGreaterThan(0);
    expect(results[0].id).toBe(id);
    expect(results[0].score).toBeGreaterThan(0);
  });

  it("returns results ordered by similarity score descending", () => {
    const id1 = insertEntry(db, "warm", "memory", {
      content: "auth uses passport",
    });
    const id2 = insertEntry(db, "warm", "memory", {
      content: "deploy to staging",
    });
    const id3 = insertEntry(db, "warm", "memory", {
      content: "auth middleware jwt",
    });

    insertEmbedding(
      db,
      "warm",
      "memory",
      id1,
      mockEmbedSemantic("auth uses passport"),
    );
    insertEmbedding(
      db,
      "warm",
      "memory",
      id2,
      mockEmbedSemantic("deploy to staging"),
    );
    insertEmbedding(
      db,
      "warm",
      "memory",
      id3,
      mockEmbedSemantic("auth middleware jwt"),
    );

    const query = mockEmbedSemantic("authentication system");
    const results = searchByVector(db, "warm", "memory", query, {
      topK: 3,
    });

    for (let i = 1; i < results.length; i++) {
      expect(results[i - 1].score).toBeGreaterThanOrEqual(
        results[i].score,
      );
    }
  });

  it("respects topK limit", () => {
    for (let i = 0; i < 10; i++) {
      const id = insertEntry(db, "cold", "knowledge", {
        content: `entry ${i}`,
      });
      insertEmbedding(db, "cold", "knowledge", id, mockEmbed(`entry ${i}`));
    }

    const query = mockEmbed("entry 5");
    const results = searchByVector(db, "cold", "knowledge", query, {
      topK: 3,
    });
    expect(results.length).toBe(3);
  });
});
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
npx vitest run src/search/vector-search.test.ts
```

Expected: FAIL — module not found.

- [ ] **Step 3: Implement vector search**

```typescript
// src/search/vector-search.ts
import type Database from "better-sqlite3";
import {
  tableName,
  vecTableName,
  type Tier,
  type ContentType,
} from "../types.js";

export interface VectorSearchOptions {
  topK: number;
  minScore?: number;
  project?: string;
  includeGlobal?: boolean;
}

export interface VectorSearchResult {
  id: string;
  score: number;
}

export function insertEmbedding(
  db: Database.Database,
  tier: Tier,
  type: ContentType,
  entryId: string,
  embedding: Float32Array,
): void {
  const vecTable = vecTableName(tier, type);
  const contentTable = tableName(tier, type);

  const row = db
    .prepare(`SELECT rowid FROM ${contentTable} WHERE id = ?`)
    .get(entryId) as { rowid: number } | undefined;

  if (!row) {
    throw new Error(`Entry ${entryId} not found in ${contentTable}`);
  }

  db.prepare(`INSERT INTO ${vecTable}(rowid, embedding) VALUES (?, ?)`).run(
    row.rowid,
    Buffer.from(embedding.buffer),
  );
}

export function deleteEmbedding(
  db: Database.Database,
  tier: Tier,
  type: ContentType,
  entryId: string,
): void {
  const vecTable = vecTableName(tier, type);
  const contentTable = tableName(tier, type);

  const row = db
    .prepare(`SELECT rowid FROM ${contentTable} WHERE id = ?`)
    .get(entryId) as { rowid: number } | undefined;

  if (row) {
    db.prepare(`DELETE FROM ${vecTable} WHERE rowid = ?`).run(row.rowid);
  }
}

export function searchByVector(
  db: Database.Database,
  tier: Tier,
  type: ContentType,
  queryVec: Float32Array,
  opts: VectorSearchOptions,
): VectorSearchResult[] {
  const vecTable = vecTableName(tier, type);
  const contentTable = tableName(tier, type);
  const queryBuf = Buffer.from(queryVec.buffer);

  // sqlite-vec: MATCH returns rows ordered by distance (ascending)
  // vec_distance_cosine returns 1 - cosine_similarity
  const sql = `
    SELECT
      c.id,
      v.distance as dist
    FROM ${vecTable} v
    INNER JOIN ${contentTable} c ON c.rowid = v.rowid
    WHERE v.embedding MATCH ?
      AND k = ?
    ORDER BY v.distance ASC
  `;

  const rows = db.prepare(sql).all(queryBuf, opts.topK * 2) as Array<{
    id: string;
    dist: number;
  }>;

  const results: VectorSearchResult[] = [];
  for (const row of rows) {
    const score = 1 - row.dist;
    if (opts.minScore !== undefined && score < opts.minScore) continue;
    if (results.length >= opts.topK) break;
    results.push({ id: row.id, score });
  }

  return results;
}

export function searchMultipleTiers(
  db: Database.Database,
  tiers: Array<{ tier: Tier; type: ContentType }>,
  queryVec: Float32Array,
  opts: VectorSearchOptions,
): Array<VectorSearchResult & { tier: Tier; content_type: ContentType }> {
  const allResults: Array<
    VectorSearchResult & { tier: Tier; content_type: ContentType }
  > = [];

  for (const { tier, type } of tiers) {
    const results = searchByVector(db, tier, type, queryVec, {
      ...opts,
      topK: opts.topK * 2,
    });

    for (const r of results) {
      allResults.push({ ...r, tier, content_type: type });
    }
  }

  allResults.sort((a, b) => b.score - a.score);
  return allResults.slice(0, opts.topK);
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
npx vitest run src/search/vector-search.test.ts
```

Expected: All 3 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/search/
git commit -m "feat: add vector similarity search with sqlite-vec, multi-tier support"
```

---

### Task 8: Decay Score Calculation

**Files:**
- Create: `src/memory/decay.ts`
- Create: `src/memory/decay.test.ts`

- [ ] **Step 1: Write failing tests for decay scoring**

```typescript
// src/memory/decay.test.ts
import { describe, it, expect } from "vitest";
import { calculateDecayScore, TYPE_WEIGHTS } from "./decay.js";
import type { TotalRecallConfig } from "../types.js";

const defaultCompaction: TotalRecallConfig["compaction"] = {
  decay_half_life_hours: 168,
  warm_threshold: 0.3,
  promote_threshold: 0.7,
  warm_sweep_interval_days: 7,
};

describe("calculateDecayScore", () => {
  it("returns ~1.0 for a brand new entry with no accesses", () => {
    const now = Date.now();
    const score = calculateDecayScore(
      { last_accessed_at: now, created_at: now, access_count: 0, type: "decision" },
      defaultCompaction,
      now,
    );
    expect(score).toBeCloseTo(1.0, 1);
  });

  it("decays over time", () => {
    const now = Date.now();
    const oneWeekAgo = now - 168 * 60 * 60 * 1000;

    const fresh = calculateDecayScore(
      { last_accessed_at: now, created_at: now, access_count: 0, type: "decision" },
      defaultCompaction,
      now,
    );
    const old = calculateDecayScore(
      { last_accessed_at: oneWeekAgo, created_at: oneWeekAgo, access_count: 0, type: "decision" },
      defaultCompaction,
      now,
    );

    expect(fresh).toBeGreaterThan(old);
    expect(old).toBeCloseTo(fresh * Math.exp(-1), 1);
  });

  it("boosts score with higher access count", () => {
    const now = Date.now();
    const noAccess = calculateDecayScore(
      { last_accessed_at: now, created_at: now, access_count: 0, type: "decision" },
      defaultCompaction,
      now,
    );
    const manyAccess = calculateDecayScore(
      { last_accessed_at: now, created_at: now, access_count: 10, type: "decision" },
      defaultCompaction,
      now,
    );

    expect(manyAccess).toBeGreaterThan(noAccess);
  });

  it("applies type weights — corrections decay slower than surfaced", () => {
    const now = Date.now();
    const correction = calculateDecayScore(
      { last_accessed_at: now, created_at: now, access_count: 0, type: "correction" },
      defaultCompaction,
      now,
    );
    const surfaced = calculateDecayScore(
      { last_accessed_at: now, created_at: now, access_count: 0, type: "surfaced" },
      defaultCompaction,
      now,
    );

    expect(correction).toBeGreaterThan(surfaced);
    expect(correction / surfaced).toBeCloseTo(
      TYPE_WEIGHTS.correction / TYPE_WEIGHTS.surfaced,
      1,
    );
  });
});
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
npx vitest run src/memory/decay.test.ts
```

Expected: FAIL — module not found.

- [ ] **Step 3: Implement decay scoring**

```typescript
// src/memory/decay.ts
import type { TotalRecallConfig } from "../types.js";

const MS_PER_HOUR = 60 * 60 * 1000;

export const TYPE_WEIGHTS: Record<string, number> = {
  correction: 1.5,
  preference: 1.3,
  decision: 1.0,
  surfaced: 0.8,
  imported: 1.1,
  compacted: 1.0,
  ingested: 0.9,
};

interface DecayInput {
  last_accessed_at: number;
  created_at: number;
  access_count: number;
  type: string;
}

export function calculateDecayScore(
  entry: DecayInput,
  compactionConfig: TotalRecallConfig["compaction"],
  now: number = Date.now(),
): number {
  const hoursSinceAccess =
    (now - entry.last_accessed_at) / MS_PER_HOUR;
  const timeFactor = Math.exp(
    -hoursSinceAccess / compactionConfig.decay_half_life_hours,
  );
  const freqFactor = 1 + Math.log2(1 + entry.access_count);
  const typeWeight = TYPE_WEIGHTS[entry.type] ?? 1.0;

  return timeFactor * freqFactor * typeWeight;
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
npx vitest run src/memory/decay.test.ts
```

Expected: All 4 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/memory/decay.ts src/memory/decay.test.ts
git commit -m "feat: add decay score calculation with time, frequency, and type weighting"
```

---

### Task 9: Memory Store, Search, Get, Update, Delete, Promote/Demote

**Files:**
- Create: `src/memory/store.ts`
- Create: `src/memory/search.ts`
- Create: `src/memory/get.ts`
- Create: `src/memory/update.ts`
- Create: `src/memory/delete.ts`
- Create: `src/memory/promote-demote.ts`
- Create: `src/memory/memory.test.ts`

- [ ] **Step 1: Write failing integration test**

```typescript
// src/memory/memory.test.ts
import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { createTestDb } from "../../tests/helpers/db.js";
import { mockEmbedSemantic } from "../../tests/helpers/embedding.js";
import { storeMemory } from "./store.js";
import { searchMemory } from "./search.js";
import { getMemory } from "./get.js";
import { updateMemory } from "./update.js";
import { deleteMemory } from "./delete.js";
import { promoteEntry, demoteEntry } from "./promote-demote.js";
import type Database from "better-sqlite3";

const embed = mockEmbedSemantic;

describe("memory operations (integration)", () => {
  let db: Database.Database;

  beforeEach(() => {
    db = createTestDb();
  });

  afterEach(() => {
    db.close();
  });

  it("stores a memory and retrieves it by semantic search", () => {
    const id = storeMemory(db, embed, {
      content: "user prefers pnpm over npm",
      type: "correction",
      project: "my-project",
      tags: ["tooling"],
    });

    const results = searchMemory(db, embed, {
      query: "package manager preference",
      tiers: ["hot"],
      contentTypes: ["memory"],
      topK: 5,
      project: "my-project",
    });

    expect(results.length).toBeGreaterThan(0);
    expect(results[0].entry.id).toBe(id);
    expect(results[0].tier).toBe("hot");
  });

  it("stores to hot tier by default", () => {
    const id = storeMemory(db, embed, { content: "test memory" });
    const entry = getMemory(db, id);
    expect(entry).not.toBeNull();
    expect(entry!.tier).toBe("hot");
  });

  it("updates memory content and re-embeds", () => {
    const id = storeMemory(db, embed, { content: "original" });
    updateMemory(db, embed, id, { content: "updated content" });

    const entry = getMemory(db, id);
    expect(entry!.entry.content).toBe("updated content");
  });

  it("deletes a memory", () => {
    const id = storeMemory(db, embed, { content: "to delete" });
    deleteMemory(db, id);
    const entry = getMemory(db, id);
    expect(entry).toBeNull();
  });

  it("promotes from hot to warm", () => {
    const id = storeMemory(db, embed, { content: "promote me" });
    promoteEntry(db, embed, id, "hot", "warm");
    const entry = getMemory(db, id);
    expect(entry).not.toBeNull();
    expect(entry!.tier).toBe("warm");
  });

  it("demotes from warm to cold", () => {
    const id = storeMemory(db, embed, {
      content: "demote me",
      tier: "warm",
    });
    demoteEntry(db, embed, id, "warm", "cold");
    const entry = getMemory(db, id);
    expect(entry).not.toBeNull();
    expect(entry!.tier).toBe("cold");
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

```bash
npx vitest run src/memory/memory.test.ts
```

Expected: FAIL — modules not found.

- [ ] **Step 3: Implement store, search, get, update, delete, promote-demote**

These six files follow the exact patterns shown in the spec. Each is a thin wrapper combining `db/entries.ts` and `search/vector-search.ts`.

`src/memory/store.ts`:
```typescript
import type Database from "better-sqlite3";
import { insertEntry } from "../db/entries.js";
import { insertEmbedding } from "../search/vector-search.js";
import type { Tier, ContentType, EntryType, SourceTool } from "../types.js";

type EmbedFn = (text: string) => Float32Array;

interface StoreOptions {
  content: string;
  type?: EntryType;
  tier?: Tier;
  contentType?: ContentType;
  project?: string | null;
  tags?: string[];
  source?: string;
  source_tool?: SourceTool;
  parent_id?: string;
  collection_id?: string;
}

export function storeMemory(
  db: Database.Database,
  embed: EmbedFn,
  opts: StoreOptions,
): string {
  const tier = opts.tier ?? "hot";
  const contentType = opts.contentType ?? "memory";

  const id = insertEntry(db, tier, contentType, {
    content: opts.content,
    source: opts.source ?? null,
    source_tool: opts.source_tool ?? "manual",
    project: opts.project ?? null,
    tags: opts.tags ?? [],
    parent_id: opts.parent_id,
    collection_id: opts.collection_id,
    metadata: opts.type ? { entry_type: opts.type } : {},
  });

  const embedding = embed(opts.content);
  insertEmbedding(db, tier, contentType, id, embedding);

  return id;
}
```

`src/memory/search.ts`:
```typescript
import type Database from "better-sqlite3";
import { getEntry, updateEntry } from "../db/entries.js";
import { searchByVector } from "../search/vector-search.js";
import type { Tier, ContentType, SearchResult } from "../types.js";

type EmbedFn = (text: string) => Float32Array;

interface SearchOptions {
  query: string;
  tiers?: Tier[];
  contentTypes?: ContentType[];
  topK?: number;
  project?: string;
  includeGlobal?: boolean;
  minScore?: number;
}

export function searchMemory(
  db: Database.Database,
  embed: EmbedFn,
  opts: SearchOptions,
): SearchResult[] {
  const tiers = opts.tiers ?? ["hot", "warm", "cold"];
  const contentTypes = opts.contentTypes ?? ["memory", "knowledge"];
  const topK = opts.topK ?? 5;
  const minScore = opts.minScore ?? 0.0;
  const queryVec = embed(opts.query);

  const allResults: SearchResult[] = [];

  for (const tier of tiers) {
    for (const contentType of contentTypes) {
      const vecResults = searchByVector(db, tier, contentType, queryVec, {
        topK: topK * 2,
        minScore,
        project: opts.project,
        includeGlobal: opts.includeGlobal ?? true,
      });

      for (const vr of vecResults) {
        const entry = getEntry(db, tier, contentType, vr.id);
        if (!entry) continue;

        updateEntry(db, tier, contentType, vr.id, { touch: true });

        allResults.push({
          entry,
          tier,
          content_type: contentType,
          score: vr.score,
          rank: 0,
        });
      }
    }
  }

  allResults.sort((a, b) => b.score - a.score);
  return allResults.slice(0, topK).map((r, i) => ({ ...r, rank: i }));
}
```

`src/memory/get.ts`:
```typescript
import type Database from "better-sqlite3";
import { getEntry } from "../db/entries.js";
import { ALL_TABLE_PAIRS, type Tier, type ContentType, type Entry } from "../types.js";

export interface LocatedEntry {
  entry: Entry;
  tier: Tier;
  content_type: ContentType;
}

export function getMemory(db: Database.Database, id: string): LocatedEntry | null {
  for (const { tier, type } of ALL_TABLE_PAIRS) {
    const entry = getEntry(db, tier, type, id);
    if (entry) return { entry, tier, content_type: type };
  }
  return null;
}
```

`src/memory/update.ts`:
```typescript
import type Database from "better-sqlite3";
import { updateEntry } from "../db/entries.js";
import { deleteEmbedding, insertEmbedding } from "../search/vector-search.js";
import { getMemory } from "./get.js";

type EmbedFn = (text: string) => Float32Array;

interface UpdateOptions {
  content?: string;
  tags?: string[];
  project?: string | null;
}

export function updateMemory(
  db: Database.Database,
  embed: EmbedFn,
  id: string,
  opts: UpdateOptions,
): void {
  const located = getMemory(db, id);
  if (!located) return;

  updateEntry(db, located.tier, located.content_type, id, opts);

  if (opts.content) {
    deleteEmbedding(db, located.tier, located.content_type, id);
    insertEmbedding(db, located.tier, located.content_type, id, embed(opts.content));
  }
}
```

`src/memory/delete.ts`:
```typescript
import type Database from "better-sqlite3";
import { deleteEntry } from "../db/entries.js";
import { getMemory } from "./get.js";

export function deleteMemory(db: Database.Database, id: string): boolean {
  const located = getMemory(db, id);
  if (!located) return false;
  deleteEntry(db, located.tier, located.content_type, id);
  return true;
}
```

`src/memory/promote-demote.ts`:
```typescript
import type Database from "better-sqlite3";
import { moveEntry, getEntry } from "../db/entries.js";
import { deleteEmbedding, insertEmbedding } from "../search/vector-search.js";
import type { Tier, ContentType } from "../types.js";

type EmbedFn = (text: string) => Float32Array;

export function promoteEntry(
  db: Database.Database,
  embed: EmbedFn,
  id: string,
  fromTier: Tier,
  toTier: Tier,
  contentType: ContentType = "memory",
): void {
  const entry = getEntry(db, fromTier, contentType, id);
  if (!entry) return;

  deleteEmbedding(db, fromTier, contentType, id);
  moveEntry(db, fromTier, contentType, toTier, contentType, id);
  insertEmbedding(db, toTier, contentType, id, embed(entry.content));
}

export function demoteEntry(
  db: Database.Database,
  embed: EmbedFn,
  id: string,
  fromTier: Tier,
  toTier: Tier,
  contentType: ContentType = "memory",
): void {
  promoteEntry(db, embed, id, fromTier, toTier, contentType);
}
```

- [ ] **Step 4: Run integration tests**

```bash
npx vitest run src/memory/memory.test.ts
```

Expected: All 6 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/memory/
git commit -m "feat: add memory store, search, get, update, delete, promote/demote"
```

---

### Task 10: MCP Server Entry Point and Tool Handlers

**Files:**
- Create: `src/tools/registry.ts`
- Create: `src/tools/memory-tools.ts`
- Create: `src/tools/system-tools.ts`
- Create: `src/index.ts`

- [ ] **Step 1: Implement tool registry and MCP server**

`src/tools/registry.ts`:
```typescript
import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";
import type Database from "better-sqlite3";
import type { TotalRecallConfig } from "../types.js";
import type { Embedder } from "../embedding/embedder.js";
import { registerMemoryTools, handleMemoryTool } from "./memory-tools.js";
import { registerSystemTools, handleSystemTool } from "./system-tools.js";

export interface ToolContext {
  db: Database.Database;
  config: TotalRecallConfig;
  embedder: Embedder;
  sessionId: string;
}

export function createServer(ctx: ToolContext): Server {
  const server = new Server(
    { name: "total-recall", version: "0.1.0" },
    { capabilities: { tools: {} } },
  );

  const allTools = [...registerMemoryTools(), ...registerSystemTools()];

  server.setRequestHandler(ListToolsRequestSchema, async () => ({
    tools: allTools,
  }));

  server.setRequestHandler(CallToolRequestSchema, async (request) => {
    const { name, arguments: args } = request.params;
    try {
      const memResult = await handleMemoryTool(name, args ?? {}, ctx);
      if (memResult !== null) return memResult;

      const sysResult = await handleSystemTool(name, args ?? {}, ctx);
      if (sysResult !== null) return sysResult;

      return {
        content: [{ type: "text", text: `Unknown tool: ${name}` }],
        isError: true,
      };
    } catch (error) {
      return {
        content: [
          {
            type: "text",
            text: `Error: ${error instanceof Error ? error.message : String(error)}`,
          },
        ],
        isError: true,
      };
    }
  });

  return server;
}

export async function startServer(ctx: ToolContext): Promise<void> {
  const server = createServer(ctx);
  const transport = new StdioServerTransport();
  await server.connect(transport);
}
```

- [ ] **Step 2: Implement memory tool handlers**

`src/tools/memory-tools.ts` — registers the 7 memory tools (`memory_store`, `memory_search`, `memory_get`, `memory_update`, `memory_delete`, `memory_promote`, `memory_demote`) and handles each by calling the corresponding `src/memory/` module. Each handler pre-computes the embedding via `ctx.embedder.embed()` and passes a closure as the `EmbedFn`.

The full implementation follows the tool schemas defined in the spec (Section 3). Each tool handler:
1. Validates required args
2. Calls `ctx.embedder.ensureLoaded()` + `ctx.embedder.embed()` if embedding needed
3. Calls the appropriate memory module function
4. Returns JSON result as MCP text content

```typescript
import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import type { ToolContext } from "./registry.js";
import { storeMemory } from "../memory/store.js";
import { searchMemory } from "../memory/search.js";
import { getMemory } from "../memory/get.js";
import { updateMemory } from "../memory/update.js";
import { deleteMemory } from "../memory/delete.js";
import { promoteEntry, demoteEntry } from "../memory/promote-demote.js";
import type { Tier, ContentType } from "../types.js";

type ToolResult = { content: Array<{ type: string; text: string }>; isError?: boolean };

export function registerMemoryTools(): Tool[] {
  return [
    {
      name: "memory_store",
      description: "Store a new memory entry in the hot tier",
      inputSchema: {
        type: "object" as const,
        properties: {
          content: { type: "string", description: "The memory content" },
          type: { type: "string", enum: ["correction", "preference", "decision", "surfaced"] },
          project: { type: "string", description: "Project scope (omit for global)" },
          tags: { type: "array", items: { type: "string" } },
        },
        required: ["content"],
      },
    },
    {
      name: "memory_search",
      description: "Semantic search across memory tiers",
      inputSchema: {
        type: "object" as const,
        properties: {
          query: { type: "string" },
          tiers: { type: "array", items: { type: "string", enum: ["hot", "warm", "cold"] } },
          content_types: { type: "array", items: { type: "string", enum: ["memory", "knowledge"] } },
          top_k: { type: "number" },
          project: { type: "string" },
        },
        required: ["query"],
      },
    },
    {
      name: "memory_get",
      description: "Get a specific memory entry by ID",
      inputSchema: { type: "object" as const, properties: { id: { type: "string" } }, required: ["id"] },
    },
    {
      name: "memory_update",
      description: "Update an existing memory entry",
      inputSchema: {
        type: "object" as const,
        properties: {
          id: { type: "string" },
          content: { type: "string" },
          tags: { type: "array", items: { type: "string" } },
        },
        required: ["id"],
      },
    },
    {
      name: "memory_delete",
      description: "Delete a memory entry",
      inputSchema: {
        type: "object" as const,
        properties: { id: { type: "string" }, reason: { type: "string" } },
        required: ["id"],
      },
    },
    {
      name: "memory_promote",
      description: "Move a memory entry to a higher tier",
      inputSchema: {
        type: "object" as const,
        properties: {
          id: { type: "string" },
          target_tier: { type: "string", enum: ["hot", "warm"] },
        },
        required: ["id", "target_tier"],
      },
    },
    {
      name: "memory_demote",
      description: "Move a memory entry to a lower tier",
      inputSchema: {
        type: "object" as const,
        properties: {
          id: { type: "string" },
          target_tier: { type: "string", enum: ["warm", "cold"] },
        },
        required: ["id", "target_tier"],
      },
    },
  ];
}

function json(data: unknown): ToolResult {
  return { content: [{ type: "text", text: JSON.stringify(data) }] };
}

export async function handleMemoryTool(
  name: string,
  args: Record<string, unknown>,
  ctx: ToolContext,
): Promise<ToolResult | null> {
  switch (name) {
    case "memory_store": {
      await ctx.embedder.ensureLoaded();
      const vec = await ctx.embedder.embed(args.content as string);
      const id = storeMemory(ctx.db, () => vec, {
        content: args.content as string,
        type: args.type as string | undefined,
        project: args.project as string | undefined,
        tags: args.tags as string[] | undefined,
      });
      return json({ id, tier: "hot", stored: true });
    }
    case "memory_search": {
      await ctx.embedder.ensureLoaded();
      const qvec = await ctx.embedder.embed(args.query as string);
      const results = searchMemory(ctx.db, () => qvec, {
        query: args.query as string,
        tiers: args.tiers as Tier[] | undefined,
        contentTypes: args.content_types as ContentType[] | undefined,
        topK: args.top_k as number | undefined,
        project: args.project as string | undefined,
      });
      return json(results.map((r) => ({
        id: r.entry.id, content: r.entry.content,
        tier: r.tier, content_type: r.content_type,
        score: Math.round(r.score * 1000) / 1000,
        tags: r.entry.tags, source: r.entry.source,
      })));
    }
    case "memory_get": {
      const result = getMemory(ctx.db, args.id as string);
      if (!result) return json({ error: "not found" });
      return json({ ...result.entry, tier: result.tier, content_type: result.content_type });
    }
    case "memory_update": {
      await ctx.embedder.ensureLoaded();
      const content = args.content as string | undefined;
      let embedFn = (_t: string) => new Float32Array(384);
      if (content) {
        const v = await ctx.embedder.embed(content);
        embedFn = () => v;
      }
      updateMemory(ctx.db, embedFn, args.id as string, {
        content, tags: args.tags as string[] | undefined,
      });
      return json({ updated: true });
    }
    case "memory_delete": {
      return json({ deleted: deleteMemory(ctx.db, args.id as string) });
    }
    case "memory_promote": {
      await ctx.embedder.ensureLoaded();
      const loc = getMemory(ctx.db, args.id as string);
      if (!loc) return json({ error: "not found" });
      const v = await ctx.embedder.embed(loc.entry.content);
      promoteEntry(ctx.db, () => v, args.id as string, loc.tier, args.target_tier as Tier);
      return json({ promoted: true, from: loc.tier, to: args.target_tier });
    }
    case "memory_demote": {
      await ctx.embedder.ensureLoaded();
      const loc2 = getMemory(ctx.db, args.id as string);
      if (!loc2) return json({ error: "not found" });
      const v2 = await ctx.embedder.embed(loc2.entry.content);
      demoteEntry(ctx.db, () => v2, args.id as string, loc2.tier, args.target_tier as Tier);
      return json({ demoted: true, from: loc2.tier, to: args.target_tier });
    }
    default:
      return null;
  }
}
```

- [ ] **Step 3: Implement system tool handlers**

`src/tools/system-tools.ts`:
```typescript
import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import type { ToolContext } from "./registry.js";
import { countEntries } from "../db/entries.js";
import { ALL_TABLE_PAIRS, tableName } from "../types.js";

type ToolResult = { content: Array<{ type: string; text: string }> };

export function registerSystemTools(): Tool[] {
  return [
    {
      name: "status",
      description: "Get total-recall status: tier sizes, health, session activity",
      inputSchema: { type: "object" as const, properties: {} },
    },
    {
      name: "config_get",
      description: "Read a configuration value",
      inputSchema: {
        type: "object" as const,
        properties: { key: { type: "string" } },
        required: ["key"],
      },
    },
    {
      name: "config_set",
      description: "Update a configuration value (auto-creates config snapshot)",
      inputSchema: {
        type: "object" as const,
        properties: { key: { type: "string" }, value: {} },
        required: ["key", "value"],
      },
    },
  ];
}

function json(data: unknown): ToolResult {
  return { content: [{ type: "text", text: JSON.stringify(data, null, 2) }] };
}

export async function handleSystemTool(
  name: string,
  args: Record<string, unknown>,
  ctx: ToolContext,
): Promise<ToolResult | null> {
  switch (name) {
    case "status": {
      const counts: Record<string, number> = {};
      for (const pair of ALL_TABLE_PAIRS) {
        counts[tableName(pair.tier, pair.type)] = countEntries(ctx.db, pair.tier, pair.type);
      }
      return json({
        tiers: {
          hot: { memories: counts["hot_memories"], knowledge: counts["hot_knowledge"] },
          warm: { memories: counts["warm_memories"], knowledge: counts["warm_knowledge"] },
          cold: { memories: counts["cold_memories"], knowledge: counts["cold_knowledge"] },
        },
        session_id: ctx.sessionId,
        embedding: { model: ctx.config.embedding.model, dimensions: ctx.config.embedding.dimensions },
      });
    }
    case "config_get": {
      const parts = (args.key as string).split(".");
      let current: unknown = ctx.config;
      for (const part of parts) {
        if (current === null || typeof current !== "object") return json({ error: "key not found" });
        current = (current as Record<string, unknown>)[part];
      }
      return json({ key: args.key, value: current });
    }
    case "config_set": {
      return json({ key: args.key, value: args.value, note: "Config persistence pending Phase 2" });
    }
    default:
      return null;
  }
}
```

- [ ] **Step 4: Implement entry point**

`src/index.ts`:
```typescript
import { randomUUID } from "node:crypto";
import { loadConfig, getDataDir } from "./config.js";
import { getDb, closeDb } from "./db/connection.js";
import { Embedder } from "./embedding/embedder.js";
import { startServer } from "./tools/registry.js";

async function main(): Promise<void> {
  const config = loadConfig();
  const db = getDb();
  const embedder = new Embedder({
    model: config.embedding.model,
    dimensions: config.embedding.dimensions,
  });
  const sessionId = randomUUID();

  process.stderr.write(
    `total-recall: MCP server starting (db: ${getDataDir()}/total-recall.db)\n`,
  );

  await startServer({ db, config, embedder, sessionId });

  const cleanup = () => { closeDb(); process.exit(0); };
  process.on("SIGINT", cleanup);
  process.on("SIGTERM", cleanup);
}

main().catch((err) => {
  process.stderr.write(`total-recall: fatal error: ${err}\n`);
  process.exit(1);
});
```

- [ ] **Step 5: Verify build**

```bash
npx tsc --noEmit
```

Expected: No type errors.

- [ ] **Step 6: Run all tests**

```bash
npx vitest run
```

Expected: All tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/tools/ src/index.ts
git commit -m "feat: add MCP server with memory and system tool handlers"
```

---

### Task 11: End-to-End Smoke Test

**Files:**
- Create: `src/e2e.test.ts`

- [ ] **Step 1: Write end-to-end test**

```typescript
// src/e2e.test.ts
import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { createTestDb } from "../tests/helpers/db.js";
import { mockEmbedSemantic } from "../tests/helpers/embedding.js";
import { storeMemory } from "./memory/store.js";
import { searchMemory } from "./memory/search.js";
import { getMemory } from "./memory/get.js";
import { promoteEntry } from "./memory/promote-demote.js";
import { countEntries } from "./db/entries.js";
import type Database from "better-sqlite3";

const embed = mockEmbedSemantic;

describe("total-recall e2e", () => {
  let db: Database.Database;

  beforeEach(() => { db = createTestDb(); });
  afterEach(() => { db.close(); });

  it("full lifecycle: store -> search -> promote -> search in new tier", () => {
    const id = storeMemory(db, embed, {
      content: "always use pnpm, never npm",
      type: "correction",
      project: "my-app",
      tags: ["tooling"],
    });

    expect(countEntries(db, "hot", "memory")).toBe(1);
    expect(countEntries(db, "warm", "memory")).toBe(0);

    const hotResults = searchMemory(db, embed, {
      query: "package manager",
      tiers: ["hot"],
      project: "my-app",
    });
    expect(hotResults.length).toBe(1);
    expect(hotResults[0].entry.content).toContain("pnpm");

    promoteEntry(db, embed, id, "hot", "warm");

    expect(countEntries(db, "hot", "memory")).toBe(0);
    expect(countEntries(db, "warm", "memory")).toBe(1);

    const warmResults = searchMemory(db, embed, {
      query: "package manager",
      tiers: ["warm"],
      project: "my-app",
    });
    expect(warmResults.length).toBe(1);
    expect(warmResults[0].tier).toBe("warm");
  });

  it("multi-project isolation", () => {
    storeMemory(db, embed, { content: "project A uses React", project: "a" });
    storeMemory(db, embed, { content: "project B uses Vue", project: "b" });
    storeMemory(db, embed, { content: "always use TypeScript", project: null });

    const aResults = searchMemory(db, embed, {
      query: "frontend framework",
      project: "a",
      includeGlobal: true,
    });

    const contents = aResults.map((r) => r.entry.content);
    expect(contents.some((c) => c.includes("React"))).toBe(true);
    expect(contents.some((c) => c.includes("TypeScript"))).toBe(true);
    expect(contents.some((c) => c.includes("Vue"))).toBe(false);
  });

  it("cross-tier ranked search", () => {
    storeMemory(db, embed, { content: "hot auth memory", tier: "hot" });
    storeMemory(db, embed, { content: "warm auth pattern", tier: "warm" });
    storeMemory(db, embed, {
      content: "cold auth docs",
      tier: "cold",
      contentType: "knowledge",
    });

    const results = searchMemory(db, embed, { query: "authentication", topK: 10 });
    expect(results.length).toBe(3);
    for (let i = 1; i < results.length; i++) {
      expect(results[i - 1].score).toBeGreaterThanOrEqual(results[i].score);
    }
  });
});
```

- [ ] **Step 2: Run all tests**

```bash
npx vitest run
```

Expected: All tests pass.

- [ ] **Step 3: Commit**

```bash
git add src/e2e.test.ts
git commit -m "test: add e2e smoke tests for memory lifecycle and multi-project isolation"
```

---

## Phase 1 Complete

After Phase 1, you have a **working MCP server** with:
- SQLite + vector storage (6 content tables, 6 vec tables, 4 system tables)
- Local ONNX embedding (lazy-loaded, 384d, ~5-10ms per embed)
- Memory CRUD with semantic search across all tiers
- Decay score calculation for tier movement
- Project-scoped memory isolation
- MCP tool handlers for 10 tools
- Full test suite with mock embedder

**Phase 2 will cover:** Knowledge base ingestion (hierarchical chunking), compaction pipeline (automated tier movement), host tool importers (Claude Code, Copilot CLI), eval framework (retrieval events, benchmarks).

**Phase 3 will cover:** Skills (SKILL.md), hooks (SessionStart/End), platform wrappers (.claude-plugin, .copilot-plugin, etc.), TUI dashboard formatting, user commands, README.
