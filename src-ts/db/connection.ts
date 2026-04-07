import { Database } from "bun:sqlite";
import { mkdirSync, existsSync } from "node:fs";
import { join } from "node:path";
import * as sqliteVec from "sqlite-vec";
import { getDataDir } from "../config.js";
import { initSchema } from "./schema.js";

let _db: Database | null = null;

export function getDb(): Database {
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
