// src-ts/eval/ci-smoke.ts
import { resolve, dirname as dirname3, basename } from "path";
import { fileURLToPath as fileURLToPath4 } from "url";
import { Database } from "bun:sqlite";

// node_modules/sqlite-vec/index.mjs
import { fileURLToPath } from "url";
import { arch, platform } from "process";
var BASE_PACKAGE_NAME = "sqlite-vec";
var ENTRYPOINT_BASE_NAME = "vec0";
var supportedPlatforms = [["darwin", "x64"], ["linux", "x64"], ["darwin", "arm64"], ["win32", "x64"], ["linux", "arm64"]];
var invalidPlatformErrorMessage = `Unsupported platform for ${BASE_PACKAGE_NAME}, on a ${platform}-${arch} machine. Supported platforms are (${supportedPlatforms.map(([p, a]) => `${p}-${a}`).join(",")}). Consult the ${BASE_PACKAGE_NAME} NPM package README for details.`;
function validPlatform(platform2, arch2) {
  return supportedPlatforms.find(([p, a]) => platform2 === p && arch2 === a) !== void 0;
}
function extensionSuffix(platform2) {
  if (platform2 === "win32") return "dll";
  if (platform2 === "darwin") return "dylib";
  return "so";
}
function platformPackageName(platform2, arch2) {
  const os = platform2 === "win32" ? "windows" : platform2;
  return `${BASE_PACKAGE_NAME}-${os}-${arch2}`;
}
function getLoadablePath() {
  if (!validPlatform(platform, arch)) {
    throw new Error(
      invalidPlatformErrorMessage
    );
  }
  const packageName = platformPackageName(platform, arch);
  const loadablePath = fileURLToPath(import.meta.resolve(packageName + "/" + ENTRYPOINT_BASE_NAME + "." + extensionSuffix(platform)));
  return loadablePath;
}
function load(db) {
  db.loadExtension(getLoadablePath());
}

// src-ts/types.ts
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

// src-ts/db/schema.ts
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
      db.run(contentTableDDL(tbl));
      db.run(
        `CREATE VIRTUAL TABLE IF NOT EXISTS ${vecTbl} USING vec0(embedding float[384])`
      );
      for (const idx of contentTableIndexes(tbl)) {
        db.run(idx);
      }
    }
    for (const ddl of SYSTEM_TABLE_DDLS) {
      db.run(ddl);
    }
    for (const idx of SYSTEM_TABLE_INDEXES) {
      db.run(idx);
    }
  },
  // Migration 2: _meta key-value store + benchmark_candidates
  (db) => {
    db.run(`
      CREATE TABLE IF NOT EXISTS _meta (
        key   TEXT PRIMARY KEY,
        value TEXT NOT NULL
      )
    `);
    db.run(`
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
    `);
    db.run(
      `CREATE INDEX IF NOT EXISTS idx_benchmark_candidates_status ON benchmark_candidates(status)`
    );
  },
  // Migration 3: FTS5 full-text indexes for hybrid search
  (db) => {
    for (const pair of ALL_TABLE_PAIRS) {
      const tbl = tableName(pair.tier, pair.type);
      const ftsTbl = `${tbl}_fts`;
      db.run(
        `CREATE VIRTUAL TABLE IF NOT EXISTS ${ftsTbl} USING fts5(content, tags, content=${tbl}, content_rowid=rowid)`
      );
      db.run(`
        CREATE TRIGGER IF NOT EXISTS ${tbl}_fts_ai AFTER INSERT ON ${tbl} BEGIN
          INSERT INTO ${ftsTbl}(rowid, content, tags) VALUES (new.rowid, new.content, new.tags);
        END
      `);
      db.run(`
        CREATE TRIGGER IF NOT EXISTS ${tbl}_fts_ad AFTER DELETE ON ${tbl} BEGIN
          INSERT INTO ${ftsTbl}(${ftsTbl}, rowid, content, tags) VALUES('delete', old.rowid, old.content, old.tags);
        END
      `);
      db.run(`
        CREATE TRIGGER IF NOT EXISTS ${tbl}_fts_au AFTER UPDATE ON ${tbl} BEGIN
          INSERT INTO ${ftsTbl}(${ftsTbl}, rowid, content, tags) VALUES('delete', old.rowid, old.content, old.tags);
          INSERT INTO ${ftsTbl}(rowid, content, tags) VALUES (new.rowid, new.content, new.tags);
        END
      `);
      db.run(
        `INSERT INTO ${ftsTbl}(rowid, content, tags) SELECT rowid, content, tags FROM ${tbl}`
      );
    }
  }
];
function getCurrentVersion(db) {
  const hasTable = db.query("SELECT name FROM sqlite_master WHERE type='table' AND name='_schema_version'").get();
  if (!hasTable) return 0;
  const row = db.query("SELECT MAX(version) as v FROM _schema_version").get();
  return row?.v ?? 0;
}
function initSchema(db) {
  db.run("PRAGMA journal_mode = WAL");
  db.run("PRAGMA foreign_keys = ON");
  const migrate = db.transaction(() => {
    db.run(SCHEMA_VERSION_DDL);
    const currentVersion = getCurrentVersion(db);
    for (let i = currentVersion; i < MIGRATIONS.length; i++) {
      MIGRATIONS[i](db);
      db.run(
        "INSERT INTO _schema_version (version, applied_at) VALUES (?, ?)",
        [i + 1, Date.now()]
      );
    }
  });
  migrate();
}

// src-ts/config.ts
import { readFileSync, writeFileSync, existsSync, mkdirSync } from "fs";
import { join } from "path";
import { createHash, randomUUID } from "crypto";

// node_modules/smol-toml/dist/error.js
function getLineColFromPtr(string, ptr) {
  let lines = string.slice(0, ptr).split(/\r\n|\n|\r/g);
  return [lines.length, lines.pop().length + 1];
}
function makeCodeBlock(string, line, column) {
  let lines = string.split(/\r\n|\n|\r/g);
  let codeblock = "";
  let numberLen = (Math.log10(line + 1) | 0) + 1;
  for (let i = line - 1; i <= line + 1; i++) {
    let l = lines[i - 1];
    if (!l)
      continue;
    codeblock += i.toString().padEnd(numberLen, " ");
    codeblock += ":  ";
    codeblock += l;
    codeblock += "\n";
    if (i === line) {
      codeblock += " ".repeat(numberLen + column + 2);
      codeblock += "^\n";
    }
  }
  return codeblock;
}
var TomlError = class extends Error {
  line;
  column;
  codeblock;
  constructor(message, options) {
    const [line, column] = getLineColFromPtr(options.toml, options.ptr);
    const codeblock = makeCodeBlock(options.toml, line, column);
    super(`Invalid TOML document: ${message}

${codeblock}`, options);
    this.line = line;
    this.column = column;
    this.codeblock = codeblock;
  }
};

// node_modules/smol-toml/dist/util.js
function isEscaped(str, ptr) {
  let i = 0;
  while (str[ptr - ++i] === "\\")
    ;
  return --i && i % 2;
}
function indexOfNewline(str, start = 0, end = str.length) {
  let idx = str.indexOf("\n", start);
  if (str[idx - 1] === "\r")
    idx--;
  return idx <= end ? idx : -1;
}
function skipComment(str, ptr) {
  for (let i = ptr; i < str.length; i++) {
    let c = str[i];
    if (c === "\n")
      return i;
    if (c === "\r" && str[i + 1] === "\n")
      return i + 1;
    if (c < " " && c !== "	" || c === "\x7F") {
      throw new TomlError("control characters are not allowed in comments", {
        toml: str,
        ptr
      });
    }
  }
  return str.length;
}
function skipVoid(str, ptr, banNewLines, banComments) {
  let c;
  while (1) {
    while ((c = str[ptr]) === " " || c === "	" || !banNewLines && (c === "\n" || c === "\r" && str[ptr + 1] === "\n"))
      ptr++;
    if (banComments || c !== "#")
      break;
    ptr = skipComment(str, ptr);
  }
  return ptr;
}
function skipUntil(str, ptr, sep, end, banNewLines = false) {
  if (!end) {
    ptr = indexOfNewline(str, ptr);
    return ptr < 0 ? str.length : ptr;
  }
  for (let i = ptr; i < str.length; i++) {
    let c = str[i];
    if (c === "#") {
      i = indexOfNewline(str, i);
    } else if (c === sep) {
      return i + 1;
    } else if (c === end || banNewLines && (c === "\n" || c === "\r" && str[i + 1] === "\n")) {
      return i;
    }
  }
  throw new TomlError("cannot find end of structure", {
    toml: str,
    ptr
  });
}
function getStringEnd(str, seek) {
  let first = str[seek];
  let target = first === str[seek + 1] && str[seek + 1] === str[seek + 2] ? str.slice(seek, seek + 3) : first;
  seek += target.length - 1;
  do
    seek = str.indexOf(target, ++seek);
  while (seek > -1 && first !== "'" && isEscaped(str, seek));
  if (seek > -1) {
    seek += target.length;
    if (target.length > 1) {
      if (str[seek] === first)
        seek++;
      if (str[seek] === first)
        seek++;
    }
  }
  return seek;
}

// node_modules/smol-toml/dist/date.js
var DATE_TIME_RE = /^(\d{4}-\d{2}-\d{2})?[T ]?(?:(\d{2}):\d{2}(?::\d{2}(?:\.\d+)?)?)?(Z|[-+]\d{2}:\d{2})?$/i;
var TomlDate = class _TomlDate extends Date {
  #hasDate = false;
  #hasTime = false;
  #offset = null;
  constructor(date) {
    let hasDate = true;
    let hasTime = true;
    let offset = "Z";
    if (typeof date === "string") {
      let match = date.match(DATE_TIME_RE);
      if (match) {
        if (!match[1]) {
          hasDate = false;
          date = `0000-01-01T${date}`;
        }
        hasTime = !!match[2];
        hasTime && date[10] === " " && (date = date.replace(" ", "T"));
        if (match[2] && +match[2] > 23) {
          date = "";
        } else {
          offset = match[3] || null;
          date = date.toUpperCase();
          if (!offset && hasTime)
            date += "Z";
        }
      } else {
        date = "";
      }
    }
    super(date);
    if (!isNaN(this.getTime())) {
      this.#hasDate = hasDate;
      this.#hasTime = hasTime;
      this.#offset = offset;
    }
  }
  isDateTime() {
    return this.#hasDate && this.#hasTime;
  }
  isLocal() {
    return !this.#hasDate || !this.#hasTime || !this.#offset;
  }
  isDate() {
    return this.#hasDate && !this.#hasTime;
  }
  isTime() {
    return this.#hasTime && !this.#hasDate;
  }
  isValid() {
    return this.#hasDate || this.#hasTime;
  }
  toISOString() {
    let iso = super.toISOString();
    if (this.isDate())
      return iso.slice(0, 10);
    if (this.isTime())
      return iso.slice(11, 23);
    if (this.#offset === null)
      return iso.slice(0, -1);
    if (this.#offset === "Z")
      return iso;
    let offset = +this.#offset.slice(1, 3) * 60 + +this.#offset.slice(4, 6);
    offset = this.#offset[0] === "-" ? offset : -offset;
    let offsetDate = new Date(this.getTime() - offset * 6e4);
    return offsetDate.toISOString().slice(0, -1) + this.#offset;
  }
  static wrapAsOffsetDateTime(jsDate, offset = "Z") {
    let date = new _TomlDate(jsDate);
    date.#offset = offset;
    return date;
  }
  static wrapAsLocalDateTime(jsDate) {
    let date = new _TomlDate(jsDate);
    date.#offset = null;
    return date;
  }
  static wrapAsLocalDate(jsDate) {
    let date = new _TomlDate(jsDate);
    date.#hasTime = false;
    date.#offset = null;
    return date;
  }
  static wrapAsLocalTime(jsDate) {
    let date = new _TomlDate(jsDate);
    date.#hasDate = false;
    date.#offset = null;
    return date;
  }
};

// node_modules/smol-toml/dist/primitive.js
var INT_REGEX = /^((0x[0-9a-fA-F](_?[0-9a-fA-F])*)|(([+-]|0[ob])?\d(_?\d)*))$/;
var FLOAT_REGEX = /^[+-]?\d(_?\d)*(\.\d(_?\d)*)?([eE][+-]?\d(_?\d)*)?$/;
var LEADING_ZERO = /^[+-]?0[0-9_]/;
var ESCAPE_REGEX = /^[0-9a-f]{2,8}$/i;
var ESC_MAP = {
  b: "\b",
  t: "	",
  n: "\n",
  f: "\f",
  r: "\r",
  e: "\x1B",
  '"': '"',
  "\\": "\\"
};
function parseString(str, ptr = 0, endPtr = str.length) {
  let isLiteral = str[ptr] === "'";
  let isMultiline = str[ptr++] === str[ptr] && str[ptr] === str[ptr + 1];
  if (isMultiline) {
    endPtr -= 2;
    if (str[ptr += 2] === "\r")
      ptr++;
    if (str[ptr] === "\n")
      ptr++;
  }
  let tmp = 0;
  let isEscape;
  let parsed = "";
  let sliceStart = ptr;
  while (ptr < endPtr - 1) {
    let c = str[ptr++];
    if (c === "\n" || c === "\r" && str[ptr] === "\n") {
      if (!isMultiline) {
        throw new TomlError("newlines are not allowed in strings", {
          toml: str,
          ptr: ptr - 1
        });
      }
    } else if (c < " " && c !== "	" || c === "\x7F") {
      throw new TomlError("control characters are not allowed in strings", {
        toml: str,
        ptr: ptr - 1
      });
    }
    if (isEscape) {
      isEscape = false;
      if (c === "x" || c === "u" || c === "U") {
        let code = str.slice(ptr, ptr += c === "x" ? 2 : c === "u" ? 4 : 8);
        if (!ESCAPE_REGEX.test(code)) {
          throw new TomlError("invalid unicode escape", {
            toml: str,
            ptr: tmp
          });
        }
        try {
          parsed += String.fromCodePoint(parseInt(code, 16));
        } catch {
          throw new TomlError("invalid unicode escape", {
            toml: str,
            ptr: tmp
          });
        }
      } else if (isMultiline && (c === "\n" || c === " " || c === "	" || c === "\r")) {
        ptr = skipVoid(str, ptr - 1, true);
        if (str[ptr] !== "\n" && str[ptr] !== "\r") {
          throw new TomlError("invalid escape: only line-ending whitespace may be escaped", {
            toml: str,
            ptr: tmp
          });
        }
        ptr = skipVoid(str, ptr);
      } else if (c in ESC_MAP) {
        parsed += ESC_MAP[c];
      } else {
        throw new TomlError("unrecognized escape sequence", {
          toml: str,
          ptr: tmp
        });
      }
      sliceStart = ptr;
    } else if (!isLiteral && c === "\\") {
      tmp = ptr - 1;
      isEscape = true;
      parsed += str.slice(sliceStart, tmp);
    }
  }
  return parsed + str.slice(sliceStart, endPtr - 1);
}
function parseValue(value, toml, ptr, integersAsBigInt) {
  if (value === "true")
    return true;
  if (value === "false")
    return false;
  if (value === "-inf")
    return -Infinity;
  if (value === "inf" || value === "+inf")
    return Infinity;
  if (value === "nan" || value === "+nan" || value === "-nan")
    return NaN;
  if (value === "-0")
    return integersAsBigInt ? 0n : 0;
  let isInt = INT_REGEX.test(value);
  if (isInt || FLOAT_REGEX.test(value)) {
    if (LEADING_ZERO.test(value)) {
      throw new TomlError("leading zeroes are not allowed", {
        toml,
        ptr
      });
    }
    value = value.replace(/_/g, "");
    let numeric = +value;
    if (isNaN(numeric)) {
      throw new TomlError("invalid number", {
        toml,
        ptr
      });
    }
    if (isInt) {
      if ((isInt = !Number.isSafeInteger(numeric)) && !integersAsBigInt) {
        throw new TomlError("integer value cannot be represented losslessly", {
          toml,
          ptr
        });
      }
      if (isInt || integersAsBigInt === true)
        numeric = BigInt(value);
    }
    return numeric;
  }
  const date = new TomlDate(value);
  if (!date.isValid()) {
    throw new TomlError("invalid value", {
      toml,
      ptr
    });
  }
  return date;
}

// node_modules/smol-toml/dist/extract.js
function sliceAndTrimEndOf(str, startPtr, endPtr) {
  let value = str.slice(startPtr, endPtr);
  let commentIdx = value.indexOf("#");
  if (commentIdx > -1) {
    skipComment(str, commentIdx);
    value = value.slice(0, commentIdx);
  }
  return [value.trimEnd(), commentIdx];
}
function extractValue(str, ptr, end, depth, integersAsBigInt) {
  if (depth === 0) {
    throw new TomlError("document contains excessively nested structures. aborting.", {
      toml: str,
      ptr
    });
  }
  let c = str[ptr];
  if (c === "[" || c === "{") {
    let [value, endPtr2] = c === "[" ? parseArray(str, ptr, depth, integersAsBigInt) : parseInlineTable(str, ptr, depth, integersAsBigInt);
    if (end) {
      endPtr2 = skipVoid(str, endPtr2);
      if (str[endPtr2] === ",")
        endPtr2++;
      else if (str[endPtr2] !== end) {
        throw new TomlError("expected comma or end of structure", {
          toml: str,
          ptr: endPtr2
        });
      }
    }
    return [value, endPtr2];
  }
  let endPtr;
  if (c === '"' || c === "'") {
    endPtr = getStringEnd(str, ptr);
    let parsed = parseString(str, ptr, endPtr);
    if (end) {
      endPtr = skipVoid(str, endPtr);
      if (str[endPtr] && str[endPtr] !== "," && str[endPtr] !== end && str[endPtr] !== "\n" && str[endPtr] !== "\r") {
        throw new TomlError("unexpected character encountered", {
          toml: str,
          ptr: endPtr
        });
      }
      endPtr += +(str[endPtr] === ",");
    }
    return [parsed, endPtr];
  }
  endPtr = skipUntil(str, ptr, ",", end);
  let slice = sliceAndTrimEndOf(str, ptr, endPtr - +(str[endPtr - 1] === ","));
  if (!slice[0]) {
    throw new TomlError("incomplete key-value declaration: no value specified", {
      toml: str,
      ptr
    });
  }
  if (end && slice[1] > -1) {
    endPtr = skipVoid(str, ptr + slice[1]);
    endPtr += +(str[endPtr] === ",");
  }
  return [
    parseValue(slice[0], str, ptr, integersAsBigInt),
    endPtr
  ];
}

// node_modules/smol-toml/dist/struct.js
var KEY_PART_RE = /^[a-zA-Z0-9-_]+[ \t]*$/;
function parseKey(str, ptr, end = "=") {
  let dot = ptr - 1;
  let parsed = [];
  let endPtr = str.indexOf(end, ptr);
  if (endPtr < 0) {
    throw new TomlError("incomplete key-value: cannot find end of key", {
      toml: str,
      ptr
    });
  }
  do {
    let c = str[ptr = ++dot];
    if (c !== " " && c !== "	") {
      if (c === '"' || c === "'") {
        if (c === str[ptr + 1] && c === str[ptr + 2]) {
          throw new TomlError("multiline strings are not allowed in keys", {
            toml: str,
            ptr
          });
        }
        let eos = getStringEnd(str, ptr);
        if (eos < 0) {
          throw new TomlError("unfinished string encountered", {
            toml: str,
            ptr
          });
        }
        dot = str.indexOf(".", eos);
        let strEnd = str.slice(eos, dot < 0 || dot > endPtr ? endPtr : dot);
        let newLine = indexOfNewline(strEnd);
        if (newLine > -1) {
          throw new TomlError("newlines are not allowed in keys", {
            toml: str,
            ptr: ptr + dot + newLine
          });
        }
        if (strEnd.trimStart()) {
          throw new TomlError("found extra tokens after the string part", {
            toml: str,
            ptr: eos
          });
        }
        if (endPtr < eos) {
          endPtr = str.indexOf(end, eos);
          if (endPtr < 0) {
            throw new TomlError("incomplete key-value: cannot find end of key", {
              toml: str,
              ptr
            });
          }
        }
        parsed.push(parseString(str, ptr, eos));
      } else {
        dot = str.indexOf(".", ptr);
        let part = str.slice(ptr, dot < 0 || dot > endPtr ? endPtr : dot);
        if (!KEY_PART_RE.test(part)) {
          throw new TomlError("only letter, numbers, dashes and underscores are allowed in keys", {
            toml: str,
            ptr
          });
        }
        parsed.push(part.trimEnd());
      }
    }
  } while (dot + 1 && dot < endPtr);
  return [parsed, skipVoid(str, endPtr + 1, true, true)];
}
function parseInlineTable(str, ptr, depth, integersAsBigInt) {
  let res = {};
  let seen = /* @__PURE__ */ new Set();
  let c;
  ptr++;
  while ((c = str[ptr++]) !== "}" && c) {
    if (c === ",") {
      throw new TomlError("expected value, found comma", {
        toml: str,
        ptr: ptr - 1
      });
    } else if (c === "#")
      ptr = skipComment(str, ptr);
    else if (c !== " " && c !== "	" && c !== "\n" && c !== "\r") {
      let k;
      let t = res;
      let hasOwn = false;
      let [key, keyEndPtr] = parseKey(str, ptr - 1);
      for (let i = 0; i < key.length; i++) {
        if (i)
          t = hasOwn ? t[k] : t[k] = {};
        k = key[i];
        if ((hasOwn = Object.hasOwn(t, k)) && (typeof t[k] !== "object" || seen.has(t[k]))) {
          throw new TomlError("trying to redefine an already defined value", {
            toml: str,
            ptr
          });
        }
        if (!hasOwn && k === "__proto__") {
          Object.defineProperty(t, k, { enumerable: true, configurable: true, writable: true });
        }
      }
      if (hasOwn) {
        throw new TomlError("trying to redefine an already defined value", {
          toml: str,
          ptr
        });
      }
      let [value, valueEndPtr] = extractValue(str, keyEndPtr, "}", depth - 1, integersAsBigInt);
      seen.add(value);
      t[k] = value;
      ptr = valueEndPtr;
    }
  }
  if (!c) {
    throw new TomlError("unfinished table encountered", {
      toml: str,
      ptr
    });
  }
  return [res, ptr];
}
function parseArray(str, ptr, depth, integersAsBigInt) {
  let res = [];
  let c;
  ptr++;
  while ((c = str[ptr++]) !== "]" && c) {
    if (c === ",") {
      throw new TomlError("expected value, found comma", {
        toml: str,
        ptr: ptr - 1
      });
    } else if (c === "#")
      ptr = skipComment(str, ptr);
    else if (c !== " " && c !== "	" && c !== "\n" && c !== "\r") {
      let e = extractValue(str, ptr - 1, "]", depth - 1, integersAsBigInt);
      res.push(e[0]);
      ptr = e[1];
    }
  }
  if (!c) {
    throw new TomlError("unfinished array encountered", {
      toml: str,
      ptr
    });
  }
  return [res, ptr];
}

// node_modules/smol-toml/dist/parse.js
function peekTable(key, table, meta, type) {
  let t = table;
  let m = meta;
  let k;
  let hasOwn = false;
  let state;
  for (let i = 0; i < key.length; i++) {
    if (i) {
      t = hasOwn ? t[k] : t[k] = {};
      m = (state = m[k]).c;
      if (type === 0 && (state.t === 1 || state.t === 2)) {
        return null;
      }
      if (state.t === 2) {
        let l = t.length - 1;
        t = t[l];
        m = m[l].c;
      }
    }
    k = key[i];
    if ((hasOwn = Object.hasOwn(t, k)) && m[k]?.t === 0 && m[k]?.d) {
      return null;
    }
    if (!hasOwn) {
      if (k === "__proto__") {
        Object.defineProperty(t, k, { enumerable: true, configurable: true, writable: true });
        Object.defineProperty(m, k, { enumerable: true, configurable: true, writable: true });
      }
      m[k] = {
        t: i < key.length - 1 && type === 2 ? 3 : type,
        d: false,
        i: 0,
        c: {}
      };
    }
  }
  state = m[k];
  if (state.t !== type && !(type === 1 && state.t === 3)) {
    return null;
  }
  if (type === 2) {
    if (!state.d) {
      state.d = true;
      t[k] = [];
    }
    t[k].push(t = {});
    state.c[state.i++] = state = { t: 1, d: false, i: 0, c: {} };
  }
  if (state.d) {
    return null;
  }
  state.d = true;
  if (type === 1) {
    t = hasOwn ? t[k] : t[k] = {};
  } else if (type === 0 && hasOwn) {
    return null;
  }
  return [k, t, state.c];
}
function parse(toml, { maxDepth = 1e3, integersAsBigInt } = {}) {
  let res = {};
  let meta = {};
  let tbl = res;
  let m = meta;
  for (let ptr = skipVoid(toml, 0); ptr < toml.length; ) {
    if (toml[ptr] === "[") {
      let isTableArray = toml[++ptr] === "[";
      let k = parseKey(toml, ptr += +isTableArray, "]");
      if (isTableArray) {
        if (toml[k[1] - 1] !== "]") {
          throw new TomlError("expected end of table declaration", {
            toml,
            ptr: k[1] - 1
          });
        }
        k[1]++;
      }
      let p = peekTable(
        k[0],
        res,
        meta,
        isTableArray ? 2 : 1
        /* Type.EXPLICIT */
      );
      if (!p) {
        throw new TomlError("trying to redefine an already defined table or value", {
          toml,
          ptr
        });
      }
      m = p[2];
      tbl = p[1];
      ptr = k[1];
    } else {
      let k = parseKey(toml, ptr);
      let p = peekTable(
        k[0],
        tbl,
        m,
        0
        /* Type.DOTTED */
      );
      if (!p) {
        throw new TomlError("trying to redefine an already defined table or value", {
          toml,
          ptr
        });
      }
      let v = extractValue(toml, k[1], void 0, maxDepth, integersAsBigInt);
      p[1][p[0]] = v[0];
      ptr = v[1];
    }
    ptr = skipVoid(toml, ptr, true);
    if (toml[ptr] && toml[ptr] !== "\n" && toml[ptr] !== "\r") {
      throw new TomlError("each key-value declaration must be followed by an end-of-line", {
        toml,
        ptr
      });
    }
    ptr = skipVoid(toml, ptr);
  }
  return res;
}

// src-ts/config.ts
var DEFAULTS_PATH = new URL("./defaults.toml", import.meta.url);
function getDataDir() {
  return process.env.TOTAL_RECALL_HOME ?? join(process.env.HOME ?? "~", ".total-recall");
}
function loadConfig() {
  const defaultsText = readFileSync(DEFAULTS_PATH, "utf-8");
  const defaults = parse(defaultsText);
  const userConfigPath = join(getDataDir(), "config.toml");
  if (existsSync(userConfigPath)) {
    const userText = readFileSync(userConfigPath, "utf-8");
    const userConfig = parse(userText);
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

// src-ts/embedding/embedder.ts
import { readFile as readFile2 } from "fs/promises";
import { join as join5 } from "path";
import * as ort from "onnxruntime-node";

// src-ts/embedding/bootstrap.ts
import { mkdirSync as mkdirSync3 } from "fs";
import { join as join4 } from "path";

// src-ts/embedding/registry.ts
import { readFileSync as readFileSync2 } from "fs";

// src-ts/pkg-root.ts
import { existsSync as existsSync2 } from "fs";
import { dirname, join as join2 } from "path";
import { fileURLToPath as fileURLToPath2 } from "url";
var cached = null;
function getPackageRoot() {
  if (cached) return cached;
  let dir = dirname(fileURLToPath2(import.meta.url));
  for (let i = 0; i < 10; i++) {
    if (existsSync2(join2(dir, "package.json"))) {
      cached = dir;
      return dir;
    }
    const parent = dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  throw new Error(
    `Unable to locate package root from ${fileURLToPath2(import.meta.url)}`
  );
}
function pkgPath(...segments) {
  return join2(getPackageRoot(), ...segments);
}

// src-ts/embedding/registry.ts
var cached2 = null;
function findRegistryPath() {
  return pkgPath("models", "registry.json");
}
function loadRegistry() {
  if (cached2) return cached2;
  const path = findRegistryPath();
  let raw;
  try {
    raw = readFileSync2(path, "utf8");
  } catch (err) {
    throw new Error(`Failed to read model registry at ${path}: ${err.message}`);
  }
  let parsed;
  try {
    parsed = JSON.parse(raw);
  } catch (err) {
    throw new Error(`Failed to parse model registry at ${path}: ${err.message}`);
  }
  if (parsed.version !== 1) {
    throw new Error(`Unsupported model registry version: ${parsed.version}`);
  }
  cached2 = {};
  for (const [name, spec] of Object.entries(parsed.models)) {
    cached2[name] = { name, ...spec };
  }
  return cached2;
}
function getModelSpec(name) {
  const reg = loadRegistry();
  const spec = reg[name];
  if (!spec) {
    const available = Object.keys(reg).join(", ");
    throw new Error(`Unknown model "${name}". Available: ${available}`);
  }
  return spec;
}
function expandUrl(template, revision) {
  return template.replace(/\{revision\}/g, revision);
}

// src-ts/embedding/model-manager.ts
import { existsSync as existsSync3, mkdirSync as mkdirSync2 } from "fs";
import { statSync, createReadStream } from "fs";
import { writeFile, rename, unlink, readFile } from "fs/promises";
import { Readable } from "stream";
import { pipeline } from "stream/promises";
import { createWriteStream } from "fs";
import { createHash as createHash2 } from "crypto";
import { join as join3, dirname as dirname2 } from "path";
import { fileURLToPath as fileURLToPath3 } from "url";
function getBundledModelPath(modelName) {
  const distDir = dirname2(fileURLToPath3(import.meta.url));
  return join3(distDir, "..", "models", modelName);
}
function getUserModelPath(modelName) {
  return join3(getDataDir(), "models", modelName);
}
function getModelPath(modelName) {
  const bundled = getBundledModelPath(modelName);
  try {
    const spec = getModelSpec(modelName);
    if (isModelStructurallyValid(bundled, spec)) return bundled;
  } catch {
  }
  return getUserModelPath(modelName);
}
function sleep(ms) {
  return new Promise((resolve2) => setTimeout(resolve2, ms));
}
async function downloadFile(url, dest, file, fileIndex, fileCount, options, maxRetries) {
  const { onProgress, signal, _sleep: sleepFn = sleep } = options;
  const tmpPath = `${dest}.tmp.${process.pid}.${Date.now()}`;
  let lastErr;
  for (let attempt = 0; attempt <= maxRetries; attempt++) {
    if (attempt > 0) {
      const delayMs = Math.min(500 * Math.pow(2, attempt - 1), 2e3);
      await sleepFn(delayMs);
    }
    try {
      const response = await fetch(url, { signal });
      if (!response.ok) {
        throw new Error(
          `Failed to download ${file} from ${url}: ${response.status} ${response.statusText}`
        );
      }
      if (response.body) {
        const contentLengthRaw = response.headers.get("content-length");
        const bytesTotal = contentLengthRaw ? parseInt(contentLengthRaw, 10) : 0;
        let bytesDone = 0;
        const nodeReadable = Readable.fromWeb(
          response.body
        );
        const writeStream = createWriteStream(tmpPath);
        nodeReadable.on("data", (chunk) => {
          bytesDone += chunk.byteLength;
          onProgress?.({ file, bytesDone, bytesTotal, fileIndex, fileCount });
        });
        try {
          await pipeline(nodeReadable, writeStream);
        } catch (err) {
          try {
            await unlink(tmpPath);
          } catch {
          }
          throw err;
        }
      } else {
        const buffer = Buffer.from(await response.arrayBuffer());
        const bytesDone = buffer.byteLength;
        const bytesTotal = buffer.byteLength;
        try {
          await writeFile(tmpPath, buffer);
        } catch (err) {
          try {
            await unlink(tmpPath);
          } catch {
          }
          throw err;
        }
        onProgress?.({ file, bytesDone, bytesTotal, fileIndex, fileCount });
      }
      if (existsSync3(dest)) {
        try {
          await unlink(dest);
        } catch {
        }
      }
      await rename(tmpPath, dest);
      return;
    } catch (err) {
      if (err instanceof Error && err.name === "AbortError") {
        try {
          await unlink(tmpPath);
        } catch {
        }
        throw err;
      }
      lastErr = err;
      try {
        await unlink(tmpPath);
      } catch {
      }
    }
  }
  throw lastErr;
}
async function downloadModel(modelName, options = {}) {
  const { maxRetries = 3 } = options;
  const spec = getModelSpec(modelName);
  const target = getUserModelPath(modelName);
  mkdirSync2(target, { recursive: true });
  const fileEntries = Object.entries(spec.files).map(([file, urlTemplate]) => ({
    file,
    url: expandUrl(urlTemplate, spec.revision)
  }));
  const fileCount = fileEntries.length;
  for (let i = 0; i < fileEntries.length; i++) {
    const { file, url } = fileEntries[i];
    const finalPath = join3(target, file);
    await downloadFile(url, finalPath, file, i, fileCount, options, maxRetries);
  }
  const onnxPath = join3(target, "model.onnx");
  const actualHash = await sha256File(onnxPath);
  if (actualHash !== spec.sha256) {
    try {
      await unlink(onnxPath);
    } catch {
    }
    throw new Error(
      `sha256 mismatch for model.onnx: expected ${spec.sha256}, actual ${actualHash}`
    );
  }
  const sidecarPath = join3(target, ".verified");
  await writeFileAtomic(sidecarPath, spec.sha256);
  return target;
}
async function sha256File(path) {
  return new Promise((resolve2, reject) => {
    const hash = createHash2("sha256");
    const stream = createReadStream(path);
    stream.on("data", (chunk) => hash.update(chunk));
    stream.on("end", () => resolve2(hash.digest("hex")));
    stream.on("error", reject);
  });
}
async function writeFileAtomic(dest, data) {
  const tmp = `${dest}.tmp.${process.pid}.${Date.now()}`;
  try {
    await writeFile(tmp, data);
    if (existsSync3(dest)) {
      try {
        await unlink(dest);
      } catch {
      }
    }
    await rename(tmp, dest);
  } catch (err) {
    try {
      await unlink(tmp);
    } catch {
    }
    throw err;
  }
}
function isModelStructurallyValid(modelPath, spec) {
  if (!existsSync3(modelPath)) return false;
  for (const file of Object.keys(spec.files)) {
    const p = join3(modelPath, file);
    if (!existsSync3(p)) return false;
  }
  try {
    const onnx = join3(modelPath, "model.onnx");
    const size = statSync(onnx).size;
    return size === spec.sizeBytes;
  } catch {
    return false;
  }
}
async function isModelChecksumValid(modelPath, spec) {
  const sidecarPath = join3(modelPath, ".verified");
  if (existsSync3(sidecarPath)) {
    try {
      const cached3 = (await readFile(sidecarPath, "utf8")).trim();
      if (cached3 === spec.sha256) return true;
    } catch {
    }
  }
  const onnxPath = join3(modelPath, "model.onnx");
  if (!existsSync3(onnxPath)) return false;
  let computed;
  try {
    computed = await sha256File(onnxPath);
  } catch {
    return false;
  }
  if (computed === spec.sha256) {
    await writeFileAtomic(sidecarPath, spec.sha256);
    return true;
  }
  return false;
}

// src-ts/embedding/errors.ts
var ModelNotReadyError = class extends Error {
  modelName;
  reason;
  hint;
  cause;
  constructor(details) {
    const base = `Model '${details.modelName}' not ready: ${details.reason}`;
    const msg = details.hint ? `${base} (${details.hint})` : base;
    super(msg);
    this.name = "ModelNotReadyError";
    this.modelName = details.modelName;
    this.reason = details.reason;
    this.hint = details.hint;
    this.cause = details.cause;
  }
};

// src-ts/embedding/bootstrap.ts
import * as lockfile from "proper-lockfile";
var defaultAcquireLock = async (targetDir) => {
  mkdirSync3(targetDir, { recursive: true });
  const release = await lockfile.lock(targetDir, {
    lockfilePath: join4(targetDir, ".bootstrap.lock"),
    retries: { retries: 60, minTimeout: 1e3, maxTimeout: 1e3 }
  });
  return { release: async () => {
    await release();
  } };
};
function classifyDownloadError(err) {
  const msg = err instanceof Error ? err.message : String(err);
  if (/sha256|checksum|hash/i.test(msg)) return "corrupted";
  return "failed";
}
function buildManualInstallHint(modelName, spec, modelPath) {
  const lines = [
    `To install ${modelName} manually, place these files in ${modelPath}:`
  ];
  for (const [filename, urlTemplate] of Object.entries(spec.files)) {
    const url = expandUrl(urlTemplate, spec.revision);
    lines.push(`  - ${filename} : ${url}`);
  }
  lines.push("Then retry session_start.");
  return lines.join("\n");
}
var ModelBootstrap = class {
  status;
  inFlight = null;
  getSpec;
  getModelPath;
  getUserModelPath;
  isStructurallyValid;
  isChecksumValid;
  download;
  acquireLock;
  constructor(modelName, options) {
    this.status = { state: "idle", modelName };
    this.getSpec = options?.getSpec ?? getModelSpec;
    this.getModelPath = options?.getModelPath ?? getModelPath;
    this.getUserModelPath = options?.getUserModelPath ?? getUserModelPath;
    this.isStructurallyValid = options?.isStructurallyValid ?? isModelStructurallyValid;
    this.isChecksumValid = options?.isChecksumValid ?? isModelChecksumValid;
    this.download = options?.download ?? ((name, opts) => downloadModel(name, opts));
    this.acquireLock = options?.acquireLock ?? defaultAcquireLock;
  }
  getStatus() {
    return { ...this.status };
  }
  /**
   * Trigger or observe bootstrap.
   * - If already ready: returns immediately with the cached path.
   * - If idle/checking: runs the validate-and-maybe-download flow once.
   * - If currently downloading from a previous call: returns the in-flight promise (single-flight).
   * - retry on re-call: if state === "failed", reset to "idle" and retry from scratch.
   */
  ensureReady() {
    if (this.status.state === "ready") {
      return Promise.resolve(this.status.modelPath);
    }
    if (this.status.state === "failed") {
      this.status.state = "idle";
      delete this.status.error;
      this.inFlight = null;
    }
    if (this.status.state === "downloading" && this.inFlight !== null) {
      return this.inFlight;
    }
    this.inFlight = this._runBootstrap();
    return this.inFlight;
  }
  async _runBootstrap() {
    const { modelName } = this.status;
    this.status.state = "checking";
    const spec = this.getSpec(modelName);
    const modelPath = this.getModelPath(modelName);
    const userModelPath = this.getUserModelPath(modelName);
    const structuralOk = this.isStructurallyValid(modelPath, spec);
    const checksumOk = structuralOk && await this.isChecksumValid(modelPath, spec);
    if (structuralOk && checksumOk) {
      this.status.state = "ready";
      this.status.modelPath = modelPath;
      this.inFlight = null;
      return modelPath;
    }
    this.status.state = "downloading";
    let lockHandle;
    try {
      lockHandle = await this.acquireLock(userModelPath);
    } catch (err) {
      this.status.state = "failed";
      const errMsg = err instanceof Error ? err.message : String(err);
      this.status.error = { reason: "failed", message: errMsg };
      this.inFlight = null;
      const hint = "Another process may be downloading the model; retry shortly.";
      throw new ModelNotReadyError({ modelName, reason: "failed", hint, cause: err });
    }
    try {
      const postLockStructuralOk = this.isStructurallyValid(modelPath, spec);
      const postLockChecksumOk = postLockStructuralOk && await this.isChecksumValid(modelPath, spec);
      if (postLockStructuralOk && postLockChecksumOk) {
        this.status.state = "ready";
        this.status.modelPath = modelPath;
        this.inFlight = null;
        return modelPath;
      }
      const resolvedPath = await this.download(modelName, {
        onProgress: (p) => {
          this.status.progress = p;
        }
      });
      this.status.state = "ready";
      this.status.modelPath = resolvedPath;
      this.inFlight = null;
      return resolvedPath;
    } catch (err) {
      const reason = classifyDownloadError(err);
      this.status.state = "failed";
      this.status.error = {
        reason,
        message: err instanceof Error ? err.message : String(err)
      };
      this.inFlight = null;
      const hint = buildManualInstallHint(modelName, spec, modelPath);
      throw new ModelNotReadyError({ modelName, reason, hint, cause: err });
    } finally {
      await lockHandle.release();
    }
  }
};

// src-ts/embedding/tokenizer.ts
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

// src-ts/embedding/embedder.ts
var Embedder = class {
  options;
  session = null;
  tokenizer = null;
  bootstrap = null;
  constructor(options) {
    this.options = options;
  }
  isLoaded() {
    return this.session !== null && this.tokenizer !== null;
  }
  async ensureLoaded() {
    if (this.isLoaded()) return;
    if (this.bootstrap === null) {
      if (this.options.bootstrapFactory) {
        this.bootstrap = this.options.bootstrapFactory(this.options.model);
      } else {
        this.bootstrap = new ModelBootstrap(this.options.model);
      }
    }
    const modelPath = await this.bootstrap.ensureReady();
    const onnxPath = join5(modelPath, "model.onnx");
    this.session = await ort.InferenceSession.create(onnxPath);
    const tokenizerPath = join5(modelPath, "tokenizer.json");
    const tokenizerText = await readFile2(tokenizerPath, "utf-8");
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

// src-ts/eval/benchmark-runner.ts
import { readFileSync as readFileSync3 } from "fs";

// src-ts/db/entries.ts
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
  db.run(`
    INSERT INTO ${table}
      (id, content, summary, source, source_tool, project, tags,
       created_at, updated_at, last_accessed_at, access_count,
       decay_score, parent_id, collection_id, metadata)
    VALUES
      (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
  `, [
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
  ]);
  return id;
}
function getEntry(db, tier, type, id) {
  const table = tableName(tier, type);
  const row = db.query(`SELECT * FROM ${table} WHERE id = ?`).get(id);
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
  db.run(`UPDATE ${table} SET ${setClauses.join(", ")} WHERE id = ?`, values);
}
function deleteEntry(db, tier, type, id) {
  const table = tableName(tier, type);
  db.run(`DELETE FROM ${table} WHERE id = ?`, [id]);
}

// src-ts/search/vector-search.ts
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

// src-ts/memory/store.ts
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

// src-ts/memory/get.ts
function getMemory(db, id) {
  for (const { tier, type } of ALL_TABLE_PAIRS) {
    const entry = getEntry(db, tier, type, id);
    if (entry) {
      return { entry, tier, content_type: type };
    }
  }
  return null;
}

// src-ts/memory/delete.ts
function deleteMemory(db, id) {
  const location = getMemory(db, id);
  if (!location) return false;
  deleteEmbedding(db, location.tier, location.content_type, id);
  deleteEntry(db, location.tier, location.content_type, id);
  return true;
}

// src-ts/search/fts-search.ts
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

// src-ts/memory/search.ts
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

// src-ts/eval/benchmark-runner.ts
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

// src-ts/eval/ci-smoke.ts
var SMOKE_PASS_THRESHOLD = 0.8;
var __dirname = dirname3(fileURLToPath4(import.meta.url));
var PACKAGE_ROOT = basename(__dirname) === "dist" ? resolve(__dirname, "..") : resolve(__dirname, "..", "..");
async function main() {
  const config = loadConfig();
  const db = new Database(":memory:");
  load(db);
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
/*! Bundled license information:

smol-toml/dist/error.js:
smol-toml/dist/util.js:
smol-toml/dist/date.js:
smol-toml/dist/primitive.js:
smol-toml/dist/extract.js:
smol-toml/dist/struct.js:
smol-toml/dist/parse.js:
smol-toml/dist/stringify.js:
smol-toml/dist/index.js:
  (*!
   * Copyright (c) Squirrel Chat et al., All rights reserved.
   * SPDX-License-Identifier: BSD-3-Clause
   *
   * Redistribution and use in source and binary forms, with or without
   * modification, are permitted provided that the following conditions are met:
   *
   * 1. Redistributions of source code must retain the above copyright notice, this
   *    list of conditions and the following disclaimer.
   * 2. Redistributions in binary form must reproduce the above copyright notice,
   *    this list of conditions and the following disclaimer in the
   *    documentation and/or other materials provided with the distribution.
   * 3. Neither the name of the copyright holder nor the names of its contributors
   *    may be used to endorse or promote products derived from this software without
   *    specific prior written permission.
   *
   * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
   * ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
   * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
   * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
   * FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
   * DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
   * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
   * CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
   * OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
   * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
   *)
*/
