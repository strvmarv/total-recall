// src/eval/ci-smoke.ts
import { resolve, dirname as dirname2, basename } from "path";
import { fileURLToPath as fileURLToPath2 } from "url";
import Database from "better-sqlite3";
import * as sqliteVec from "sqlite-vec";

// src/types.ts
function tableName(tier, type) {
  const typeStr = type === "memory" ? "memories" : "knowledge";
  return `${tier}_${typeStr}`;
}
function vecTableName(tier, type) {
  return `${tableName(tier, type)}_vec`;
}
function ftsTableName(tier, type) {
  return `${tableName(tier, type)}_fts`;
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
  },
  // Migration 2: _meta key-value store + benchmark_candidates
  (db) => {
    db.prepare(`
      CREATE TABLE IF NOT EXISTS _meta (
        key   TEXT PRIMARY KEY,
        value TEXT NOT NULL
      )
    `).run();
    db.prepare(`
      CREATE TABLE IF NOT EXISTS benchmark_candidates (
        id                  TEXT PRIMARY KEY,
        query_text          TEXT NOT NULL UNIQUE,
        top_score           REAL NOT NULL,
        top_result_content  TEXT,
        top_result_entry_id TEXT,
        first_seen          INTEGER NOT NULL,
        last_seen           INTEGER NOT NULL,
        times_seen          INTEGER DEFAULT 1,
        status              TEXT DEFAULT 'pending'
      )
    `).run();
    db.prepare(
      `CREATE INDEX IF NOT EXISTS idx_benchmark_candidates_status ON benchmark_candidates(status)`
    ).run();
  },
  // Migration 3: FTS5 full-text indexes for hybrid search
  (db) => {
    for (const pair of ALL_TABLE_PAIRS) {
      const tbl = tableName(pair.tier, pair.type);
      const ftsTbl = `${tbl}_fts`;
      db.prepare(
        `CREATE VIRTUAL TABLE IF NOT EXISTS ${ftsTbl} USING fts5(content, tags, content=${tbl}, content_rowid=rowid)`
      ).run();
      db.prepare(`
        CREATE TRIGGER IF NOT EXISTS ${tbl}_fts_ai AFTER INSERT ON ${tbl} BEGIN
          INSERT INTO ${ftsTbl}(rowid, content, tags) VALUES (new.rowid, new.content, new.tags);
        END
      `).run();
      db.prepare(`
        CREATE TRIGGER IF NOT EXISTS ${tbl}_fts_ad AFTER DELETE ON ${tbl} BEGIN
          INSERT INTO ${ftsTbl}(${ftsTbl}, rowid, content, tags) VALUES('delete', old.rowid, old.content, old.tags);
        END
      `).run();
      db.prepare(`
        CREATE TRIGGER IF NOT EXISTS ${tbl}_fts_au AFTER UPDATE ON ${tbl} BEGIN
          INSERT INTO ${ftsTbl}(${ftsTbl}, rowid, content, tags) VALUES('delete', old.rowid, old.content, old.tags);
          INSERT INTO ${ftsTbl}(rowid, content, tags) VALUES (new.rowid, new.content, new.tags);
        END
      `).run();
      db.prepare(
        `INSERT INTO ${ftsTbl}(rowid, content, tags) SELECT rowid, content, tags FROM ${tbl}`
      ).run();
    }
  }
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

// src/config.ts
import { readFileSync, writeFileSync, existsSync, mkdirSync } from "fs";
import { join } from "path";
import { createHash, randomUUID } from "crypto";
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
function isSafeKey(key) {
  return key !== "__proto__" && key !== "constructor" && key !== "prototype";
}
function deepMerge(target, source) {
  const result = { ...target };
  for (const key of Object.keys(source)) {
    if (!isSafeKey(key)) continue;
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

// src/embedding/embedder.ts
import { readFile } from "fs/promises";
import { join as join3 } from "path";
import * as ort from "onnxruntime-node";

// src/embedding/model-manager.ts
import { existsSync as existsSync2, mkdirSync as mkdirSync2, readdirSync } from "fs";
import { readFileSync as readFileSync2, statSync } from "fs";
import { writeFile } from "fs/promises";
import { join as join2, dirname } from "path";
import { fileURLToPath } from "url";
var HF_BASE_URL = "https://huggingface.co";
var HF_REVISION = "main";
function getBundledModelPath(modelName) {
  const distDir = dirname(fileURLToPath(import.meta.url));
  return join2(distDir, "..", "models", modelName);
}
function getUserModelPath(modelName) {
  return join2(getDataDir(), "models", modelName);
}
function getModelPath(modelName) {
  const bundled = getBundledModelPath(modelName);
  if (isModelDownloaded(bundled)) return bundled;
  return getUserModelPath(modelName);
}
function isModelDownloaded(modelPath) {
  if (!existsSync2(modelPath)) return false;
  try {
    const files = readdirSync(modelPath);
    return files.some((f) => f.endsWith(".onnx"));
  } catch {
    return false;
  }
}
async function validateDownload(modelPath) {
  const modelStat = statSync(join2(modelPath, "model.onnx"));
  if (modelStat.size < 1e6) {
    throw new Error("model.onnx appears corrupted (< 1MB)");
  }
  const tokenizerText = readFileSync2(join2(modelPath, "tokenizer.json"), "utf-8");
  try {
    JSON.parse(tokenizerText);
  } catch {
    throw new Error("tokenizer.json is not valid JSON");
  }
}
async function downloadModel(modelName) {
  const modelPath = getUserModelPath(modelName);
  mkdirSync2(modelPath, { recursive: true });
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
    const dest = join2(modelPath, file);
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

// src/embedding/tokenizer.ts
var CLS_TOKEN_ID = 101;
var SEP_TOKEN_ID = 102;
var UNK_TOKEN_ID = 100;
var MAX_SEQ_LEN = 512;
var MAX_INPUT_CHARS_PER_WORD = 100;
var WordPieceTokenizer = class {
  vocab;
  constructor(vocab) {
    this.vocab = /* @__PURE__ */ Object.create(null);
    Object.assign(this.vocab, vocab);
  }
  tokenize(text) {
    const normalized = this.normalize(text);
    const words = this.preTokenize(normalized);
    const ids = [CLS_TOKEN_ID];
    for (const word of words) {
      if (ids.length >= MAX_SEQ_LEN - 1) break;
      const subIds = this.wordPiece(word);
      for (const id of subIds) {
        ids.push(id);
        if (ids.length >= MAX_SEQ_LEN - 1) break;
      }
    }
    ids.push(SEP_TOKEN_ID);
    return ids;
  }
  normalize(text) {
    let out = "";
    for (const ch of text) {
      const cp = ch.codePointAt(0);
      if (isControl(cp) && !isWhitespace(cp)) continue;
      if (isCjk(cp)) {
        out += ` ${ch} `;
      } else {
        out += ch;
      }
    }
    return out.toLowerCase();
  }
  preTokenize(text) {
    const tokens = [];
    let current = "";
    for (const ch of text) {
      const cp = ch.codePointAt(0);
      if (isWhitespace(cp)) {
        if (current) tokens.push(current);
        current = "";
      } else if (isPunctuation(cp)) {
        if (current) tokens.push(current);
        tokens.push(ch);
        current = "";
      } else {
        current += ch;
      }
    }
    if (current) tokens.push(current);
    return tokens;
  }
  wordPiece(word) {
    if (word.length > MAX_INPUT_CHARS_PER_WORD) return [UNK_TOKEN_ID];
    const ids = [];
    let start = 0;
    while (start < word.length) {
      let end = word.length;
      let matched = false;
      while (start < end) {
        const substr = start === 0 ? word.slice(0, end) : `##${word.slice(start, end)}`;
        const id = this.vocab[substr];
        if (id !== void 0) {
          ids.push(id);
          start = end;
          matched = true;
          break;
        }
        end--;
      }
      if (!matched) {
        return [UNK_TOKEN_ID];
      }
    }
    return ids;
  }
};
function isWhitespace(cp) {
  return cp === 32 || cp === 9 || cp === 10 || cp === 13;
}
function isControl(cp) {
  if (cp === 9 || cp === 10 || cp === 13) return false;
  const cat = charCategory(cp);
  return cat === "Cc" || cat === "Cf";
}
function isPunctuation(cp) {
  if (cp >= 33 && cp <= 47 || cp >= 58 && cp <= 64 || cp >= 91 && cp <= 96 || cp >= 123 && cp <= 126) {
    return true;
  }
  return new RegExp("^\\p{P}$", "u").test(String.fromCodePoint(cp));
}
function isCjk(cp) {
  return cp >= 19968 && cp <= 40959 || cp >= 13312 && cp <= 19903 || cp >= 131072 && cp <= 173791 || cp >= 173824 && cp <= 177983 || cp >= 177984 && cp <= 178207 || cp >= 178208 && cp <= 183983 || cp >= 63744 && cp <= 64255 || cp >= 194560 && cp <= 195103;
}
function charCategory(cp) {
  if (cp <= 31 || cp >= 127 && cp <= 159) return "Cc";
  if (cp === 173 || cp >= 1536 && cp <= 1541 || cp === 1564 || cp === 1757 || cp === 1807)
    return "Cf";
  if (cp === 65279 || cp >= 65529 && cp <= 65531) return "Cf";
  if (cp >= 8203 && cp <= 8207) return "Cf";
  if (cp >= 8234 && cp <= 8238) return "Cf";
  if (cp >= 8288 && cp <= 8292) return "Cf";
  if (cp >= 8294 && cp <= 8297) return "Cf";
  return "Lo";
}

// src/embedding/embedder.ts
var Embedder = class {
  options;
  session = null;
  tokenizer = null;
  constructor(options) {
    this.options = options;
  }
  isLoaded() {
    return this.session !== null && this.tokenizer !== null;
  }
  async ensureLoaded() {
    if (this.isLoaded()) return;
    const modelPath = getModelPath(this.options.model);
    if (!isModelDownloaded(modelPath)) {
      await downloadModel(this.options.model);
    }
    const onnxPath = join3(modelPath, "model.onnx");
    this.session = await ort.InferenceSession.create(onnxPath);
    const tokenizerPath = join3(modelPath, "tokenizer.json");
    const tokenizerText = await readFile(tokenizerPath, "utf-8");
    const tokenizerJson = JSON.parse(tokenizerText);
    this.tokenizer = new WordPieceTokenizer(tokenizerJson.model.vocab);
  }
  tokenize(text) {
    if (!this.tokenizer) throw new Error("Tokenizer not loaded");
    return this.tokenizer.tokenize(text);
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

// src/eval/benchmark-runner.ts
import { readFileSync as readFileSync3 } from "fs";

// src/db/entries.ts
import { randomUUID as randomUUID2 } from "crypto";
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
  const id = randomUUID2();
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

// src/memory/delete.ts
function deleteMemory(db, id) {
  const location = getMemory(db, id);
  if (!location) return false;
  deleteEmbedding(db, location.tier, location.content_type, id);
  deleteEntry(db, location.tier, location.content_type, id);
  return true;
}

// src/search/fts-search.ts
function sanitizeFtsQuery(query) {
  const words = query.split(/\s+/).filter(Boolean).map((w) => `"${w.replace(/"/g, '""')}"`).join(" ");
  return words;
}
function searchByFts(db, tier, type, query, opts) {
  const contentTable = tableName(tier, type);
  const ftsTable = ftsTableName(tier, type);
  const tableExists = db.prepare("SELECT name FROM sqlite_master WHERE type='table' AND name=?").get(ftsTable);
  if (!tableExists) return [];
  const sanitized = sanitizeFtsQuery(query);
  if (!sanitized) return [];
  const rows = db.prepare(
    `SELECT c.id, rank as bm25_rank
       FROM ${ftsTable} fts
       INNER JOIN ${contentTable} c ON c.rowid = fts.rowid
       WHERE ${ftsTable} MATCH ?
       ORDER BY rank
       LIMIT ?`
  ).all(sanitized, opts.topK);
  if (rows.length === 0) return [];
  const rawScores = rows.map((r) => -r.bm25_rank);
  const maxRaw = Math.max(...rawScores);
  const minRaw = Math.min(...rawScores);
  const range = maxRaw - minRaw;
  return rows.map((r, i) => ({
    id: r.id,
    score: range > 0 ? (rawScores[i] - minRaw) / range : 1
  }));
}

// src/memory/search.ts
var DEFAULT_FTS_WEIGHT = 0.3;
async function searchMemory(db, embed, query, opts) {
  const queryVec = await embed(query);
  const ftsWeight = opts.ftsWeight ?? DEFAULT_FTS_WEIGHT;
  const oversampledK = opts.topK * 2;
  const scoreMap = /* @__PURE__ */ new Map();
  for (const { tier, content_type } of opts.tiers) {
    const vectorResults = searchByVector(db, tier, content_type, queryVec, {
      topK: oversampledK,
      minScore: opts.minScore
    });
    for (const vr of vectorResults) {
      const existing = scoreMap.get(vr.id);
      if (!existing || vr.score > existing.vectorScore) {
        scoreMap.set(vr.id, {
          vectorScore: vr.score,
          ftsScore: existing?.ftsScore ?? 0,
          tier,
          content_type
        });
      }
    }
    const ftsResults = searchByFts(db, tier, content_type, query, {
      topK: oversampledK
    });
    for (const fr of ftsResults) {
      const existing = scoreMap.get(fr.id);
      if (existing) {
        existing.ftsScore = Math.max(existing.ftsScore, fr.score);
      } else {
        scoreMap.set(fr.id, {
          vectorScore: 0,
          ftsScore: fr.score,
          tier,
          content_type
        });
      }
    }
  }
  const candidates = [];
  for (const [id, scores] of scoreMap) {
    const fusedScore = scores.vectorScore + ftsWeight * scores.ftsScore;
    candidates.push({ id, fusedScore, tier: scores.tier, content_type: scores.content_type });
  }
  candidates.sort((a, b) => b.fusedScore - a.fusedScore);
  const topCandidates = candidates.slice(0, opts.topK);
  const merged = [];
  for (const c of topCandidates) {
    const entry = getEntry(db, c.tier, c.content_type, c.id);
    if (!entry) continue;
    updateEntry(db, c.tier, c.content_type, c.id, { touch: true });
    merged.push({
      entry,
      tier: c.tier,
      content_type: c.content_type,
      score: c.fusedScore,
      rank: 0
    });
  }
  merged.forEach((r, i) => {
    r.rank = i + 1;
  });
  return merged;
}

// src/eval/benchmark-runner.ts
async function runBenchmark(db, embed, opts) {
  const corpusLines = readFileSync3(opts.corpusPath, "utf-8").split("\n").filter((line) => line.trim().length > 0);
  const seededIds = [];
  for (const line of corpusLines) {
    const entry = JSON.parse(line);
    const id = await storeMemory(db, embed, {
      content: entry.content,
      type: entry.type,
      tier: "warm",
      contentType: "memory",
      tags: entry.tags
    });
    seededIds.push(id);
  }
  const benchmarkLines = readFileSync3(opts.benchmarkPath, "utf-8").split("\n").filter((line) => line.trim().length > 0);
  const queries = benchmarkLines.map((line) => JSON.parse(line));
  const details = [];
  let exactMatches = 0;
  let fuzzyMatches = 0;
  let tierMatches = 0;
  let totalLatencyMs = 0;
  const config = loadConfig();
  for (const bq of queries) {
    const start = performance.now();
    const results = await searchMemory(db, embed, bq.query, {
      tiers: [{ tier: "warm", content_type: "memory" }],
      topK: 3,
      ftsWeight: config.search?.fts_weight
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
    let negativePass = true;
    if (bq.expected_absent && topContent) {
      negativePass = !topContent.toLowerCase().includes(bq.expected_absent.toLowerCase());
    }
    if (matched) exactMatches++;
    if (fuzzyMatched) fuzzyMatches++;
    if (tierRouted) tierMatches++;
    details.push({
      query: bq.query,
      expectedContains: bq.expected_content_contains,
      topResult: topContent,
      topScore,
      matched,
      fuzzyMatched,
      hasNegativeAssertion: !!bq.expected_absent,
      negativePass
    });
  }
  for (const id of seededIds) {
    deleteMemory(db, id);
  }
  const total = queries.length;
  const negativeQueries = details.filter((d) => d.hasNegativeAssertion);
  const negativePassRate = negativeQueries.length > 0 ? negativeQueries.filter((d) => d.negativePass).length / negativeQueries.length : 1;
  return {
    totalQueries: total,
    exactMatchRate: total > 0 ? exactMatches / total : 0,
    fuzzyMatchRate: total > 0 ? fuzzyMatches / total : 0,
    tierRoutingRate: total > 0 ? tierMatches / total : 0,
    negativePassRate,
    avgLatencyMs: total > 0 ? totalLatencyMs / total : 0,
    details
  };
}

// src/eval/ci-smoke.ts
var SMOKE_PASS_THRESHOLD = 0.8;
var __dirname = dirname2(fileURLToPath2(import.meta.url));
var PACKAGE_ROOT = basename(__dirname) === "dist" ? resolve(__dirname, "..") : resolve(__dirname, "..", "..");
async function main() {
  const config = loadConfig();
  const db = new Database(":memory:");
  sqliteVec.load(db);
  initSchema(db);
  const embedder = new Embedder(config.embedding);
  const embed = (text) => embedder.embed(text);
  const corpusPath = resolve(PACKAGE_ROOT, "eval", "corpus", "memories.jsonl");
  const benchmarkPath = resolve(PACKAGE_ROOT, "eval", "benchmarks", "smoke.jsonl");
  const result = await runBenchmark(db, embed, { corpusPath, benchmarkPath });
  console.log(`Smoke benchmark: ${result.totalQueries} queries`);
  console.log(`  Exact match rate: ${(result.exactMatchRate * 100).toFixed(1)}%`);
  console.log(`  Fuzzy match rate: ${(result.fuzzyMatchRate * 100).toFixed(1)}%`);
  console.log(`  Negative pass rate: ${(result.negativePassRate * 100).toFixed(1)}%`);
  console.log(`  Avg latency: ${result.avgLatencyMs.toFixed(1)}ms`);
  db.close();
  if (result.exactMatchRate < SMOKE_PASS_THRESHOLD) {
    console.error(`
FAIL: Exact match rate ${(result.exactMatchRate * 100).toFixed(1)}% < ${SMOKE_PASS_THRESHOLD * 100}% threshold`);
    process.exit(1);
  }
  console.log("\nPASS");
}
main().catch((err) => {
  console.error("Benchmark failed:", err);
  process.exit(1);
});
