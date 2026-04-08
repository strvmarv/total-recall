import { Database } from "bun:sqlite";
import { initSchema } from "../../src/db/schema.js";
import { bootstrapSqlite } from "../../src/db/sqlite-bootstrap.js";
import * as sqliteVec from "sqlite-vec";

export function createTestDb(): Database {
  bootstrapSqlite();
  const db = new Database(":memory:");
  sqliteVec.load(db);
  initSchema(db);
  return db;
}
