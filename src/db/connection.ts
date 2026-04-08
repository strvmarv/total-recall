import { Database } from "bun:sqlite";
import { mkdirSync } from "node:fs";
import { dirname } from "node:path";
import * as sqliteVec from "sqlite-vec";
import { getDbPath } from "../config.js";
import { initSchema } from "./schema.js";
import { bootstrapSqlite } from "./sqlite-bootstrap.js";

let _db: Database | null = null;

export function getDb(): Database {
  if (_db) return _db;
  bootstrapSqlite();
  const dbPath = getDbPath();
  // Auto-create the parent directory. Handles both the default path
  // (<TOTAL_RECALL_HOME>/total-recall.db) and custom TOTAL_RECALL_DB_PATH
  // values pointing at deep/nested/locations.
  mkdirSync(dirname(dbPath), { recursive: true });
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
