import { describe, it, expect } from "vitest";
import { existsSync } from "node:fs";
import { Database } from "bun:sqlite";
import * as sqliteVec from "sqlite-vec";
import { bootstrapSqlite, SqliteExtensionError } from "./sqlite-bootstrap.js";

// NOTE: Database.setCustomSQLite is process-global and can only be invoked
// once per process. These tests therefore assert the observable contract
// from a single bootstrap call — idempotency, and that sqlite-vec actually
// loads afterwards — rather than exercising reset/re-bootstrap paths.

describe("sqlite-bootstrap", () => {
  const brewPaths = [
    "/opt/homebrew/opt/sqlite/lib/libsqlite3.dylib",
    "/usr/local/opt/sqlite/lib/libsqlite3.dylib",
  ];
  const darwinWithBrewSqlite =
    process.platform === "darwin" && brewPaths.some((p) => existsSync(p));
  const darwinWithoutBrewSqlite =
    process.platform === "darwin" && !brewPaths.some((p) => existsSync(p));

  it("is idempotent — repeated calls never throw", () => {
    try {
      bootstrapSqlite();
    } catch (e) {
      // On a bare darwin host this is expected; the second call must still
      // be a no-op and not surface a new error.
      expect(e).toBeInstanceOf(SqliteExtensionError);
    }
    expect(() => bootstrapSqlite()).not.toThrow();
    expect(() => bootstrapSqlite()).not.toThrow();
  });

  it.runIf(process.platform !== "darwin")(
    "is a no-op on non-darwin platforms",
    () => {
      expect(() => bootstrapSqlite()).not.toThrow();
    },
  );

  it.runIf(darwinWithBrewSqlite)(
    "enables sqlite-vec extension loading on darwin with brew sqlite",
    () => {
      bootstrapSqlite();
      const db = new Database(":memory:");
      expect(() => sqliteVec.load(db)).not.toThrow();
      db.close();
    },
  );

  it.runIf(darwinWithoutBrewSqlite)(
    "throws SqliteExtensionError on darwin without brew sqlite",
    () => {
      expect(() => bootstrapSqlite()).toThrow(SqliteExtensionError);
    },
  );
});
