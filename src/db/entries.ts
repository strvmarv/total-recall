import { randomUUID } from "node:crypto";
import type Database from "better-sqlite3";
import type { Tier, ContentType, Entry, EntryRow } from "../types.js";
import { tableName } from "../types.js";

export interface InsertEntryOpts {
  content: string;
  summary?: string | null;
  source?: string | null;
  source_tool?: string | null;
  project?: string | null;
  tags?: string[];
  parent_id?: string | null;
  collection_id?: string | null;
  metadata?: Record<string, unknown>;
}

export interface UpdateEntryOpts {
  content?: string;
  summary?: string | null;
  tags?: string[];
  project?: string | null;
  decay_score?: number;
  metadata?: Record<string, unknown>;
  touch?: boolean;
}

export interface ListEntriesOpts {
  project?: string | null;
  includeGlobal?: boolean;
  orderBy?: string;
  limit?: number;
}

function rowToEntry(row: EntryRow): Entry {
  return {
    id: row.id,
    content: row.content,
    summary: row.summary,
    source: row.source,
    source_tool: row.source_tool as Entry["source_tool"],
    project: row.project,
    tags: row.tags ? (JSON.parse(row.tags) as string[]) : [],
    created_at: row.created_at,
    updated_at: row.updated_at,
    last_accessed_at: row.last_accessed_at,
    access_count: row.access_count,
    decay_score: row.decay_score,
    parent_id: row.parent_id,
    collection_id: row.collection_id,
    metadata: row.metadata ? (JSON.parse(row.metadata) as Record<string, unknown>) : {},
  };
}

export function insertEntry(
  db: Database.Database,
  tier: Tier,
  type: ContentType,
  opts: InsertEntryOpts,
): string {
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
    1.0,
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

  if (!row) return null;
  return rowToEntry(row);
}

export function updateEntry(
  db: Database.Database,
  tier: Tier,
  type: ContentType,
  id: string,
  opts: UpdateEntryOpts,
): void {
  const table = tableName(tier, type);
  const now = Date.now();

  const setClauses: string[] = ["updated_at = ?"];
  const values: unknown[] = [now];

  if (opts.content !== undefined) {
    setClauses.push("content = ?");
    values.push(opts.content);
  }
  if (opts.summary !== undefined) {
    setClauses.push("summary = ?");
    values.push(opts.summary);
  }
  if (opts.tags !== undefined) {
    setClauses.push("tags = ?");
    values.push(JSON.stringify(opts.tags));
  }
  if (opts.project !== undefined) {
    setClauses.push("project = ?");
    values.push(opts.project);
  }
  if (opts.decay_score !== undefined) {
    setClauses.push("decay_score = ?");
    values.push(opts.decay_score);
  }
  if (opts.metadata !== undefined) {
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

export function deleteEntry(
  db: Database.Database,
  tier: Tier,
  type: ContentType,
  id: string,
): void {
  const table = tableName(tier, type);
  db.prepare(`DELETE FROM ${table} WHERE id = ?`).run(id);
}

const ALLOWED_ORDER_COLUMNS = new Set([
  "created_at",
  "updated_at",
  "last_accessed_at",
  "access_count",
  "decay_score",
  "content",
]);

export function listEntries(
  db: Database.Database,
  tier: Tier,
  type: ContentType,
  opts?: ListEntriesOpts,
): Entry[] {
  const table = tableName(tier, type);

  const orderParts = (opts?.orderBy ?? "created_at DESC").split(" ");
  const column = orderParts[0]!;
  const direction = orderParts[1]?.toUpperCase() === "ASC" ? "ASC" : "DESC";
  if (!ALLOWED_ORDER_COLUMNS.has(column)) {
    throw new Error(`Invalid orderBy column: ${column}`);
  }
  const orderBy = `${column} ${direction}`;

  let sql: string;
  let params: unknown[];

  if (opts?.project !== undefined && opts.project !== null) {
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

  if (opts?.limit !== undefined) {
    sql += " LIMIT ?";
    params.push(opts.limit);
  }

  const rows = db.prepare(sql).all(...params) as EntryRow[];
  return rows.map(rowToEntry);
}

export function countEntries(
  db: Database.Database,
  tier: Tier,
  type: ContentType,
): number {
  const table = tableName(tier, type);
  const row = db.prepare(`SELECT COUNT(*) as count FROM ${table}`).get() as {
    count: number;
  };
  return row.count;
}

export function listEntriesByMetadata(
  db: Database.Database,
  tier: Tier,
  type: ContentType,
  metadataFilter: Record<string, string>,
  opts?: { orderBy?: string; limit?: number },
): Entry[] {
  const table = tableName(tier, type);

  const orderParts = (opts?.orderBy ?? "created_at DESC").split(" ");
  const column = orderParts[0]!;
  const direction = orderParts[1]?.toUpperCase() === "ASC" ? "ASC" : "DESC";
  if (!ALLOWED_ORDER_COLUMNS.has(column)) {
    throw new Error(`Invalid orderBy column: ${column}`);
  }
  const orderBy = `${column} ${direction}`;

  const filterKeys = Object.keys(metadataFilter);
  const whereClauses = filterKeys.map(
    (key) => `json_extract(metadata, '$.${key}') = ?`,
  );
  const params: unknown[] = filterKeys.map((key) => metadataFilter[key]);

  let sql = `SELECT * FROM ${table} WHERE ${whereClauses.join(" AND ")} ORDER BY ${orderBy}`;

  if (opts?.limit !== undefined) {
    sql += " LIMIT ?";
    params.push(opts.limit);
  }

  const rows = db.prepare(sql).all(...params) as EntryRow[];
  return rows.map(rowToEntry);
}

export function moveEntry(
  db: Database.Database,
  fromTier: Tier,
  fromType: ContentType,
  toTier: Tier,
  toType: ContentType,
  id: string,
): void {
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
      JSON.stringify(entry.metadata),
    );

    deleteEntry(db, fromTier, fromType, id);
  });

  doMove();
}
