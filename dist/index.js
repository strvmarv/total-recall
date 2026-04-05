#!/usr/bin/env node

// src/index.ts
import { randomUUID as randomUUID6 } from "crypto";

// src/config.ts
import { readFileSync, writeFileSync, existsSync, mkdirSync } from "fs";
import { join } from "path";
import { parse as parseToml, stringify as stringifyToml } from "@iarna/toml";
var DEFAULTS_PATH = new URL("./defaults.toml", import.meta.url);
function getDataDir() {
  return process.env.TOTAL_RECALL_HOME ?? join(process.env.HOME ?? "~", ".total-recall");
}
function loadConfig() {
  const defaultsText = readFileSync(DEFAULTS_PATH, "utf-8");
  const defaults = parseToml(defaultsText);
  const userConfigPath = join(getDataDir(), "config.toml");
  if (existsSync(userConfigPath)) {
    const userText = readFileSync(userConfigPath, "utf-8");
    const userConfig = parseToml(userText);
    return deepMerge(defaults, userConfig);
  }
  return defaults;
}
function setNestedKey(obj, dotKey, value) {
  const result = { ...obj };
  const parts = dotKey.split(".");
  let current = result;
  for (let i = 0; i < parts.length - 1; i++) {
    const part = parts[i];
    if (typeof current[part] !== "object" || current[part] === null) {
      current[part] = {};
    } else {
      current[part] = { ...current[part] };
    }
    current = current[part];
  }
  current[parts[parts.length - 1]] = value;
  return result;
}
function saveUserConfig(overrides) {
  const dataDir = getDataDir();
  mkdirSync(dataDir, { recursive: true });
  const configPath = join(dataDir, "config.toml");
  let existing = {};
  if (existsSync(configPath)) {
    existing = parseToml(readFileSync(configPath, "utf-8"));
  }
  const merged = deepMerge(existing, overrides);
  writeFileSync(configPath, stringifyToml(merged));
}
function deepMerge(target, source) {
  const result = { ...target };
  for (const key of Object.keys(source)) {
    if (source[key] !== null && typeof source[key] === "object" && !Array.isArray(source[key]) && typeof target[key] === "object" && target[key] !== null) {
      result[key] = deepMerge(
        target[key],
        source[key]
      );
    } else {
      result[key] = source[key];
    }
  }
  return result;
}

// src/db/connection.ts
import Database from "better-sqlite3";
import { mkdirSync as mkdirSync2, existsSync as existsSync2 } from "fs";
import { join as join2 } from "path";
import * as sqliteVec from "sqlite-vec";

// src/types.ts
function tableName(tier, type) {
  const typeStr = type === "memory" ? "memories" : "knowledge";
  return `${tier}_${typeStr}`;
}
function vecTableName(tier, type) {
  return `${tableName(tier, type)}_vec`;
}
var ALL_TABLE_PAIRS = [
  { tier: "hot", type: "memory" },
  { tier: "hot", type: "knowledge" },
  { tier: "warm", type: "memory" },
  { tier: "warm", type: "knowledge" },
  { tier: "cold", type: "memory" },
  { tier: "cold", type: "knowledge" }
];

// src/db/schema.ts
function contentTableDDL(name) {
  return `
    CREATE TABLE IF NOT EXISTS ${name} (
      id                TEXT PRIMARY KEY NOT NULL,
      content           TEXT NOT NULL,
      summary           TEXT,
      source            TEXT,
      source_tool       TEXT,
      project           TEXT,
      tags              TEXT DEFAULT '[]',
      created_at        INTEGER NOT NULL,
      updated_at        INTEGER NOT NULL,
      last_accessed_at  INTEGER NOT NULL,
      access_count      INTEGER DEFAULT 0,
      decay_score       REAL DEFAULT 1.0,
      parent_id         TEXT,
      collection_id     TEXT,
      metadata          TEXT DEFAULT '{}'
    )
  `;
}
function contentTableIndexes(name) {
  return [
    `CREATE INDEX IF NOT EXISTS idx_${name}_project         ON ${name}(project)`,
    `CREATE INDEX IF NOT EXISTS idx_${name}_decay_score     ON ${name}(decay_score)`,
    `CREATE INDEX IF NOT EXISTS idx_${name}_last_accessed   ON ${name}(last_accessed_at)`,
    `CREATE INDEX IF NOT EXISTS idx_${name}_parent_id       ON ${name}(parent_id)`,
    `CREATE INDEX IF NOT EXISTS idx_${name}_collection_id   ON ${name}(collection_id)`
  ];
}
var SYSTEM_TABLE_DDLS = [
  `CREATE TABLE IF NOT EXISTS retrieval_events (
    id                      TEXT PRIMARY KEY NOT NULL,
    timestamp               INTEGER NOT NULL,
    session_id              TEXT NOT NULL,
    query_text              TEXT NOT NULL,
    query_source            TEXT NOT NULL,
    query_embedding         BLOB,
    results                 TEXT NOT NULL DEFAULT '[]',
    result_count            INTEGER NOT NULL DEFAULT 0,
    top_score               REAL,
    top_tier                TEXT,
    top_content_type        TEXT,
    outcome_used            INTEGER,
    outcome_signal          TEXT,
    config_snapshot_id      TEXT NOT NULL,
    latency_ms              INTEGER,
    tiers_searched          TEXT NOT NULL DEFAULT '[]',
    total_candidates_scanned INTEGER
  )`,
  `CREATE TABLE IF NOT EXISTS compaction_log (
    id                  TEXT PRIMARY KEY NOT NULL,
    timestamp           INTEGER NOT NULL,
    session_id          TEXT,
    source_tier         TEXT NOT NULL,
    target_tier         TEXT,
    source_entry_ids    TEXT NOT NULL DEFAULT '[]',
    target_entry_id     TEXT,
    semantic_drift      REAL,
    facts_preserved     INTEGER,
    facts_in_original   INTEGER,
    preservation_ratio  REAL,
    decay_scores        TEXT NOT NULL DEFAULT '[]',
    reason              TEXT NOT NULL,
    config_snapshot_id  TEXT NOT NULL
  )`,
  `CREATE TABLE IF NOT EXISTS config_snapshots (
    id        TEXT PRIMARY KEY NOT NULL,
    name      TEXT,
    timestamp INTEGER NOT NULL,
    config    TEXT NOT NULL
  )`,
  `CREATE TABLE IF NOT EXISTS import_log (
    id              TEXT PRIMARY KEY NOT NULL,
    timestamp       INTEGER NOT NULL,
    source_tool     TEXT NOT NULL,
    source_path     TEXT NOT NULL,
    content_hash    TEXT NOT NULL,
    target_entry_id TEXT NOT NULL,
    target_tier     TEXT NOT NULL,
    target_type     TEXT NOT NULL
  )`
];
var SYSTEM_TABLE_INDEXES = [
  `CREATE INDEX IF NOT EXISTS idx_retrieval_events_timestamp   ON retrieval_events(timestamp)`,
  `CREATE INDEX IF NOT EXISTS idx_retrieval_events_session_id  ON retrieval_events(session_id)`,
  `CREATE INDEX IF NOT EXISTS idx_compaction_log_timestamp     ON compaction_log(timestamp)`,
  `CREATE INDEX IF NOT EXISTS idx_compaction_log_source_tier   ON compaction_log(source_tier)`,
  `CREATE INDEX IF NOT EXISTS idx_import_log_content_hash      ON import_log(content_hash)`,
  `CREATE INDEX IF NOT EXISTS idx_import_log_source_tool       ON import_log(source_tool)`
];
var SCHEMA_VERSION_DDL = `
  CREATE TABLE IF NOT EXISTS _schema_version (
    version    INTEGER NOT NULL,
    applied_at INTEGER NOT NULL
  )
`;
var MIGRATIONS = [
  // Migration 1: Initial schema (v1)
  (db) => {
    for (const pair of ALL_TABLE_PAIRS) {
      const tbl = tableName(pair.tier, pair.type);
      const vecTbl = vecTableName(pair.tier, pair.type);
      db.prepare(contentTableDDL(tbl)).run();
      db.prepare(
        `CREATE VIRTUAL TABLE IF NOT EXISTS ${vecTbl} USING vec0(embedding float[384])`
      ).run();
      for (const idx of contentTableIndexes(tbl)) {
        db.prepare(idx).run();
      }
    }
    for (const ddl of SYSTEM_TABLE_DDLS) {
      db.prepare(ddl).run();
    }
    for (const idx of SYSTEM_TABLE_INDEXES) {
      db.prepare(idx).run();
    }
  }
  // Add future migrations here as (db) => { ... }
];
function getCurrentVersion(db) {
  const hasTable = db.prepare("SELECT name FROM sqlite_master WHERE type='table' AND name='_schema_version'").get();
  if (!hasTable) return 0;
  const row = db.prepare("SELECT MAX(version) as v FROM _schema_version").get();
  return row?.v ?? 0;
}
function initSchema(db) {
  db.pragma("journal_mode = WAL");
  db.pragma("foreign_keys = ON");
  const migrate = db.transaction(() => {
    db.prepare(SCHEMA_VERSION_DDL).run();
    const currentVersion = getCurrentVersion(db);
    for (let i = currentVersion; i < MIGRATIONS.length; i++) {
      MIGRATIONS[i](db);
      db.prepare("INSERT INTO _schema_version (version, applied_at) VALUES (?, ?)").run(
        i + 1,
        Date.now()
      );
    }
  });
  migrate();
}

// src/db/connection.ts
var _db = null;
function getDb() {
  if (_db) return _db;
  const dataDir = getDataDir();
  if (!existsSync2(dataDir)) {
    mkdirSync2(dataDir, { recursive: true });
  }
  const dbPath = join2(dataDir, "total-recall.db");
  _db = new Database(dbPath);
  sqliteVec.load(_db);
  initSchema(_db);
  return _db;
}
function closeDb() {
  if (_db) {
    _db.close();
    _db = null;
  }
}

// src/embedding/embedder.ts
import { readFile } from "fs/promises";
import { join as join4 } from "path";
import * as ort from "onnxruntime-node";

// src/embedding/model-manager.ts
import { existsSync as existsSync3, mkdirSync as mkdirSync3, readdirSync } from "fs";
import { readFileSync as readFileSync2, statSync } from "fs";
import { writeFile } from "fs/promises";
import { join as join3 } from "path";
var HF_BASE_URL = "https://huggingface.co";
var HF_REVISION = "main";
function getBundledModelPath(modelName) {
  const distDir = new URL(".", import.meta.url).pathname;
  return join3(distDir, "..", "models", modelName);
}
function getUserModelPath(modelName) {
  return join3(getDataDir(), "models", modelName);
}
function getModelPath(modelName) {
  const bundled = getBundledModelPath(modelName);
  if (isModelDownloaded(bundled)) return bundled;
  return getUserModelPath(modelName);
}
function isModelDownloaded(modelPath) {
  if (!existsSync3(modelPath)) return false;
  try {
    const files = readdirSync(modelPath);
    return files.some((f) => f.endsWith(".onnx"));
  } catch {
    return false;
  }
}
async function validateDownload(modelPath) {
  const modelStat = statSync(join3(modelPath, "model.onnx"));
  if (modelStat.size < 1e6) {
    throw new Error("model.onnx appears corrupted (< 1MB)");
  }
  const tokenizerText = readFileSync2(join3(modelPath, "tokenizer.json"), "utf-8");
  try {
    JSON.parse(tokenizerText);
  } catch {
    throw new Error("tokenizer.json is not valid JSON");
  }
}
async function downloadModel(modelName) {
  const modelPath = getUserModelPath(modelName);
  mkdirSync3(modelPath, { recursive: true });
  const fileUrls = [
    {
      file: "model.onnx",
      url: `${HF_BASE_URL}/sentence-transformers/${modelName}/resolve/${HF_REVISION}/onnx/model.onnx`
    },
    {
      file: "tokenizer.json",
      url: `${HF_BASE_URL}/sentence-transformers/${modelName}/resolve/${HF_REVISION}/tokenizer.json`
    },
    {
      file: "tokenizer_config.json",
      url: `${HF_BASE_URL}/sentence-transformers/${modelName}/resolve/${HF_REVISION}/tokenizer_config.json`
    }
  ];
  for (const { file, url } of fileUrls) {
    const dest = join3(modelPath, file);
    const response = await fetch(url);
    if (!response.ok) {
      throw new Error(
        `Failed to download ${file} from ${url}: ${response.status} ${response.statusText}`
      );
    }
    const buffer = await response.arrayBuffer();
    if (buffer.byteLength === 0) {
      throw new Error(`Downloaded ${file} is empty`);
    }
    await writeFile(dest, Buffer.from(buffer));
  }
  await validateDownload(modelPath);
  return modelPath;
}

// src/embedding/embedder.ts
var CLS_TOKEN_ID = 101;
var SEP_TOKEN_ID = 102;
var UNK_TOKEN_ID = 100;
var MAX_SEQ_LEN = 512;
var Embedder = class {
  options;
  session = null;
  vocab = null;
  constructor(options) {
    this.options = options;
  }
  isLoaded() {
    return this.session !== null && this.vocab !== null;
  }
  async ensureLoaded() {
    if (this.isLoaded()) return;
    const modelPath = getModelPath(this.options.model);
    if (!isModelDownloaded(modelPath)) {
      await downloadModel(this.options.model);
    }
    const onnxPath = join4(modelPath, "model.onnx");
    this.session = await ort.InferenceSession.create(onnxPath);
    const tokenizerPath = join4(modelPath, "tokenizer.json");
    const tokenizerText = await readFile(tokenizerPath, "utf-8");
    const tokenizerJson = JSON.parse(tokenizerText);
    this.vocab = tokenizerJson.model.vocab;
  }
  tokenize(text) {
    if (!this.vocab) throw new Error("Tokenizer not loaded");
    const words = text.toLowerCase().split(/\s+/).filter(Boolean);
    const ids = [CLS_TOKEN_ID];
    for (const word of words) {
      const id = this.vocab[word] ?? UNK_TOKEN_ID;
      ids.push(id);
      if (ids.length >= MAX_SEQ_LEN - 1) break;
    }
    ids.push(SEP_TOKEN_ID);
    return ids;
  }
  async embed(text) {
    await this.ensureLoaded();
    if (!this.session) throw new Error("Session not loaded");
    const inputIds = this.tokenize(text);
    const seqLen = inputIds.length;
    const inputIdsTensor = new ort.Tensor(
      "int64",
      BigInt64Array.from(inputIds.map(BigInt)),
      [1, seqLen]
    );
    const attentionMask = new ort.Tensor(
      "int64",
      BigInt64Array.from(new Array(seqLen).fill(1n)),
      [1, seqLen]
    );
    const tokenTypeIds = new ort.Tensor(
      "int64",
      BigInt64Array.from(new Array(seqLen).fill(0n)),
      [1, seqLen]
    );
    const feeds = {
      input_ids: inputIdsTensor,
      attention_mask: attentionMask,
      token_type_ids: tokenTypeIds
    };
    const results = await this.session.run(feeds);
    const outputKey = Object.keys(results)[0];
    if (!outputKey) throw new Error("No output from model");
    const output = results[outputKey];
    if (!output) throw new Error("Output tensor is undefined");
    const hiddenSize = this.options.dimensions;
    const data = output.data;
    const pooled = new Float32Array(hiddenSize);
    for (let i = 0; i < seqLen; i++) {
      for (let j = 0; j < hiddenSize; j++) {
        pooled[j] = pooled[j] + (data[i * hiddenSize + j] ?? 0) / seqLen;
      }
    }
    let norm = 0;
    for (let i = 0; i < hiddenSize; i++) norm += pooled[i] * pooled[i];
    norm = Math.sqrt(norm);
    if (norm > 0) {
      for (let i = 0; i < hiddenSize; i++) pooled[i] = pooled[i] / norm;
    }
    return pooled;
  }
  async embedBatch(texts) {
    const results = [];
    for (const text of texts) {
      results.push(await this.embed(text));
    }
    return results;
  }
  /**
   * Deterministic embedding based on tokenization only (no ONNX inference).
   * Used as fallback when async embed cannot be awaited synchronously.
   * Requires ensureLoaded() to have been called.
   */
  deterministicEmbed(text) {
    const tokenIds = this.tokenize(text);
    const hiddenSize = this.options.dimensions;
    const vec = new Float32Array(hiddenSize);
    for (let i = 0; i < tokenIds.length; i++) {
      const tokenId = tokenIds[i];
      for (let j = 0; j < hiddenSize; j++) {
        const h = Math.sin(tokenId * (j + 1) / hiddenSize);
        vec[j] = vec[j] + h / tokenIds.length;
      }
    }
    let norm = 0;
    for (let i = 0; i < hiddenSize; i++) norm += vec[i] * vec[i];
    norm = Math.sqrt(norm);
    if (norm > 0) {
      for (let i = 0; i < hiddenSize; i++) vec[i] = vec[i] / norm;
    }
    return vec;
  }
};

// src/tools/registry.ts
import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { CallToolRequestSchema, ListToolsRequestSchema } from "@modelcontextprotocol/sdk/types.js";

// src/db/entries.ts
import { randomUUID } from "crypto";
function rowToEntry(row) {
  return {
    id: row.id,
    content: row.content,
    summary: row.summary,
    source: row.source,
    source_tool: row.source_tool,
    project: row.project,
    tags: row.tags ? JSON.parse(row.tags) : [],
    created_at: row.created_at,
    updated_at: row.updated_at,
    last_accessed_at: row.last_accessed_at,
    access_count: row.access_count,
    decay_score: row.decay_score,
    parent_id: row.parent_id,
    collection_id: row.collection_id,
    metadata: row.metadata ? JSON.parse(row.metadata) : {}
  };
}
function insertEntry(db, tier, type, opts) {
  const table = tableName(tier, type);
  const id = randomUUID();
  const now = Date.now();
  db.prepare(`
    INSERT INTO ${table}
      (id, content, summary, source, source_tool, project, tags,
       created_at, updated_at, last_accessed_at, access_count,
       decay_score, parent_id, collection_id, metadata)
    VALUES
      (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
  `).run(
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
    0,
    1,
    opts.parent_id ?? null,
    opts.collection_id ?? null,
    JSON.stringify(opts.metadata ?? {})
  );
  return id;
}
function getEntry(db, tier, type, id) {
  const table = tableName(tier, type);
  const row = db.prepare(`SELECT * FROM ${table} WHERE id = ?`).get(id);
  if (!row) return null;
  return rowToEntry(row);
}
function updateEntry(db, tier, type, id, opts) {
  const table = tableName(tier, type);
  const now = Date.now();
  const setClauses = ["updated_at = ?"];
  const values = [now];
  if (opts.content !== void 0) {
    setClauses.push("content = ?");
    values.push(opts.content);
  }
  if (opts.summary !== void 0) {
    setClauses.push("summary = ?");
    values.push(opts.summary);
  }
  if (opts.tags !== void 0) {
    setClauses.push("tags = ?");
    values.push(JSON.stringify(opts.tags));
  }
  if (opts.project !== void 0) {
    setClauses.push("project = ?");
    values.push(opts.project);
  }
  if (opts.decay_score !== void 0) {
    setClauses.push("decay_score = ?");
    values.push(opts.decay_score);
  }
  if (opts.metadata !== void 0) {
    setClauses.push("metadata = ?");
    values.push(JSON.stringify(opts.metadata));
  }
  if (opts.touch) {
    setClauses.push("access_count = access_count + 1");
    setClauses.push("last_accessed_at = ?");
    values.push(now);
  }
  values.push(id);
  db.prepare(`UPDATE ${table} SET ${setClauses.join(", ")} WHERE id = ?`).run(...values);
}
function deleteEntry(db, tier, type, id) {
  const table = tableName(tier, type);
  db.prepare(`DELETE FROM ${table} WHERE id = ?`).run(id);
}
var ALLOWED_ORDER_COLUMNS = /* @__PURE__ */ new Set([
  "created_at",
  "updated_at",
  "last_accessed_at",
  "access_count",
  "decay_score",
  "content"
]);
function listEntries(db, tier, type, opts) {
  const table = tableName(tier, type);
  const orderParts = (opts?.orderBy ?? "created_at DESC").split(" ");
  const column = orderParts[0];
  const direction = orderParts[1]?.toUpperCase() === "ASC" ? "ASC" : "DESC";
  if (!ALLOWED_ORDER_COLUMNS.has(column)) {
    throw new Error(`Invalid orderBy column: ${column}`);
  }
  const orderBy = `${column} ${direction}`;
  let sql;
  let params;
  if (opts?.project !== void 0 && opts.project !== null) {
    if (opts.includeGlobal) {
      sql = `SELECT * FROM ${table} WHERE project = ? OR project IS NULL ORDER BY ${orderBy}`;
      params = [opts.project];
    } else {
      sql = `SELECT * FROM ${table} WHERE project = ? ORDER BY ${orderBy}`;
      params = [opts.project];
    }
  } else {
    sql = `SELECT * FROM ${table} ORDER BY ${orderBy}`;
    params = [];
  }
  if (opts?.limit !== void 0) {
    sql += " LIMIT ?";
    params.push(opts.limit);
  }
  const rows = db.prepare(sql).all(...params);
  return rows.map(rowToEntry);
}
function countEntries(db, tier, type) {
  const table = tableName(tier, type);
  const row = db.prepare(`SELECT COUNT(*) as count FROM ${table}`).get();
  return row.count;
}
function moveEntry(db, fromTier, fromType, toTier, toType, id) {
  const doMove = db.transaction(() => {
    const entry = getEntry(db, fromTier, fromType, id);
    if (!entry) {
      throw new Error(`Entry ${id} not found in ${tableName(fromTier, fromType)}`);
    }
    const toTable = tableName(toTier, toType);
    const now = Date.now();
    db.prepare(`
      INSERT INTO ${toTable}
        (id, content, summary, source, source_tool, project, tags,
         created_at, updated_at, last_accessed_at, access_count,
         decay_score, parent_id, collection_id, metadata)
      VALUES
        (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
    `).run(
      entry.id,
      entry.content,
      entry.summary,
      entry.source,
      entry.source_tool,
      entry.project,
      JSON.stringify(entry.tags),
      entry.created_at,
      now,
      entry.last_accessed_at,
      entry.access_count,
      entry.decay_score,
      entry.parent_id,
      entry.collection_id,
      JSON.stringify(entry.metadata)
    );
    deleteEntry(db, fromTier, fromType, id);
  });
  doMove();
}

// src/search/vector-search.ts
function insertEmbedding(db, tier, type, entryId, embedding) {
  const contentTable = tableName(tier, type);
  const vecTable = vecTableName(tier, type);
  const row = db.prepare(`SELECT rowid FROM ${contentTable} WHERE id = ?`).get(entryId);
  if (!row) {
    throw new Error(`Entry ${entryId} not found in ${contentTable}`);
  }
  db.prepare(`INSERT INTO ${vecTable} (rowid, embedding) VALUES (?, ?)`).run(
    BigInt(row.rowid),
    Buffer.from(embedding.buffer)
  );
}
function deleteEmbedding(db, tier, type, entryId) {
  const contentTable = tableName(tier, type);
  const vecTable = vecTableName(tier, type);
  const row = db.prepare(`SELECT rowid FROM ${contentTable} WHERE id = ?`).get(entryId);
  if (!row) return;
  db.prepare(`DELETE FROM ${vecTable} WHERE rowid = ?`).run(BigInt(row.rowid));
}
function searchByVector(db, tier, type, queryVec, opts) {
  const contentTable = tableName(tier, type);
  const vecTable = vecTableName(tier, type);
  const oversample = opts.topK * 2;
  const rows = db.prepare(
    `SELECT c.id, v.distance as dist
       FROM ${vecTable} v
       INNER JOIN ${contentTable} c ON c.rowid = v.rowid
       WHERE v.embedding MATCH ? AND k = ?
       ORDER BY v.distance ASC`
  ).all(Buffer.from(queryVec.buffer), oversample);
  let results = rows.map((r) => ({
    id: r.id,
    score: 1 - r.dist
  }));
  if (opts.minScore !== void 0) {
    results = results.filter((r) => r.score >= opts.minScore);
  }
  return results.slice(0, opts.topK);
}

// src/memory/store.ts
async function storeMemory(db, embed, opts) {
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
    metadata: opts.type ? { entry_type: opts.type } : {}
  });
  const embedding = await embed(opts.content);
  insertEmbedding(db, tier, contentType, id, embedding);
  return id;
}

// src/memory/search.ts
async function searchMemory(db, embed, query, opts) {
  const queryVec = await embed(query);
  const merged = [];
  for (const { tier, content_type } of opts.tiers) {
    const vectorResults = searchByVector(db, tier, content_type, queryVec, {
      topK: opts.topK,
      minScore: opts.minScore
    });
    for (const vr of vectorResults) {
      const entry = getEntry(db, tier, content_type, vr.id);
      if (!entry) continue;
      updateEntry(db, tier, content_type, vr.id, { touch: true });
      merged.push({
        entry,
        tier,
        content_type,
        score: vr.score,
        rank: 0
      });
    }
  }
  merged.sort((a, b) => b.score - a.score);
  const topK = merged.slice(0, opts.topK);
  topK.forEach((r, i) => {
    r.rank = i + 1;
  });
  return topK;
}

// src/memory/get.ts
function getMemory(db, id) {
  for (const { tier, type } of ALL_TABLE_PAIRS) {
    const entry = getEntry(db, tier, type, id);
    if (entry) {
      return { entry, tier, content_type: type };
    }
  }
  return null;
}

// src/memory/update.ts
async function updateMemory(db, embed, id, opts) {
  const location = getMemory(db, id);
  if (!location) return false;
  const { tier, content_type } = location;
  updateEntry(db, tier, content_type, id, opts);
  if (opts.content !== void 0 && embed !== void 0) {
    deleteEmbedding(db, tier, content_type, id);
    const newEmbedding = await embed(opts.content);
    insertEmbedding(db, tier, content_type, id, newEmbedding);
  }
  return true;
}

// src/memory/delete.ts
function deleteMemory(db, id) {
  const location = getMemory(db, id);
  if (!location) return false;
  deleteEmbedding(db, location.tier, location.content_type, id);
  deleteEntry(db, location.tier, location.content_type, id);
  return true;
}

// src/memory/promote-demote.ts
async function promoteEntry(db, embed, id, fromTier, fromType, toTier, toType) {
  const entry = getEntry(db, fromTier, fromType, id);
  if (!entry) {
    throw new Error(`Entry ${id} not found in ${fromTier}/${fromType}`);
  }
  deleteEmbedding(db, fromTier, fromType, id);
  moveEntry(db, fromTier, fromType, toTier, toType, id);
  const newEmbedding = await embed(entry.content);
  insertEmbedding(db, toTier, toType, id, newEmbedding);
}
async function demoteEntry(db, embed, id, fromTier, fromType, toTier, toType) {
  await promoteEntry(db, embed, id, fromTier, fromType, toTier, toType);
}

// src/tools/validation.ts
import { resolve } from "path";
var VALID_TIERS = /* @__PURE__ */ new Set(["hot", "warm", "cold"]);
var VALID_CONTENT_TYPES = /* @__PURE__ */ new Set(["memory", "knowledge"]);
var VALID_ENTRY_TYPES = /* @__PURE__ */ new Set(["correction", "preference", "decision", "surfaced"]);
var MAX_CONTENT_LENGTH = 1e5;
function validateString(value, name) {
  if (typeof value !== "string" || value.length === 0) {
    throw new Error(`${name} must be a non-empty string`);
  }
  return value;
}
function validateOptionalString(value, name) {
  if (value === void 0 || value === null) return void 0;
  if (typeof value !== "string") throw new Error(`${name} must be a string`);
  return value;
}
function validateTier(value) {
  if (!VALID_TIERS.has(value)) {
    throw new Error(`Invalid tier: ${String(value)}. Must be hot, warm, or cold`);
  }
  return value;
}
function validateContentType(value) {
  if (!VALID_CONTENT_TYPES.has(value)) {
    throw new Error(`Invalid content type: ${String(value)}. Must be memory or knowledge`);
  }
  return value;
}
function validateEntryType(value) {
  if (value === void 0 || value === null) return void 0;
  if (!VALID_ENTRY_TYPES.has(value)) {
    throw new Error(`Invalid entry type: ${String(value)}`);
  }
  return value;
}
function validateContent(value) {
  const content = validateString(value, "content");
  if (content.length > MAX_CONTENT_LENGTH) {
    throw new Error(`Content exceeds maximum length of ${MAX_CONTENT_LENGTH} characters`);
  }
  return content;
}
function validateNumber(value, name, min, max) {
  if (typeof value !== "number" || isNaN(value)) {
    throw new Error(`${name} must be a number`);
  }
  if (min !== void 0 && value < min) throw new Error(`${name} must be >= ${min}`);
  if (max !== void 0 && value > max) throw new Error(`${name} must be <= ${max}`);
  return value;
}
function validateOptionalNumber(value, name, min, max) {
  if (value === void 0 || value === null) return void 0;
  return validateNumber(value, name, min, max);
}
function validateTags(value) {
  if (value === void 0 || value === null) return [];
  if (!Array.isArray(value)) throw new Error("tags must be an array");
  return value.map((v, i) => {
    if (typeof v !== "string") throw new Error(`tags[${i}] must be a string`);
    return v;
  });
}
function validatePath(value, name) {
  const path = validateString(value, name);
  const resolved = resolve(path);
  const dangerous = ["/etc", "/proc", "/sys", "/dev", "/var/run", "/root"];
  for (const prefix of dangerous) {
    if (resolved.startsWith(prefix)) {
      throw new Error(`Access denied: ${name} cannot access ${prefix}`);
    }
  }
  const basename3 = resolved.split("/").pop() ?? "";
  if (basename3 === ".env" || basename3 === ".credentials.json") {
    throw new Error(`Access denied: ${name} cannot access sensitive files`);
  }
  return resolved;
}

// src/tools/memory-tools.ts
var MEMORY_TOOLS = [
  {
    name: "memory_store",
    description: "Store a new memory or knowledge entry",
    inputSchema: {
      type: "object",
      properties: {
        content: { type: "string", description: "The content to store" },
        tier: { type: "string", enum: ["hot", "warm", "cold"], description: "Storage tier (default: hot)" },
        contentType: { type: "string", enum: ["memory", "knowledge"], description: "Content type (default: memory)" },
        entryType: {
          type: "string",
          enum: ["correction", "preference", "decision", "surfaced", "imported", "compacted", "ingested"],
          description: "Entry type"
        },
        project: { type: "string", description: "Project scope" },
        tags: { type: "array", items: { type: "string" }, description: "Tags" },
        source: { type: "string", description: "Source identifier" }
      },
      required: ["content"]
    }
  },
  {
    name: "memory_search",
    description: "Search memories and knowledge using semantic similarity",
    inputSchema: {
      type: "object",
      properties: {
        query: { type: "string", description: "Search query" },
        topK: { type: "number", description: "Number of results to return (default: 10)" },
        minScore: { type: "number", description: "Minimum similarity score (0-1)" },
        tiers: {
          type: "array",
          items: { type: "string", enum: ["hot", "warm", "cold"] },
          description: "Tiers to search (default: all)"
        },
        contentTypes: {
          type: "array",
          items: { type: "string", enum: ["memory", "knowledge"] },
          description: "Content types to search (default: all)"
        }
      },
      required: ["query"]
    }
  },
  {
    name: "memory_get",
    description: "Retrieve a specific memory entry by ID",
    inputSchema: {
      type: "object",
      properties: {
        id: { type: "string", description: "Entry ID" }
      },
      required: ["id"]
    }
  },
  {
    name: "memory_update",
    description: "Update an existing memory entry",
    inputSchema: {
      type: "object",
      properties: {
        id: { type: "string", description: "Entry ID" },
        content: { type: "string", description: "New content" },
        summary: { type: "string", description: "New summary" },
        tags: { type: "array", items: { type: "string" }, description: "New tags" },
        project: { type: "string", description: "New project" }
      },
      required: ["id"]
    }
  },
  {
    name: "memory_delete",
    description: "Delete a memory entry by ID",
    inputSchema: {
      type: "object",
      properties: {
        id: { type: "string", description: "Entry ID to delete" }
      },
      required: ["id"]
    }
  },
  {
    name: "memory_promote",
    description: "Promote a memory entry to a higher tier",
    inputSchema: {
      type: "object",
      properties: {
        id: { type: "string", description: "Entry ID" },
        toTier: { type: "string", enum: ["hot", "warm", "cold"], description: "Target tier" },
        toType: { type: "string", enum: ["memory", "knowledge"], description: "Target content type" }
      },
      required: ["id", "toTier", "toType"]
    }
  },
  {
    name: "memory_demote",
    description: "Demote a memory entry to a lower tier",
    inputSchema: {
      type: "object",
      properties: {
        id: { type: "string", description: "Entry ID" },
        toTier: { type: "string", enum: ["hot", "warm", "cold"], description: "Target tier" },
        toType: { type: "string", enum: ["memory", "knowledge"], description: "Target content type" }
      },
      required: ["id", "toTier", "toType"]
    }
  }
];
async function handleMemoryTool(name, args, ctx) {
  if (name === "memory_store") {
    const content = validateContent(args.content);
    const tier = args.tier !== void 0 ? validateTier(args.tier) : "hot";
    const contentType = args.contentType !== void 0 ? validateContentType(args.contentType) : "memory";
    const type = validateEntryType(args.entryType);
    const project = validateOptionalString(args.project, "project");
    const tags = validateTags(args.tags);
    const source = validateOptionalString(args.source, "source");
    await ctx.embedder.ensureLoaded();
    const vec = await ctx.embedder.embed(content);
    const embedFn = () => vec;
    const id = await storeMemory(ctx.db, embedFn, {
      content,
      tier,
      contentType,
      type,
      project,
      tags,
      source
    });
    return { content: [{ type: "text", text: JSON.stringify({ id }) }] };
  }
  if (name === "memory_search") {
    const query = validateString(args.query, "query");
    const topK = validateOptionalNumber(args.topK, "topK", 1, 1e3) ?? 10;
    const minScore = validateOptionalNumber(args.minScore, "minScore", 0, 1);
    await ctx.embedder.ensureLoaded();
    const vec = await ctx.embedder.embed(query);
    const embedFn = () => vec;
    const tierFilter = args.tiers;
    const typeFilter = args.contentTypes;
    const tiers = ALL_TABLE_PAIRS.filter(
      (p) => (!tierFilter || tierFilter.includes(p.tier)) && (!typeFilter || typeFilter.includes(p.type))
    ).map((p) => ({ tier: p.tier, content_type: p.type }));
    const results = await searchMemory(ctx.db, embedFn, query, {
      tiers,
      topK,
      minScore
    });
    return { content: [{ type: "text", text: JSON.stringify(results) }] };
  }
  if (name === "memory_get") {
    const id = validateString(args.id, "id");
    const location = getMemory(ctx.db, id);
    return { content: [{ type: "text", text: JSON.stringify(location) }] };
  }
  if (name === "memory_update") {
    const id = validateString(args.id, "id");
    await ctx.embedder.ensureLoaded();
    const newContent = args.content !== void 0 ? validateContent(args.content) : void 0;
    const summary = validateOptionalString(args.summary, "summary");
    const tags = args.tags !== void 0 ? validateTags(args.tags) : void 0;
    const project = validateOptionalString(args.project, "project");
    let embedFn;
    if (newContent !== void 0) {
      const vec = await ctx.embedder.embed(newContent);
      embedFn = () => vec;
    }
    const updated = await updateMemory(ctx.db, embedFn, id, {
      content: newContent,
      summary,
      tags,
      project
    });
    return { content: [{ type: "text", text: JSON.stringify({ updated }) }] };
  }
  if (name === "memory_delete") {
    const id = validateString(args.id, "id");
    const deleted = deleteMemory(ctx.db, id);
    return { content: [{ type: "text", text: JSON.stringify({ deleted }) }] };
  }
  if (name === "memory_promote") {
    const id = validateString(args.id, "id");
    const toTier = validateTier(args.toTier);
    const toType = validateContentType(args.toType);
    const location = getMemory(ctx.db, id);
    if (!location) {
      return { content: [{ type: "text", text: JSON.stringify({ error: "Entry not found" }) }] };
    }
    await ctx.embedder.ensureLoaded();
    const vec = await ctx.embedder.embed(location.entry.content);
    const embedFn = () => vec;
    await promoteEntry(
      ctx.db,
      embedFn,
      id,
      location.tier,
      location.content_type,
      toTier,
      toType
    );
    return { content: [{ type: "text", text: JSON.stringify({ promoted: true }) }] };
  }
  if (name === "memory_demote") {
    const id = validateString(args.id, "id");
    const toTier = validateTier(args.toTier);
    const toType = validateContentType(args.toType);
    const location = getMemory(ctx.db, id);
    if (!location) {
      return { content: [{ type: "text", text: JSON.stringify({ error: "Entry not found" }) }] };
    }
    await ctx.embedder.ensureLoaded();
    const vec = await ctx.embedder.embed(location.entry.content);
    const embedFn = () => vec;
    await demoteEntry(
      ctx.db,
      embedFn,
      id,
      location.tier,
      location.content_type,
      toTier,
      toType
    );
    return { content: [{ type: "text", text: JSON.stringify({ demoted: true }) }] };
  }
  return null;
}

// src/tools/system-tools.ts
import { statSync as statSync2 } from "fs";
import { join as join5 } from "path";

// src/ingestion/hierarchical-index.ts
function rowToEntry2(row) {
  return {
    id: row.id,
    content: row.content,
    summary: row.summary,
    source: row.source,
    source_tool: row.source_tool,
    project: row.project,
    tags: row.tags ? JSON.parse(row.tags) : [],
    created_at: row.created_at,
    updated_at: row.updated_at,
    last_accessed_at: row.last_accessed_at,
    access_count: row.access_count,
    decay_score: row.decay_score,
    parent_id: row.parent_id,
    collection_id: row.collection_id,
    metadata: row.metadata ? JSON.parse(row.metadata) : {}
  };
}
async function createCollection(db, embed, opts) {
  const content = `Collection: ${opts.name}`;
  const id = insertEntry(db, "cold", "knowledge", {
    content,
    source: opts.sourcePath,
    metadata: {
      type: "collection",
      name: opts.name,
      source_path: opts.sourcePath
    }
  });
  const embedding = await embed(content);
  insertEmbedding(db, "cold", "knowledge", id, embedding);
  return id;
}
async function addDocumentToCollection(db, embed, opts) {
  const joined = opts.chunks.map((c) => c.content).join("\n\n");
  const docContent = joined.slice(0, 500);
  const docId = insertEntry(db, "cold", "knowledge", {
    content: docContent,
    source: opts.sourcePath,
    collection_id: opts.collectionId,
    metadata: {
      type: "document",
      source_path: opts.sourcePath,
      chunk_count: opts.chunks.length
    }
  });
  const docEmbedding = await embed(docContent);
  insertEmbedding(db, "cold", "knowledge", docId, docEmbedding);
  for (const chunk of opts.chunks) {
    const chunkId = insertEntry(db, "cold", "knowledge", {
      content: chunk.content,
      source: opts.sourcePath,
      parent_id: docId,
      collection_id: opts.collectionId,
      metadata: {
        type: "chunk",
        heading_path: chunk.headingPath,
        name: chunk.name,
        kind: chunk.kind
      }
    });
    const chunkEmbedding = await embed(chunk.content);
    insertEmbedding(db, "cold", "knowledge", chunkId, chunkEmbedding);
  }
  return docId;
}
function listCollections(db) {
  const rows = db.prepare(`SELECT * FROM cold_knowledge WHERE json_extract(metadata, '$.type') = 'collection'`).all();
  return rows.map((row) => {
    const entry = rowToEntry2(row);
    const metadata = entry.metadata;
    return { ...entry, name: metadata["name"] };
  });
}

// src/eval/event-logger.ts
import { randomUUID as randomUUID2 } from "crypto";
function getRetrievalEvents(db, opts = {}) {
  const conditions = [];
  const params = [];
  if (opts.sessionId !== void 0) {
    conditions.push("session_id = ?");
    params.push(opts.sessionId);
  }
  if (opts.configSnapshotId !== void 0) {
    conditions.push("config_snapshot_id = ?");
    params.push(opts.configSnapshotId);
  }
  if (opts.days !== void 0) {
    const cutoff = Date.now() - opts.days * 24 * 60 * 60 * 1e3;
    conditions.push("timestamp >= ?");
    params.push(cutoff);
  }
  let sql = "SELECT * FROM retrieval_events";
  if (conditions.length > 0) {
    sql += " WHERE " + conditions.join(" AND ");
  }
  sql += " ORDER BY timestamp DESC";
  if (opts.limit !== void 0) {
    sql += " LIMIT ?";
    params.push(opts.limit);
  }
  return db.prepare(sql).all(...params);
}

// src/tools/system-tools.ts
var SYSTEM_TOOLS = [
  {
    name: "status",
    description: "Get the status of the total-recall memory system",
    inputSchema: {
      type: "object",
      properties: {},
      required: []
    }
  },
  {
    name: "config_get",
    description: "Get a configuration value by dot-notation key",
    inputSchema: {
      type: "object",
      properties: {
        key: { type: "string", description: "Dot-notation config key (e.g. 'tiers.hot.max_entries'). Omit for full config." }
      },
      required: []
    }
  },
  {
    name: "config_set",
    description: "Set a configuration value and persist to ~/.total-recall/config.toml",
    inputSchema: {
      type: "object",
      properties: {
        key: { type: "string", description: "Dot-notation config key" },
        value: { description: "Value to set" }
      },
      required: ["key", "value"]
    }
  }
];
function handleSystemTool(name, args, ctx) {
  if (name === "status") {
    const tierSizes = {};
    for (const { tier, type } of ALL_TABLE_PAIRS) {
      const key = `${tier}_${type === "memory" ? "memories" : "knowledge"}`;
      tierSizes[key] = countEntries(ctx.db, tier, type);
    }
    const dataDir = getDataDir();
    const dbPath = join5(dataDir, "total-recall.db");
    let dbSizeBytes = null;
    try {
      dbSizeBytes = statSync2(dbPath).size;
    } catch {
    }
    const collections = listCollections(ctx.db);
    const kbCollections = collections.map((c) => ({
      id: c.id,
      name: c.name
    }));
    const totalKbEntries = countEntries(ctx.db, "cold", "knowledge");
    const totalChunks = totalKbEntries - kbCollections.length;
    const embeddingModel = ctx.config.embedding.model;
    const embeddingDimensions = ctx.config.embedding.dimensions;
    const recentEvents = getRetrievalEvents(ctx.db, { days: 7 });
    const totalRetrievals = recentEvents.length;
    const avgTopScore = recentEvents.length > 0 ? recentEvents.reduce((sum, e) => sum + (e.top_score ?? 0), 0) / recentEvents.length : null;
    const outcomes = recentEvents.filter((e) => e.outcome_signal !== null);
    const positiveOutcomes = outcomes.filter((e) => e.outcome_signal === "positive").length;
    const negativeOutcomes = outcomes.filter((e) => e.outcome_signal === "negative").length;
    const lastCompaction = ctx.db.prepare(`SELECT * FROM compaction_log ORDER BY timestamp DESC LIMIT 1`).get();
    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            tierSizes,
            knowledgeBase: {
              collections: kbCollections,
              totalChunks
            },
            db: {
              path: dbPath,
              sizeBytes: dbSizeBytes,
              sessionId: ctx.sessionId
            },
            embedding: {
              model: embeddingModel,
              dimensions: embeddingDimensions
            },
            activity: {
              retrievals7d: totalRetrievals,
              avgTopScore7d: avgTopScore !== null ? Math.round(avgTopScore * 1e3) / 1e3 : null,
              positiveOutcomes7d: positiveOutcomes,
              negativeOutcomes7d: negativeOutcomes
            },
            lastCompaction: lastCompaction ? {
              timestamp: lastCompaction.timestamp,
              from: lastCompaction.source_tier,
              to: lastCompaction.target_tier,
              reason: lastCompaction.reason
            } : null
          })
        }
      ]
    };
  }
  if (name === "config_get") {
    const key = args.key;
    if (!key) {
      return { content: [{ type: "text", text: JSON.stringify(ctx.config) }] };
    }
    const parts = key.split(".");
    let value = ctx.config;
    for (const part of parts) {
      if (value === null || typeof value !== "object") return { content: [{ type: "text", text: JSON.stringify({ error: "key not found" }) }] };
      if (!Object.prototype.hasOwnProperty.call(value, part)) return { content: [{ type: "text", text: JSON.stringify({ error: `key not found: ${key}` }) }] };
      value = value[part];
    }
    return { content: [{ type: "text", text: JSON.stringify({ key, value }) }] };
  }
  if (name === "config_set") {
    const key = args.key;
    const value = args.value;
    const overrides = setNestedKey({}, key, value);
    saveUserConfig(overrides);
    const refreshed = loadConfig();
    Object.assign(ctx.config, refreshed);
    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({ key, value, persisted: true })
        }
      ]
    };
  }
  return null;
}

// src/tools/kb-tools.ts
import { statSync as statSync4 } from "fs";

// src/ingestion/ingest.ts
import { readFileSync as readFileSync3, readdirSync as readdirSync2, statSync as statSync3 } from "fs";
import { join as join6, dirname, basename, extname } from "path";

// src/ingestion/markdown-parser.ts
function estimateTokens(text) {
  const wordCount = text.trim().split(/\s+/).filter(Boolean).length;
  return Math.ceil(wordCount * 0.75);
}
function parseMarkdown(text, opts) {
  if (!text || !text.trim()) return [];
  const { maxTokens } = opts;
  const allLines = text.split("\n");
  const sections = [];
  let currentHeadingPath = [];
  let currentLines = [];
  let currentStartLine = 1;
  const headingRe = /^(#{1,6})\s+(.+)$/;
  function flushSection() {
    if (currentLines.length > 0) {
      sections.push({
        headingPath: [...currentHeadingPath],
        lines: currentLines,
        startLine: currentStartLine
      });
    }
  }
  for (let i = 0; i < allLines.length; i++) {
    const line = allLines[i];
    const match = headingRe.exec(line);
    if (match) {
      flushSection();
      const level = match[1].length;
      const title = match[2].trim();
      currentHeadingPath = currentHeadingPath.slice(0, level - 1);
      currentHeadingPath[level - 1] = title;
      currentLines = [line];
      currentStartLine = i + 1;
    } else {
      currentLines.push(line);
    }
  }
  flushSection();
  const chunks = [];
  for (const section of sections) {
    const sectionText = section.lines.join("\n");
    if (estimateTokens(sectionText) <= maxTokens) {
      chunks.push({
        content: sectionText,
        headingPath: section.headingPath,
        startLine: section.startLine,
        endLine: section.startLine + section.lines.length - 1
      });
    } else {
      const subChunks = splitSection(section, maxTokens);
      chunks.push(...subChunks);
    }
  }
  return chunks;
}
function splitSection(section, maxTokens) {
  const { headingPath, lines, startLine } = section;
  const blocks = [];
  let i = 0;
  const codeFenceRe = /^```/;
  while (i < lines.length) {
    const line = lines[i];
    if (codeFenceRe.test(line)) {
      const blockLines = [line];
      const offset = i;
      i++;
      while (i < lines.length) {
        const inner = lines[i];
        blockLines.push(inner);
        i++;
        if (/^```\s*$/.test(inner)) break;
      }
      blocks.push({ lines: blockLines, lineOffset: offset });
    } else {
      const blockLines = [];
      const offset = i;
      while (i < lines.length && !/^```/.test(lines[i])) {
        blockLines.push(lines[i]);
        i++;
        if (blockLines[blockLines.length - 1].trim() === "") break;
      }
      if (blockLines.length > 0) {
        blocks.push({ lines: blockLines, lineOffset: offset });
      }
    }
  }
  const chunks = [];
  let currentBlockLines = [];
  let currentOffset = 0;
  function flushChunk() {
    if (currentBlockLines.length === 0) return;
    const content = currentBlockLines.join("\n");
    chunks.push({
      content,
      headingPath,
      startLine: startLine + currentOffset,
      endLine: startLine + currentOffset + currentBlockLines.length - 1
    });
    currentBlockLines = [];
  }
  for (const block of blocks) {
    const blockText = block.lines.join("\n");
    const blockTokens = estimateTokens(blockText);
    const currentTokens = estimateTokens(currentBlockLines.join("\n"));
    if (currentBlockLines.length === 0) {
      currentBlockLines = [...block.lines];
      currentOffset = block.lineOffset;
    } else if (currentTokens + blockTokens <= maxTokens) {
      currentBlockLines.push(...block.lines);
    } else {
      flushChunk();
      currentBlockLines = [...block.lines];
      currentOffset = block.lineOffset;
    }
  }
  flushChunk();
  return chunks;
}

// src/ingestion/code-parser.ts
function estimateTokens2(text) {
  const wordCount = text.trim().split(/\s+/).filter(Boolean).length;
  return Math.ceil(wordCount * 0.75);
}
var PATTERNS = {
  typescript: {
    boundary: /^(export\s+)?(async\s+)?function\s+\w+|^(export\s+)?(abstract\s+)?class\s+\w+|^(export\s+)?const\s+\w+\s*=\s*(async\s+)?\(|^(export\s+)?const\s+\w+\s*=\s*(async\s+)?function/,
    importLine: /^\s*import\s/,
    extractName(line) {
      const m = /function\s+(\w+)/.exec(line) || /class\s+(\w+)/.exec(line) || /const\s+(\w+)/.exec(line);
      return m ? m[1] : "";
    },
    classifyKind(line) {
      if (/class\s+/.test(line)) return "class";
      if (/function\s+|=\s*(async\s+)?\(|=\s*(async\s+)?function/.test(line)) return "function";
      return "block";
    }
  },
  javascript: {
    boundary: /^(export\s+)?(async\s+)?function\s+\w+|^(export\s+)?(class)\s+\w+|^(export\s+)?const\s+\w+\s*=\s*(async\s+)?\(|^(export\s+)?const\s+\w+\s*=\s*(async\s+)?function/,
    importLine: /^\s*import\s|^\s*const\s+\w+\s*=\s*require\(/,
    extractName(line) {
      const m = /function\s+(\w+)/.exec(line) || /class\s+(\w+)/.exec(line) || /const\s+(\w+)/.exec(line);
      return m ? m[1] : "";
    },
    classifyKind(line) {
      if (/class\s+/.test(line)) return "class";
      if (/function\s+|=\s*(async\s+)?\(|=\s*(async\s+)?function/.test(line)) return "function";
      return "block";
    }
  },
  python: {
    boundary: /^(async\s+)?def\s+\w+|^class\s+\w+/,
    importLine: /^\s*import\s|^\s*from\s+\S+\s+import\s/,
    extractName(line) {
      const m = /(?:def|class)\s+(\w+)/.exec(line);
      return m ? m[1] : "";
    },
    classifyKind(line) {
      if (/^class\s+/.test(line)) return "class";
      if (/(?:async\s+)?def\s+/.test(line)) return "function";
      return "block";
    }
  },
  go: {
    boundary: /^func\s+/,
    importLine: /^\s*import\s|^\s*"[\w/]+"/,
    extractName(line) {
      const m = /func\s+(?:\(\w+\s+\*?\w+\)\s+)?(\w+)/.exec(line);
      return m ? m[1] : "";
    },
    classifyKind(_line) {
      return "function";
    }
  },
  rust: {
    boundary: /^(pub\s+)?(async\s+)?fn\s+\w+|^(pub\s+)?struct\s+\w+|^(pub\s+)?impl\s+\w+/,
    importLine: /^\s*use\s/,
    extractName(line) {
      const m = /fn\s+(\w+)/.exec(line) || /struct\s+(\w+)/.exec(line) || /impl\s+(\w+)/.exec(line);
      return m ? m[1] : "";
    },
    classifyKind(line) {
      if (/struct\s+/.test(line) || /impl\s+/.test(line)) return "class";
      if (/fn\s+/.test(line)) return "function";
      return "block";
    }
  }
};
function parseCode(code, language, opts) {
  if (!code || !code.trim()) return [];
  const patterns = PATTERNS[language] ?? PATTERNS["typescript"];
  const { maxTokens } = opts;
  const lines = code.split("\n");
  const importLines = [];
  const nonImportStartIdx = findNonImportStart(lines, patterns);
  for (let i = 0; i < nonImportStartIdx; i++) {
    importLines.push({ line: lines[i], lineIdx: i });
  }
  const segments = [];
  let currentLines = [];
  let currentStart = nonImportStartIdx;
  let currentName = "";
  let currentKind = "block";
  function flushSegment() {
    if (currentLines.length > 0 && currentLines.some((l) => l.trim())) {
      segments.push({
        lines: currentLines,
        startIdx: currentStart,
        name: currentName,
        kind: currentKind
      });
    }
  }
  for (let i = nonImportStartIdx; i < lines.length; i++) {
    const line = lines[i];
    if (patterns.boundary.test(line)) {
      flushSegment();
      currentLines = [line];
      currentStart = i;
      currentName = patterns.extractName(line);
      currentKind = patterns.classifyKind(line);
    } else {
      currentLines.push(line);
    }
  }
  flushSegment();
  const chunks = [];
  if (importLines.length > 0) {
    const content = importLines.map((l) => l.line).join("\n");
    chunks.push({
      content,
      name: "imports",
      kind: "import",
      startLine: 1,
      endLine: importLines.length
    });
  }
  for (const seg of segments) {
    const segText = seg.lines.join("\n");
    if (estimateTokens2(segText) <= maxTokens) {
      chunks.push({
        content: segText,
        name: seg.name,
        kind: seg.kind,
        startLine: seg.startIdx + 1,
        endLine: seg.startIdx + seg.lines.length
      });
    } else {
      const subChunks = splitAtBlankLines(seg.lines, seg.startIdx, seg.name, seg.kind, maxTokens);
      chunks.push(...subChunks);
    }
  }
  return chunks;
}
function findNonImportStart(lines, patterns) {
  let lastImportOrBlank = 0;
  let seenImport = false;
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (line.trim() === "") {
      if (seenImport) lastImportOrBlank = i + 1;
      continue;
    }
    if (patterns.importLine.test(line)) {
      seenImport = true;
      lastImportOrBlank = i + 1;
    } else {
      break;
    }
  }
  return lastImportOrBlank;
}
function splitAtBlankLines(lines, startIdx, name, kind, maxTokens) {
  const chunks = [];
  let currentLines = [];
  let currentOffset = 0;
  function flush() {
    if (currentLines.length > 0 && currentLines.some((l) => l.trim())) {
      chunks.push({
        content: currentLines.join("\n"),
        name,
        kind,
        startLine: startIdx + currentOffset + 1,
        endLine: startIdx + currentOffset + currentLines.length
      });
    }
    currentLines = [];
  }
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    currentLines.push(line);
    if (line.trim() === "") {
      const tokens = estimateTokens2(currentLines.join("\n"));
      if (tokens >= maxTokens) {
        flush();
        currentOffset = i + 1;
      }
    }
  }
  flush();
  return chunks.length > 0 ? chunks : [{
    content: lines.join("\n"),
    name,
    kind,
    startLine: startIdx + 1,
    endLine: startIdx + lines.length
  }];
}

// src/ingestion/chunker.ts
var MARKDOWN_EXTENSIONS = /* @__PURE__ */ new Set([".md", ".mdx", ".markdown"]);
var CODE_LANGUAGE_MAP = {
  ".ts": "typescript",
  ".tsx": "typescript",
  ".js": "javascript",
  ".jsx": "javascript",
  ".py": "python",
  ".go": "go",
  ".rs": "rust"
};
function getExtension(filePath) {
  const base = filePath.split("/").pop() ?? filePath;
  const dotIdx = base.lastIndexOf(".");
  if (dotIdx === -1) return "";
  return base.slice(dotIdx).toLowerCase();
}
function estimateTokens3(text) {
  const wordCount = text.trim().split(/\s+/).filter(Boolean).length;
  return Math.ceil(wordCount * 0.75);
}
function splitByParagraphs(content, maxTokens) {
  const paragraphs = content.split(/\n\n+/);
  const chunks = [];
  let currentParts = [];
  let lineOffset = 1;
  let currentStartLine = 1;
  let lineCount = 1;
  for (const para of paragraphs) {
    const paraLines = para.split("\n").length;
    const paraTokens = estimateTokens3(para);
    const currentTokens = estimateTokens3(currentParts.join("\n\n"));
    if (currentParts.length === 0) {
      currentParts.push(para);
      currentStartLine = lineCount;
    } else if (currentTokens + paraTokens <= maxTokens) {
      currentParts.push(para);
    } else {
      const content2 = currentParts.join("\n\n");
      const contentLines = content2.split("\n").length;
      chunks.push({
        content: content2,
        startLine: currentStartLine,
        endLine: currentStartLine + contentLines - 1
      });
      currentParts = [para];
      currentStartLine = lineCount;
    }
    lineCount += paraLines + 1;
    lineOffset = lineCount;
  }
  if (currentParts.length > 0) {
    const content2 = currentParts.join("\n\n");
    const contentLines = content2.split("\n").length;
    chunks.push({
      content: content2,
      startLine: currentStartLine,
      endLine: currentStartLine + contentLines - 1
    });
  }
  return chunks;
}
function chunkFile(content, filePath, opts) {
  if (!content || !content.trim()) return [];
  const ext = getExtension(filePath);
  if (MARKDOWN_EXTENSIONS.has(ext)) {
    const mdChunks = parseMarkdown(content, opts);
    return mdChunks.map((c) => ({
      content: c.content,
      headingPath: c.headingPath,
      startLine: c.startLine,
      endLine: c.endLine
    }));
  }
  const language = CODE_LANGUAGE_MAP[ext];
  if (language !== void 0) {
    const codeChunks = parseCode(content, language, opts);
    return codeChunks.map((c) => ({
      content: c.content,
      name: c.name,
      kind: c.kind,
      startLine: c.startLine,
      endLine: c.endLine
    }));
  }
  return splitByParagraphs(content, opts.maxTokens);
}

// src/ingestion/ingest.ts
var INGESTABLE_EXTENSIONS = /* @__PURE__ */ new Set([
  ".md",
  ".mdx",
  ".markdown",
  ".txt",
  ".rst",
  ".ts",
  ".tsx",
  ".js",
  ".jsx",
  ".py",
  ".go",
  ".rs",
  ".java",
  ".kt",
  ".cs",
  ".cpp",
  ".c",
  ".h",
  ".json",
  ".yaml",
  ".yml",
  ".toml"
]);
async function ingestFile(db, embed, filePath, collectionId) {
  const content = readFileSync3(filePath, "utf-8");
  const chunks = chunkFile(content, filePath, { maxTokens: 512, overlapTokens: 50 });
  let resolvedCollectionId = collectionId;
  if (!resolvedCollectionId) {
    const dirPath = dirname(filePath);
    const dirName = basename(dirPath);
    resolvedCollectionId = await createCollection(db, embed, {
      name: dirName,
      sourcePath: dirPath
    });
  }
  const documentId = await addDocumentToCollection(db, embed, {
    collectionId: resolvedCollectionId,
    sourcePath: filePath,
    chunks: chunks.map((c) => ({
      content: c.content,
      headingPath: c.headingPath,
      name: c.name,
      kind: c.kind
    }))
  });
  let validationPassed = false;
  if (chunks.length > 0) {
    const firstChunk = chunks[0];
    const queryVec = await embed(firstChunk.content);
    const results = searchByVector(db, "cold", "knowledge", queryVec, {
      topK: 5,
      minScore: 0
    });
    validationPassed = results.some((r) => r.score > 0.5);
  }
  return {
    documentId,
    chunkCount: chunks.length,
    validationPassed
  };
}
function matchesGlob(filename, glob) {
  if (glob.startsWith("*.")) {
    const ext = glob.slice(1);
    return filename.endsWith(ext);
  }
  return filename === glob;
}
function walkDirectory(dirPath) {
  const files = [];
  let entries;
  try {
    entries = readdirSync2(dirPath);
  } catch {
    return files;
  }
  for (const entry of entries) {
    if (entry.startsWith(".") || entry === "node_modules") continue;
    const fullPath = join6(dirPath, entry);
    let stat;
    try {
      stat = statSync3(fullPath);
    } catch {
      continue;
    }
    if (stat.isDirectory()) {
      files.push(...walkDirectory(fullPath));
    } else if (stat.isFile()) {
      const ext = extname(entry).toLowerCase();
      if (INGESTABLE_EXTENSIONS.has(ext)) {
        files.push(fullPath);
      }
    }
  }
  return files;
}
async function ingestDirectory(db, embed, dirPath, glob) {
  const dirName = basename(dirPath);
  const collectionId = await createCollection(db, embed, {
    name: dirName,
    sourcePath: dirPath
  });
  const files = walkDirectory(dirPath);
  let documentCount = 0;
  let totalChunks = 0;
  const errors = [];
  for (const filePath of files) {
    if (glob !== void 0) {
      const name = basename(filePath);
      if (!matchesGlob(name, glob)) continue;
    }
    try {
      const result = await ingestFile(db, embed, filePath, collectionId);
      documentCount++;
      totalChunks += result.chunkCount;
    } catch (err) {
      errors.push(`${filePath}: ${err instanceof Error ? err.message : String(err)}`);
    }
  }
  return {
    collectionId,
    documentCount,
    totalChunks,
    errors
  };
}

// src/tools/kb-tools.ts
var KB_TOOLS = [
  {
    name: "kb_ingest_file",
    description: "Ingest a single file into the knowledge base",
    inputSchema: {
      type: "object",
      properties: {
        path: { type: "string", description: "Path to the file to ingest" },
        collection: { type: "string", description: "Optional collection ID to add to" }
      },
      required: ["path"]
    }
  },
  {
    name: "kb_ingest_dir",
    description: "Ingest a directory of files into the knowledge base",
    inputSchema: {
      type: "object",
      properties: {
        path: { type: "string", description: "Path to the directory to ingest" },
        glob: { type: "string", description: "Optional glob pattern to filter files" },
        collection: { type: "string", description: "Optional collection name override" }
      },
      required: ["path"]
    }
  },
  {
    name: "kb_search",
    description: "Search the knowledge base (cold/knowledge tier)",
    inputSchema: {
      type: "object",
      properties: {
        query: { type: "string", description: "Search query" },
        collection: { type: "string", description: "Optional collection ID to restrict search" },
        top_k: { type: "number", description: "Number of results to return (default: 10)" }
      },
      required: ["query"]
    }
  },
  {
    name: "kb_list_collections",
    description: "List all knowledge base collections",
    inputSchema: {
      type: "object",
      properties: {},
      required: []
    }
  },
  {
    name: "kb_remove",
    description: "Remove an entry from the knowledge base",
    inputSchema: {
      type: "object",
      properties: {
        id: { type: "string", description: "Entry ID to remove" },
        cascade: { type: "boolean", description: "If true, also delete child entries" }
      },
      required: ["id"]
    }
  },
  {
    name: "kb_refresh",
    description: "Refresh a knowledge base collection (re-ingest)",
    inputSchema: {
      type: "object",
      properties: {
        collection: { type: "string", description: "Collection ID to refresh" }
      },
      required: ["collection"]
    }
  }
];
async function handleKbTool(name, args, ctx) {
  if (name === "kb_ingest_file") {
    const filePath = validatePath(args.path, "path");
    const collectionId = validateOptionalString(args.collection, "collection");
    await ctx.embedder.ensureLoaded();
    const embedFn = (text) => ctx.embedder.embed(text);
    const result = await ingestFile(ctx.db, embedFn, filePath, collectionId);
    return { content: [{ type: "text", text: JSON.stringify(result) }] };
  }
  if (name === "kb_ingest_dir") {
    const dirPath = validatePath(args.path, "path");
    const glob = validateOptionalString(args.glob, "glob");
    await ctx.embedder.ensureLoaded();
    const embedFn = (text) => ctx.embedder.embed(text);
    const result = await ingestDirectory(ctx.db, embedFn, dirPath, glob);
    return { content: [{ type: "text", text: JSON.stringify(result) }] };
  }
  if (name === "kb_search") {
    const query = validateString(args.query, "query");
    const topK = validateOptionalNumber(args.top_k, "top_k", 1, 1e3) ?? 10;
    await ctx.embedder.ensureLoaded();
    const vec = await ctx.embedder.embed(query);
    const embedFn = () => vec;
    const results = await searchMemory(ctx.db, embedFn, query, {
      tiers: [{ tier: "cold", content_type: "knowledge" }],
      topK
    });
    return { content: [{ type: "text", text: JSON.stringify(results) }] };
  }
  if (name === "kb_list_collections") {
    const collections = listCollections(ctx.db);
    return { content: [{ type: "text", text: JSON.stringify(collections) }] };
  }
  if (name === "kb_remove") {
    const id = validateString(args.id, "id");
    const cascade = args.cascade ?? false;
    if (cascade) {
      const children = listEntries(ctx.db, "cold", "knowledge").filter(
        (e) => e.parent_id === id || e.collection_id === id
      );
      for (const child of children) {
        deleteEmbedding(ctx.db, "cold", "knowledge", child.id);
        deleteEntry(ctx.db, "cold", "knowledge", child.id);
      }
    }
    deleteEmbedding(ctx.db, "cold", "knowledge", id);
    deleteEntry(ctx.db, "cold", "knowledge", id);
    return { content: [{ type: "text", text: JSON.stringify({ removed: id, cascade }) }] };
  }
  if (name === "kb_refresh") {
    const collectionId = validateString(args.collection, "collection");
    const collectionEntry = getEntry(ctx.db, "cold", "knowledge", collectionId);
    if (!collectionEntry) {
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify({ error: `Collection not found: ${collectionId}` })
          }
        ]
      };
    }
    const sourcePath = collectionEntry.metadata?.source_path;
    if (!sourcePath) {
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify({ error: "Collection has no source_path in metadata; cannot refresh" })
          }
        ]
      };
    }
    const children = listEntries(ctx.db, "cold", "knowledge").filter(
      (e) => e.collection_id === collectionId || e.parent_id === collectionId
    );
    for (const child of children) {
      deleteEmbedding(ctx.db, "cold", "knowledge", child.id);
      deleteEntry(ctx.db, "cold", "knowledge", child.id);
    }
    deleteEmbedding(ctx.db, "cold", "knowledge", collectionId);
    deleteEntry(ctx.db, "cold", "knowledge", collectionId);
    await ctx.embedder.ensureLoaded();
    const embedFn = (text) => ctx.embedder.embed(text);
    let result;
    let isDir = false;
    try {
      isDir = statSync4(sourcePath).isDirectory();
    } catch (err) {
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify({ error: `Cannot stat source path: ${String(err)}` })
          }
        ]
      };
    }
    if (isDir) {
      result = await ingestDirectory(ctx.db, embedFn, sourcePath);
    } else {
      result = await ingestFile(ctx.db, embedFn, sourcePath);
    }
    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            refreshed: true,
            source_path: sourcePath,
            deleted_children: children.length,
            ...result
          })
        }
      ]
    };
  }
  return null;
}
function registerKbTools() {
  return KB_TOOLS;
}

// src/tools/eval-tools.ts
import { resolve as resolve2 } from "path";
import { fileURLToPath } from "url";

// src/eval/benchmark-runner.ts
import { readFileSync as readFileSync4 } from "fs";
async function runBenchmark(db, embed, opts) {
  const corpusLines = readFileSync4(opts.corpusPath, "utf-8").split("\n").filter((line) => line.trim().length > 0);
  const existingWarmCount = countEntries(db, "warm", "memory");
  if (existingWarmCount < corpusLines.length) {
    for (const line of corpusLines) {
      const entry = JSON.parse(line);
      await storeMemory(db, embed, {
        content: entry.content,
        type: entry.type,
        tier: "warm",
        contentType: "memory",
        tags: entry.tags
      });
    }
  }
  const benchmarkLines = readFileSync4(opts.benchmarkPath, "utf-8").split("\n").filter((line) => line.trim().length > 0);
  const queries = benchmarkLines.map((line) => JSON.parse(line));
  const details = [];
  let exactMatches = 0;
  let fuzzyMatches = 0;
  let tierMatches = 0;
  let totalLatencyMs = 0;
  for (const bq of queries) {
    const start = performance.now();
    const results = await searchMemory(db, embed, bq.query, {
      tiers: [{ tier: "warm", content_type: "memory" }],
      topK: 3
    });
    const latencyMs = performance.now() - start;
    totalLatencyMs += latencyMs;
    const topResult = results[0] ?? null;
    const topContent = topResult?.entry.content ?? null;
    const topScore = topResult?.score ?? 0;
    const topTier = topResult?.tier ?? null;
    const matched = topContent !== null && topContent.includes(bq.expected_content_contains);
    const fuzzyMatched = matched || results.slice(1).some((r) => r.entry.content.includes(bq.expected_content_contains));
    const tierRouted = topTier === bq.expected_tier;
    if (matched) exactMatches++;
    if (fuzzyMatched) fuzzyMatches++;
    if (tierRouted) tierMatches++;
    details.push({
      query: bq.query,
      expectedContains: bq.expected_content_contains,
      topResult: topContent,
      topScore,
      matched,
      fuzzyMatched
    });
  }
  const total = queries.length;
  return {
    totalQueries: total,
    exactMatchRate: total > 0 ? exactMatches / total : 0,
    fuzzyMatchRate: total > 0 ? fuzzyMatches / total : 0,
    tierRoutingRate: total > 0 ? tierMatches / total : 0,
    avgLatencyMs: total > 0 ? totalLatencyMs / total : 0,
    details
  };
}

// src/eval/metrics.ts
function computeGroupMetrics(events) {
  const withOutcome = events.filter((e) => e.outcome_used !== null);
  const used = withOutcome.filter((e) => e.outcome_used === 1);
  const precision = withOutcome.length > 0 ? used.length / withOutcome.length : 0;
  const hitEvents = events.filter((e) => e.outcome_used !== null && e.outcome_used === 1);
  const eventsWithOutcome = events.filter((e) => e.outcome_used !== null);
  const hitRate = eventsWithOutcome.length > 0 ? hitEvents.length / eventsWithOutcome.length : 0;
  const scoresWithValue = events.filter((e) => e.top_score !== null);
  const avgScore = scoresWithValue.length > 0 ? scoresWithValue.reduce((sum, e) => sum + e.top_score, 0) / scoresWithValue.length : 0;
  return { precision, hitRate, avgScore };
}
function computeMetrics(events, similarityThreshold, compactionRows = []) {
  if (events.length === 0) {
    return {
      precision: 0,
      hitRate: 0,
      missRate: 0,
      mrr: 0,
      avgLatencyMs: 0,
      totalEvents: 0,
      byTier: {},
      byContentType: {},
      topMisses: [],
      falsePositives: [],
      compactionHealth: computeCompactionHealth(compactionRows)
    };
  }
  const withOutcome = events.filter((e) => e.outcome_used !== null);
  const usedCount = withOutcome.filter((e) => e.outcome_used === 1).length;
  const precision = withOutcome.length > 0 ? usedCount / withOutcome.length : 0;
  const hitRate = withOutcome.length > 0 ? usedCount / withOutcome.length : 0;
  const missCount = events.filter(
    (e) => e.top_score === null || e.top_score < similarityThreshold
  ).length;
  const missRate = missCount / events.length;
  const mrrSum = withOutcome.reduce((sum, e) => {
    return sum + (e.outcome_used === 1 ? 1 : 0);
  }, 0);
  const mrr = withOutcome.length > 0 ? mrrSum / withOutcome.length : 0;
  const latencies = events.filter((e) => e.latency_ms !== null);
  const avgLatencyMs = latencies.length > 0 ? latencies.reduce((sum, e) => sum + e.latency_ms, 0) / latencies.length : 0;
  const tierMap = /* @__PURE__ */ new Map();
  for (const e of events) {
    if (e.top_tier) {
      const group = tierMap.get(e.top_tier) ?? [];
      group.push(e);
      tierMap.set(e.top_tier, group);
    }
  }
  const byTier = {};
  for (const [tier, group] of tierMap) {
    const { precision: p, hitRate: h, avgScore } = computeGroupMetrics(group);
    byTier[tier] = { precision: p, hitRate: h, avgScore, count: group.length };
  }
  const ctMap = /* @__PURE__ */ new Map();
  for (const e of events) {
    if (e.top_content_type) {
      const group = ctMap.get(e.top_content_type) ?? [];
      group.push(e);
      ctMap.set(e.top_content_type, group);
    }
  }
  const byContentType = {};
  for (const [ct, group] of ctMap) {
    const { precision: p, hitRate: h } = computeGroupMetrics(group);
    byContentType[ct] = { precision: p, hitRate: h, count: group.length };
  }
  const topMisses = events.filter((e) => e.top_score === null || e.top_score < similarityThreshold).sort((a, b) => (a.top_score ?? -1) - (b.top_score ?? -1)).slice(0, 10).map((e) => ({ query: e.query_text, topScore: e.top_score, timestamp: e.timestamp }));
  const falsePositives = events.filter((e) => e.outcome_used === 0 && e.top_score !== null && e.top_score >= similarityThreshold).sort((a, b) => (b.top_score ?? 0) - (a.top_score ?? 0)).slice(0, 10).map((e) => ({ query: e.query_text, topScore: e.top_score, timestamp: e.timestamp }));
  return {
    precision,
    hitRate,
    missRate,
    mrr,
    avgLatencyMs,
    totalEvents: events.length,
    byTier,
    byContentType,
    topMisses,
    falsePositives,
    compactionHealth: computeCompactionHealth(compactionRows)
  };
}
function computeCompactionHealth(rows) {
  const withRatio = rows.filter((r) => r.preservation_ratio !== null);
  const withDrift = rows.filter((r) => r.semantic_drift !== null && r.semantic_drift > 0.2);
  return {
    totalCompactions: rows.length,
    avgPreservationRatio: withRatio.length > 0 ? withRatio.reduce((sum, r) => sum + r.preservation_ratio, 0) / withRatio.length : null,
    entriesWithDrift: withDrift.length
  };
}

// src/tools/eval-tools.ts
var __dirname = fileURLToPath(new URL(".", import.meta.url));
var PACKAGE_ROOT = resolve2(__dirname, "..", "..");
var EVAL_TOOLS = [
  {
    name: "eval_benchmark",
    description: "Run a retrieval benchmark against the eval corpus and benchmark queries",
    inputSchema: {
      type: "object",
      properties: {
        compare_to: { type: "string", description: "Optional baseline snapshot ID to compare against" },
        snapshot: { type: "string", description: "Optional config snapshot ID to tag this run" }
      },
      required: []
    }
  },
  {
    name: "eval_report",
    description: "Generate a retrieval quality report from logged events",
    inputSchema: {
      type: "object",
      properties: {
        days: { type: "number", description: "Number of days of history to include (default: 7)" },
        config_snapshot: { type: "string", description: "Optional config snapshot ID to filter by" }
      },
      required: []
    }
  }
];
async function handleEvalTool(name, args, ctx) {
  if (name === "eval_benchmark") {
    await ctx.embedder.ensureLoaded();
    const embedFn = (text) => ctx.embedder.embed(text);
    const corpusPath = resolve2(PACKAGE_ROOT, "eval", "corpus", "memories.jsonl");
    const benchmarkPath = resolve2(PACKAGE_ROOT, "eval", "benchmarks", "retrieval.jsonl");
    const result = await runBenchmark(ctx.db, embedFn, {
      corpusPath,
      benchmarkPath
    });
    return { content: [{ type: "text", text: JSON.stringify(result) }] };
  }
  if (name === "eval_report") {
    const days = args.days ?? 7;
    const configSnapshot = args.config_snapshot;
    const events = getRetrievalEvents(ctx.db, {
      days,
      configSnapshotId: configSnapshot
    });
    const cutoff = Date.now() - days * 24 * 60 * 60 * 1e3;
    const compactionRows = ctx.db.prepare(`SELECT * FROM compaction_log WHERE timestamp >= ? ORDER BY timestamp DESC`).all(cutoff);
    const similarityThreshold = ctx.config.tiers.warm.similarity_threshold ?? 0.5;
    const metrics = computeMetrics(events, similarityThreshold, compactionRows);
    return { content: [{ type: "text", text: JSON.stringify({ days, events: events.length, metrics }) }] };
  }
  return null;
}
function registerEvalTools() {
  return EVAL_TOOLS;
}

// src/importers/claude-code.ts
import { existsSync as existsSync4, readdirSync as readdirSync3, readFileSync as readFileSync5 } from "fs";
import { join as join7 } from "path";
import { homedir } from "os";
import { createHash } from "crypto";
function parseFrontmatter(raw) {
  const match = raw.match(/^---\n([\s\S]*?)\n---\n([\s\S]*)$/);
  if (!match) return { frontmatter: null, content: raw };
  const frontmatter = {};
  for (const line of match[1].split("\n")) {
    const kv = line.match(/^(\w+):\s*(.*)$/);
    if (kv) {
      const key = kv[1];
      frontmatter[key] = kv[2].trim();
    }
  }
  return { frontmatter, content: match[2] };
}
function contentHash(text) {
  return createHash("sha256").update(text).digest("hex");
}
function importLogId(sourceTool, sourcePath, hash) {
  return createHash("md5").update(`${sourceTool}:${sourcePath}:${hash}`).digest("hex");
}
function isAlreadyImported(db, hash) {
  const row = db.prepare("SELECT id FROM import_log WHERE content_hash = ?").get(hash);
  return row !== void 0;
}
function logImport(db, sourceTool, sourcePath, hash, entryId, tier, type) {
  const id = importLogId(sourceTool, sourcePath, hash);
  db.prepare(`
    INSERT OR IGNORE INTO import_log
      (id, timestamp, source_tool, source_path, content_hash, target_entry_id, target_tier, target_type)
    VALUES (?, ?, ?, ?, ?, ?, ?, ?)
  `).run(id, Date.now(), sourceTool, sourcePath, hash, entryId, tier, type);
}
var ClaudeCodeImporter = class {
  name = "claude-code";
  basePath;
  constructor(basePath) {
    this.basePath = basePath ?? join7(homedir(), ".claude");
  }
  detect() {
    return existsSync4(this.basePath) && existsSync4(join7(this.basePath, "projects"));
  }
  scan() {
    let memoryFiles = 0;
    let knowledgeFiles = 0;
    let sessionFiles = 0;
    const projectsDir = join7(this.basePath, "projects");
    if (!existsSync4(projectsDir)) {
      return { memoryFiles, knowledgeFiles, sessionFiles };
    }
    for (const projectEntry of readdirSync3(projectsDir, { withFileTypes: true })) {
      if (!projectEntry.isDirectory()) continue;
      const projectDir = join7(projectsDir, projectEntry.name);
      const memoryDir = join7(projectDir, "memory");
      if (existsSync4(memoryDir)) {
        for (const f of readdirSync3(memoryDir)) {
          if (f.endsWith(".md") && f !== "MEMORY.md") memoryFiles++;
        }
      }
      if (existsSync4(join7(projectDir, "CLAUDE.md"))) knowledgeFiles++;
      for (const f of readdirSync3(projectDir)) {
        if (f.endsWith(".jsonl")) sessionFiles++;
      }
    }
    return { memoryFiles, knowledgeFiles, sessionFiles };
  }
  async importMemories(db, embed, project) {
    const result = { imported: 0, skipped: 0, errors: [] };
    const projectsDir = join7(this.basePath, "projects");
    if (!existsSync4(projectsDir)) return result;
    for (const projectEntry of readdirSync3(projectsDir, { withFileTypes: true })) {
      if (!projectEntry.isDirectory()) continue;
      const projectDir = join7(projectsDir, projectEntry.name);
      const memoryDir = join7(projectDir, "memory");
      if (!existsSync4(memoryDir)) continue;
      for (const filename of readdirSync3(memoryDir)) {
        if (!filename.endsWith(".md") || filename === "MEMORY.md") continue;
        const filePath = join7(memoryDir, filename);
        try {
          const raw = readFileSync5(filePath, "utf8");
          const hash = contentHash(raw);
          if (isAlreadyImported(db, hash)) {
            result.skipped++;
            continue;
          }
          const { frontmatter, content } = parseFrontmatter(raw);
          let tier = "warm";
          let type = "memory";
          if (frontmatter?.type === "reference") {
            tier = "cold";
            type = "knowledge";
          }
          const entryId = insertEntry(db, tier, type, {
            content,
            summary: frontmatter?.description ?? null,
            source: filePath,
            source_tool: "claude-code",
            project: project ?? null,
            tags: frontmatter?.name ? [frontmatter.name] : []
          });
          insertEmbedding(db, tier, type, entryId, await embed(content));
          logImport(db, "claude-code", filePath, hash, entryId, tier, type);
          result.imported++;
        } catch (err) {
          result.errors.push(`${filePath}: ${err instanceof Error ? err.message : String(err)}`);
        }
      }
    }
    return result;
  }
  async importKnowledge(db, embed) {
    const result = { imported: 0, skipped: 0, errors: [] };
    const claudeMdPath = join7(this.basePath, "CLAUDE.md");
    if (!existsSync4(claudeMdPath)) return result;
    try {
      const raw = readFileSync5(claudeMdPath, "utf8");
      const hash = contentHash(raw);
      if (isAlreadyImported(db, hash)) {
        result.skipped++;
        return result;
      }
      const { content } = parseFrontmatter(raw);
      const entryId = insertEntry(db, "warm", "knowledge", {
        content,
        source: claudeMdPath,
        source_tool: "claude-code",
        tags: ["pinned"]
      });
      insertEmbedding(db, "warm", "knowledge", entryId, await embed(content));
      logImport(db, "claude-code", claudeMdPath, hash, entryId, "warm", "knowledge");
      result.imported++;
    } catch (err) {
      result.errors.push(
        `${claudeMdPath}: ${err instanceof Error ? err.message : String(err)}`
      );
    }
    return result;
  }
};

// src/importers/copilot-cli.ts
import { existsSync as existsSync5, readdirSync as readdirSync4, readFileSync as readFileSync6 } from "fs";
import { join as join8 } from "path";
import { homedir as homedir2 } from "os";
import { createHash as createHash2 } from "crypto";
function contentHash2(text) {
  return createHash2("sha256").update(text).digest("hex");
}
function importLogId2(sourceTool, sourcePath, hash) {
  return createHash2("md5").update(`${sourceTool}:${sourcePath}:${hash}`).digest("hex");
}
function isAlreadyImported2(db, hash) {
  const row = db.prepare("SELECT id FROM import_log WHERE content_hash = ?").get(hash);
  return row !== void 0;
}
function logImport2(db, sourceTool, sourcePath, hash, entryId, tier, type) {
  const id = importLogId2(sourceTool, sourcePath, hash);
  db.prepare(`
    INSERT OR IGNORE INTO import_log
      (id, timestamp, source_tool, source_path, content_hash, target_entry_id, target_tier, target_type)
    VALUES (?, ?, ?, ?, ?, ?, ?, ?)
  `).run(id, Date.now(), sourceTool, sourcePath, hash, entryId, tier, type);
}
var CopilotCliImporter = class {
  name = "copilot-cli";
  basePath;
  constructor(basePath) {
    this.basePath = basePath ?? join8(homedir2(), ".copilot");
  }
  detect() {
    return existsSync5(this.basePath) && existsSync5(join8(this.basePath, "session-state"));
  }
  scan() {
    let knowledgeFiles = 0;
    let sessionFiles = 0;
    const sessionStateDir = join8(this.basePath, "session-state");
    if (!existsSync5(sessionStateDir)) {
      return { memoryFiles: 0, knowledgeFiles, sessionFiles };
    }
    for (const entry of readdirSync4(sessionStateDir, { withFileTypes: true })) {
      if (!entry.isDirectory()) continue;
      const sessionDir = join8(sessionStateDir, entry.name);
      if (existsSync5(join8(sessionDir, "plan.md"))) knowledgeFiles++;
      for (const f of readdirSync4(sessionDir)) {
        if (f.endsWith(".jsonl")) sessionFiles++;
      }
    }
    return { memoryFiles: 0, knowledgeFiles, sessionFiles };
  }
  async importMemories(_db2, _embed, _project) {
    return { imported: 0, skipped: 0, errors: [] };
  }
  async importKnowledge(db, embed) {
    const result = { imported: 0, skipped: 0, errors: [] };
    const sessionStateDir = join8(this.basePath, "session-state");
    if (!existsSync5(sessionStateDir)) return result;
    for (const entry of readdirSync4(sessionStateDir, { withFileTypes: true })) {
      if (!entry.isDirectory()) continue;
      const planPath = join8(sessionStateDir, entry.name, "plan.md");
      if (!existsSync5(planPath)) continue;
      try {
        const raw = readFileSync6(planPath, "utf8");
        const hash = contentHash2(raw);
        if (isAlreadyImported2(db, hash)) {
          result.skipped++;
          continue;
        }
        const entryId = insertEntry(db, "cold", "knowledge", {
          content: raw,
          source: planPath,
          source_tool: "copilot-cli"
        });
        insertEmbedding(db, "cold", "knowledge", entryId, await embed(raw));
        logImport2(db, "copilot-cli", planPath, hash, entryId, "cold", "knowledge");
        result.imported++;
      } catch (err) {
        result.errors.push(`${planPath}: ${err instanceof Error ? err.message : String(err)}`);
      }
    }
    return result;
  }
};

// src/tools/import-tools.ts
var IMPORT_TOOLS = [
  {
    name: "import_host",
    description: "Detect and import memories/knowledge from installed host tools (Claude Code, Copilot CLI)",
    inputSchema: {
      type: "object",
      properties: {
        source: { type: "string", description: "Optional: restrict to a specific source ('claude-code' or 'copilot-cli')" }
      },
      required: []
    }
  }
];
async function handleImportTool(name, args, ctx) {
  if (name === "import_host") {
    const source = validateOptionalString(args.source, "source");
    await ctx.embedder.ensureLoaded();
    const embedFn = (text) => ctx.embedder.embed(text);
    const importers = [
      new ClaudeCodeImporter(),
      new CopilotCliImporter()
    ];
    const results = [];
    for (const importer of importers) {
      if (source && importer.name !== source) continue;
      const detected = importer.detect();
      if (!detected) {
        results.push({ tool: importer.name, detected: false });
        continue;
      }
      const scan = importer.scan();
      const memoriesResult = await importer.importMemories(ctx.db, embedFn);
      const knowledgeResult = await importer.importKnowledge(ctx.db, embedFn);
      results.push({
        tool: importer.name,
        detected: true,
        scan,
        memoriesResult,
        knowledgeResult
      });
    }
    return { content: [{ type: "text", text: JSON.stringify({ results }) }] };
  }
  return null;
}
function registerImportTools() {
  return IMPORT_TOOLS;
}

// src/tools/session-tools.ts
import { randomUUID as randomUUID5 } from "crypto";

// src/compaction/compactor.ts
import { randomUUID as randomUUID3 } from "crypto";

// src/memory/decay.ts
var MS_PER_HOUR = 60 * 60 * 1e3;
var TYPE_WEIGHTS = {
  correction: 1.5,
  preference: 1.3,
  decision: 1,
  surfaced: 0.8,
  imported: 1.1,
  compacted: 1,
  ingested: 0.9
};
function calculateDecayScore(entry, compactionConfig, now = Date.now()) {
  const hoursSinceAccess = (now - entry.last_accessed_at) / MS_PER_HOUR;
  const timeFactor = Math.exp(-hoursSinceAccess / compactionConfig.decay_half_life_hours);
  const freqFactor = 1 + Math.log2(1 + entry.access_count);
  const typeWeight = TYPE_WEIGHTS[entry.type] ?? 1;
  return timeFactor * freqFactor * typeWeight;
}

// src/compaction/compactor.ts
function logCompactionEvent(db, opts) {
  const id = randomUUID3();
  const timestamp = Date.now();
  db.prepare(`
    INSERT INTO compaction_log
      (id, timestamp, session_id, source_tier, target_tier, source_entry_ids,
       target_entry_id, semantic_drift, facts_preserved, facts_in_original,
       preservation_ratio, decay_scores, reason, config_snapshot_id)
    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
  `).run(
    id,
    timestamp,
    opts.sessionId,
    opts.sourceTier,
    opts.targetTier,
    JSON.stringify(opts.sourceEntryIds),
    opts.targetEntryId,
    null,
    null,
    null,
    null,
    JSON.stringify(opts.decayScores),
    opts.reason,
    opts.configSnapshotId
  );
}
async function compactHotTier(db, embed, config, sessionId, configSnapshotId) {
  const snapshotId = configSnapshotId ?? "default";
  const entries = listEntries(db, "hot", "memory");
  const now = Date.now();
  const carryForward = [];
  const promoted = [];
  const discarded = [];
  for (const entry of entries) {
    const entryType = entry.metadata?.entry_type ?? "decision";
    const score = calculateDecayScore(
      {
        last_accessed_at: entry.last_accessed_at,
        created_at: entry.created_at,
        access_count: entry.access_count,
        type: entryType
      },
      config,
      now
    );
    if (score > config.promote_threshold) {
      carryForward.push(entry.id);
    } else if (score >= config.warm_threshold) {
      await promoteEntry(db, embed, entry.id, "hot", "memory", "warm", "memory");
      promoted.push(entry.id);
      logCompactionEvent(db, {
        sessionId,
        sourceTier: "hot",
        targetTier: "warm",
        sourceEntryIds: [entry.id],
        targetEntryId: entry.id,
        decayScores: { [entry.id]: score },
        reason: "decay_score_below_promote_threshold",
        configSnapshotId: snapshotId
      });
    } else {
      deleteEmbedding(db, "hot", "memory", entry.id);
      deleteEntry(db, "hot", "memory", entry.id);
      discarded.push(entry.id);
      logCompactionEvent(db, {
        sessionId,
        sourceTier: "hot",
        targetTier: null,
        sourceEntryIds: [entry.id],
        targetEntryId: null,
        decayScores: { [entry.id]: score },
        reason: "decay_score_below_warm_threshold",
        configSnapshotId: snapshotId
      });
    }
  }
  return { carryForward, promoted, discarded };
}

// src/compaction/warm-sweep.ts
import { randomUUID as randomUUID4 } from "crypto";
async function sweepWarmTier(db, embed, config, sessionId) {
  const entries = listEntries(db, "warm", "memory");
  const now = Date.now();
  const coldDecayMs = config.coldDecayDays * 24 * 60 * 60 * 1e3;
  const demoted = [];
  const kept = [];
  for (const entry of entries) {
    const age = now - entry.last_accessed_at;
    if (age > coldDecayMs && entry.access_count === 0) {
      await demoteEntry(db, embed, entry.id, "warm", "memory", "cold", "memory");
      db.prepare(`
        INSERT INTO compaction_log
          (id, timestamp, session_id, source_tier, target_tier, source_entry_ids,
           target_entry_id, decay_scores, reason, config_snapshot_id)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
      `).run(
        randomUUID4(),
        now,
        sessionId,
        "warm",
        "cold",
        JSON.stringify([entry.id]),
        entry.id,
        JSON.stringify([entry.decay_score]),
        "warm_sweep_decay",
        "default"
      );
      demoted.push(entry.id);
    } else {
      kept.push(entry.id);
    }
  }
  return { demoted, kept };
}

// src/importers/project-docs.ts
import { existsSync as existsSync6, readFileSync as readFileSync7, readdirSync as readdirSync5, statSync as statSync5 } from "fs";
import { join as join9, basename as basename2 } from "path";
import { createHash as createHash3 } from "crypto";
var DOC_FILES = ["README.md", "CONTRIBUTING.md", "CLAUDE.md", "AGENTS.md"];
var DOC_DIRS = ["docs", "doc"];
function contentHash3(content) {
  return createHash3("sha256").update(content).digest("hex");
}
function isAlreadyIngested(db, hash) {
  const row = db.prepare("SELECT id FROM import_log WHERE content_hash = ? AND source_tool = 'project-docs'").get(hash);
  return row !== void 0;
}
function logIngest(db, sourcePath, hash, entryId) {
  const id = createHash3("md5").update(`project-docs:${sourcePath}:${hash}`).digest("hex");
  db.prepare(`
    INSERT OR IGNORE INTO import_log
      (id, timestamp, source_tool, source_path, content_hash, target_entry_id, target_tier, target_type)
    VALUES (?, ?, ?, ?, ?, ?, ?, ?)
  `).run(id, Date.now(), "project-docs", sourcePath, hash, entryId, "cold", "knowledge");
}
async function ingestProjectDocs(db, embed, cwd) {
  const result = { filesIngested: 0, totalChunks: 0, skipped: 0 };
  const collectionName = `${basename2(cwd)}-project-docs`;
  let collectionId = null;
  const filesToIngest = [];
  for (const file of DOC_FILES) {
    const path = join9(cwd, file);
    if (existsSync6(path)) filesToIngest.push(path);
  }
  for (const dir of DOC_DIRS) {
    const dirPath = join9(cwd, dir);
    if (existsSync6(dirPath) && statSync5(dirPath).isDirectory()) {
      collectMarkdownFiles(dirPath, filesToIngest);
    }
  }
  if (filesToIngest.length === 0) return result;
  collectionId = await createCollection(db, embed, {
    name: collectionName,
    sourcePath: cwd
  });
  for (const filePath of filesToIngest) {
    const content = readFileSync7(filePath, "utf-8").trim();
    if (!content) {
      result.skipped++;
      continue;
    }
    const hash = contentHash3(content);
    if (isAlreadyIngested(db, hash)) {
      result.skipped++;
      continue;
    }
    const ingestResult = await ingestFile(db, embed, filePath, collectionId);
    logIngest(db, filePath, hash, collectionId);
    result.filesIngested++;
    result.totalChunks += ingestResult.chunkCount;
  }
  return result;
}
function collectMarkdownFiles(dirPath, files) {
  for (const entry of readdirSync5(dirPath, { withFileTypes: true })) {
    const full = join9(dirPath, entry.name);
    if (entry.isDirectory()) {
      collectMarkdownFiles(full, files);
    } else if (entry.name.endsWith(".md")) {
      files.push(full);
    }
  }
}

// src/tools/session-tools.ts
var SESSION_TOOLS = [
  {
    name: "session_start",
    description: "Initialize a session: sync host tool imports and assemble hot tier context",
    inputSchema: {
      type: "object",
      properties: {},
      required: []
    }
  },
  {
    name: "session_end",
    description: "End a session: compact the hot tier and return compaction results",
    inputSchema: {
      type: "object",
      properties: {},
      required: []
    }
  },
  {
    name: "session_context",
    description: "Return current hot tier entries as formatted context text",
    inputSchema: {
      type: "object",
      properties: {},
      required: []
    }
  }
];
async function handleSessionTool(name, args, ctx) {
  if (name === "session_start") {
    await ctx.embedder.ensureLoaded();
    const embedFn = (text) => ctx.embedder.embed(text);
    const importers = [
      new ClaudeCodeImporter(),
      new CopilotCliImporter()
    ];
    const importSummary = [];
    for (const importer of importers) {
      if (!importer.detect()) continue;
      const memResult = await importer.importMemories(ctx.db, embedFn);
      const kbResult = await importer.importKnowledge(ctx.db, embedFn);
      importSummary.push({
        tool: importer.name,
        memoriesImported: memResult.imported,
        knowledgeImported: kbResult.imported
      });
    }
    let warmSweepResult = null;
    const sweepIntervalMs = ctx.config.compaction.warm_sweep_interval_days * 24 * 60 * 60 * 1e3;
    const lastSweep = ctx.db.prepare(`SELECT MAX(timestamp) as ts FROM compaction_log WHERE reason = 'warm_sweep_decay'`).get();
    const lastSweepTs = lastSweep?.ts ?? 0;
    if (Date.now() - lastSweepTs > sweepIntervalMs) {
      const sessionId = ctx.sessionId ?? randomUUID5();
      const result = await sweepWarmTier(ctx.db, embedFn, {
        coldDecayDays: ctx.config.tiers.warm.cold_decay_days
      }, sessionId);
      if (result.demoted.length > 0) {
        warmSweepResult = { demoted: result.demoted.length };
      }
    }
    let projectDocs = null;
    const docsResult = await ingestProjectDocs(ctx.db, embedFn, process.cwd());
    if (docsResult.filesIngested > 0) {
      projectDocs = { filesIngested: docsResult.filesIngested, totalChunks: docsResult.totalChunks };
    }
    const hotEntries = listEntries(ctx.db, "hot", "memory");
    const contextLines = hotEntries.map((e) => {
      const tags = e.tags.length > 0 ? ` [${e.tags.join(", ")}]` : "";
      return `- ${e.content}${tags}`;
    });
    const contextText = contextLines.join("\n");
    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            sessionId: ctx.sessionId,
            importSummary,
            warmSweep: warmSweepResult,
            projectDocs,
            hotEntryCount: hotEntries.length,
            context: contextText
          })
        }
      ]
    };
  }
  if (name === "session_end") {
    await ctx.embedder.ensureLoaded();
    const embedFn = (text) => ctx.embedder.embed(text);
    const sessionId = ctx.sessionId ?? randomUUID5();
    const result = await compactHotTier(ctx.db, embedFn, ctx.config.compaction, sessionId);
    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            sessionId,
            carryForward: result.carryForward.length,
            promoted: result.promoted.length,
            discarded: result.discarded.length,
            details: result
          })
        }
      ]
    };
  }
  if (name === "session_context") {
    const hotMemories = listEntries(ctx.db, "hot", "memory");
    const hotKnowledge = listEntries(ctx.db, "hot", "knowledge");
    const allEntries = [...hotMemories, ...hotKnowledge];
    const lines = allEntries.map((e) => {
      const tags = e.tags.length > 0 ? ` [${e.tags.join(", ")}]` : "";
      const project = e.project ? ` (project: ${e.project})` : "";
      return `- ${e.content}${tags}${project}`;
    });
    const contextText = lines.length > 0 ? lines.join("\n") : "(no hot tier entries)";
    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            entryCount: allEntries.length,
            context: contextText
          })
        }
      ]
    };
  }
  return null;
}
function registerSessionTools() {
  return SESSION_TOOLS;
}

// src/tools/extra-tools.ts
import { mkdirSync as mkdirSync4, writeFileSync as writeFileSync2, readFileSync as readFileSync8 } from "fs";
import { join as join10 } from "path";
function registerExtraTools() {
  return [
    {
      name: "compact_now",
      description: "Force compaction of a memory tier immediately",
      inputSchema: {
        type: "object",
        properties: {
          tier: {
            type: "string",
            enum: ["hot"],
            description: "Tier to compact (currently only hot supported)"
          }
        },
        required: []
      }
    },
    {
      name: "memory_inspect",
      description: "Deep dive on a single memory entry, including its compaction history",
      inputSchema: {
        type: "object",
        properties: {
          id: { type: "string", description: "Entry ID" }
        },
        required: ["id"]
      }
    },
    {
      name: "memory_history",
      description: "List recent tier movements from compaction log",
      inputSchema: {
        type: "object",
        properties: {
          limit: { type: "number", description: "Max results (default 20)" }
        },
        required: []
      }
    },
    {
      name: "memory_lineage",
      description: "Show the full compaction ancestry tree for a memory entry",
      inputSchema: {
        type: "object",
        properties: {
          id: { type: "string", description: "Entry ID" }
        },
        required: ["id"]
      }
    },
    {
      name: "memory_export",
      description: "Export memories to a JSON file",
      inputSchema: {
        type: "object",
        properties: {
          tiers: {
            type: "array",
            items: { type: "string", enum: ["hot", "warm", "cold"] },
            description: "Tiers to export (default: all)"
          },
          content_types: {
            type: "array",
            items: { type: "string", enum: ["memory", "knowledge"] },
            description: "Content types to export (default: all)"
          },
          format: {
            type: "string",
            enum: ["json"],
            description: "Export format (default: json)"
          }
        },
        required: []
      }
    },
    {
      name: "memory_import",
      description: "Import memories from a JSON export file",
      inputSchema: {
        type: "object",
        properties: {
          path: { type: "string", description: "Path to export JSON file" }
        },
        required: ["path"]
      }
    }
  ];
}
function getCompactionLogForEntry(db, id) {
  const rows = db.prepare(
    `SELECT * FROM compaction_log
       WHERE target_entry_id = ?
          OR source_entry_ids LIKE ?
       ORDER BY timestamp DESC`
  ).all(id, `%"${id}"%`);
  return rows;
}
function buildLineage(db, id, depth) {
  if (depth >= 10) {
    return { id, sources: [] };
  }
  const row = db.prepare(`SELECT * FROM compaction_log WHERE target_entry_id = ? ORDER BY timestamp DESC LIMIT 1`).get(id);
  if (!row) {
    return { id };
  }
  let sourceIds = [];
  try {
    sourceIds = JSON.parse(row.source_entry_ids);
  } catch {
    sourceIds = [];
  }
  const sources = sourceIds.map((srcId) => buildLineage(db, srcId, depth + 1));
  return {
    id,
    compaction_log_id: row.id,
    reason: row.reason,
    timestamp: row.timestamp,
    source_tier: row.source_tier,
    target_tier: row.target_tier,
    sources
  };
}
async function handleExtraTool(name, args, ctx) {
  const { db } = ctx;
  if (name === "compact_now") {
    await ctx.embedder.ensureLoaded();
    const embedFn = (text) => ctx.embedder.embed(text);
    const result = await compactHotTier(
      db,
      embedFn,
      ctx.config.compaction,
      ctx.sessionId
    );
    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            carryForward: result.carryForward.length,
            promoted: result.promoted.length,
            discarded: result.discarded.length,
            carryForwardIds: result.carryForward,
            promotedIds: result.promoted,
            discardedIds: result.discarded
          })
        }
      ]
    };
  }
  if (name === "memory_inspect") {
    const id = validateString(args.id, "id");
    const location = getMemory(db, id);
    const compactionHistory = getCompactionLogForEntry(db, id);
    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            entry: location,
            compaction_history: compactionHistory
          })
        }
      ]
    };
  }
  if (name === "memory_history") {
    const limit = validateOptionalNumber(args.limit, "limit", 1, 1e3) ?? 20;
    const rows = db.prepare(`SELECT * FROM compaction_log ORDER BY timestamp DESC LIMIT ?`).all(limit);
    const movements = rows.map((row) => {
      let sourceIds = [];
      try {
        sourceIds = JSON.parse(row.source_entry_ids);
      } catch {
        sourceIds = [];
      }
      return {
        id: row.id,
        timestamp: row.timestamp,
        session_id: row.session_id,
        source_tier: row.source_tier,
        target_tier: row.target_tier,
        source_entry_ids: sourceIds,
        target_entry_id: row.target_entry_id,
        reason: row.reason,
        decay_scores: (() => {
          try {
            return JSON.parse(row.decay_scores);
          } catch {
            return {};
          }
        })()
      };
    });
    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({ movements, count: movements.length })
        }
      ]
    };
  }
  if (name === "memory_lineage") {
    const id = validateString(args.id, "id");
    const lineage = buildLineage(db, id, 0);
    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({ lineage })
        }
      ]
    };
  }
  if (name === "memory_export") {
    const tierFilter = args.tiers;
    const typeFilter = args.content_types;
    const pairs = ALL_TABLE_PAIRS.filter(
      (p) => (!tierFilter || tierFilter.includes(p.tier)) && (!typeFilter || typeFilter.includes(p.type))
    );
    const allEntries = [];
    for (const { tier, type } of pairs) {
      const entries = listEntries(db, tier, type);
      for (const entry of entries) {
        allEntries.push({
          ...entry,
          tier,
          content_type: type
        });
      }
    }
    const exportsDir = join10(getDataDir(), "exports");
    mkdirSync4(exportsDir, { recursive: true });
    const timestamp = Date.now();
    const exportPath = join10(exportsDir, `${timestamp}.json`);
    const exportData = { version: 1, exported_at: timestamp, entries: allEntries };
    const jsonStr = JSON.stringify(exportData, null, 2);
    writeFileSync2(exportPath, jsonStr, "utf-8");
    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            path: exportPath,
            entry_count: allEntries.length,
            size_bytes: Buffer.byteLength(jsonStr, "utf-8")
          })
        }
      ]
    };
  }
  if (name === "memory_import") {
    const filePath = validatePath(args.path, "path");
    let raw;
    try {
      raw = readFileSync8(filePath, "utf-8");
    } catch (err) {
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify({ error: `Failed to read file: ${String(err)}` })
          }
        ]
      };
    }
    let exportData;
    try {
      exportData = JSON.parse(raw);
    } catch {
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify({ error: "Invalid JSON in export file" })
          }
        ]
      };
    }
    const entries = exportData.entries ?? [];
    if (!Array.isArray(entries)) {
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify({ error: "Export file missing entries array" })
          }
        ]
      };
    }
    await ctx.embedder.ensureLoaded();
    let imported = 0;
    let skipped = 0;
    const existingIds = /* @__PURE__ */ new Set();
    const existingContents = /* @__PURE__ */ new Set();
    for (const { tier, type } of ALL_TABLE_PAIRS) {
      const existing = listEntries(db, tier, type);
      for (const e of existing) {
        existingIds.add(e.id);
        existingContents.add(e.content);
      }
    }
    const seenContents = new Set(existingContents);
    for (const entry of entries) {
      if (typeof entry.content !== "string" || !entry.content) {
        skipped++;
        continue;
      }
      if (existingIds.has(entry.id)) {
        skipped++;
        continue;
      }
      if (seenContents.has(entry.content)) {
        skipped++;
        continue;
      }
      seenContents.add(entry.content);
      const tier = ["hot", "warm", "cold"].includes(entry.tier) ? entry.tier : "hot";
      const content_type = ["memory", "knowledge"].includes(entry.content_type) ? entry.content_type : "memory";
      const newId = insertEntry(db, tier, content_type, {
        content: entry.content,
        summary: entry.summary ?? null,
        source: entry.source ?? null,
        source_tool: entry.source_tool ?? null,
        project: entry.project ?? null,
        tags: Array.isArray(entry.tags) ? entry.tags : [],
        parent_id: entry.parent_id ?? null,
        collection_id: entry.collection_id ?? null,
        metadata: entry.metadata ?? {}
      });
      const vec = await ctx.embedder.embed(entry.content);
      insertEmbedding(db, tier, content_type, newId, vec);
      imported++;
    }
    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({ imported, skipped })
        }
      ]
    };
  }
  return null;
}

// src/tools/registry.ts
async function startServer(ctx) {
  const server = new Server(
    { name: "total-recall", version: "0.1.0" },
    { capabilities: { tools: {} } }
  );
  const allTools = [
    ...MEMORY_TOOLS,
    ...SYSTEM_TOOLS,
    ...registerKbTools(),
    ...registerEvalTools(),
    ...registerImportTools(),
    ...registerSessionTools(),
    ...registerExtraTools()
  ];
  server.setRequestHandler(ListToolsRequestSchema, async () => {
    return { tools: allTools };
  });
  server.setRequestHandler(CallToolRequestSchema, async (request) => {
    const { name, arguments: rawArgs } = request.params;
    const args = rawArgs ?? {};
    const memResult = await handleMemoryTool(name, args ?? {}, ctx);
    if (memResult !== null) return memResult;
    const sysResult = handleSystemTool(name, args ?? {}, ctx);
    if (sysResult !== null) return sysResult;
    const kbResult = await handleKbTool(name, args ?? {}, ctx);
    if (kbResult !== null) return kbResult;
    const evalResult = await handleEvalTool(name, args ?? {}, ctx);
    if (evalResult !== null) return evalResult;
    const importResult = await handleImportTool(name, args ?? {}, ctx);
    if (importResult !== null) return importResult;
    const sessionResult = await handleSessionTool(name, args ?? {}, ctx);
    if (sessionResult !== null) return sessionResult;
    const extraResult = await handleExtraTool(name, args ?? {}, ctx);
    if (extraResult !== null) return extraResult;
    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({ error: `Unknown tool: ${name}` })
        }
      ],
      isError: true
    };
  });
  const transport = new StdioServerTransport();
  await server.connect(transport);
}

// src/index.ts
async function main() {
  const config = loadConfig();
  const db = getDb();
  const embedder = new Embedder({
    model: config.embedding.model,
    dimensions: config.embedding.dimensions
  });
  const sessionId = randomUUID6();
  process.stderr.write(`total-recall: MCP server starting (db: ${getDataDir()}/total-recall.db)
`);
  await startServer({ db, config, embedder, sessionId });
  const cleanup = () => {
    closeDb();
    process.exit(0);
  };
  process.on("SIGINT", cleanup);
  process.on("SIGTERM", cleanup);
}
main().catch((err) => {
  process.stderr.write(`total-recall: fatal error: ${err}
`);
  process.exit(1);
});
