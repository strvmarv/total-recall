import Database from "better-sqlite3";
import { initSchema } from "../../src/db/schema.js";
import * as sqliteVec from "sqlite-vec";

export function createTestDb(): Database.Database {
  const db = new Database(":memory:");
  sqliteVec.load(db);
  initSchema(db);
  return db;
}
