import { Database } from "bun:sqlite";
import { existsSync } from "node:fs";

// macOS ships /usr/lib/libsqlite3.dylib without SQLITE_ENABLE_LOAD_EXTENSION,
// so bun:sqlite — which dlopens libsqlite3 at runtime — cannot call
// sqlite-vec's `loadExtension()` and fails with:
//
//   This build of sqlite3 does not support dynamic extension loading
//
// (See oven-sh/bun#5756.) Fix: point bun:sqlite at a libsqlite3 built with
// extension loading enabled via `Database.setCustomSQLite()` BEFORE any
// Database is constructed. Homebrew's keg-only sqlite qualifies.
//
// Linux distros build their system libsqlite3 with extension loading on by
// default, and Windows bun ships its own SQLite — so this bootstrap is a
// darwin-only concern today.

const DARWIN_SQLITE_CANDIDATES = [
  "/opt/homebrew/opt/sqlite/lib/libsqlite3.dylib", // Apple Silicon brew
  "/usr/local/opt/sqlite/lib/libsqlite3.dylib", // Intel brew
];

export class SqliteExtensionError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "SqliteExtensionError";
  }
}

let _bootstrapped = false;

/**
 * Ensure bun:sqlite is linked against a libsqlite3 that supports
 * dynamic extension loading. Idempotent and safe to call repeatedly;
 * the underlying `Database.setCustomSQLite()` only runs on the first
 * invocation, and only on darwin.
 *
 * Must be called before constructing any `Database` instance.
 *
 * @throws {SqliteExtensionError} on darwin when no extension-capable
 *   libsqlite3 can be found in the standard Homebrew locations.
 */
export function bootstrapSqlite(): void {
  if (_bootstrapped) return;
  _bootstrapped = true;

  if (process.platform !== "darwin") return;

  for (const candidate of DARWIN_SQLITE_CANDIDATES) {
    if (existsSync(candidate)) {
      Database.setCustomSQLite(candidate);
      return;
    }
  }

  throw new SqliteExtensionError(
    [
      "total-recall: no extension-capable libsqlite3 found on this Mac.",
      "",
      "macOS ships /usr/lib/libsqlite3.dylib without SQLITE_ENABLE_LOAD_EXTENSION,",
      "so sqlite-vec cannot be loaded. Install Homebrew sqlite to fix:",
      "",
      "  brew install sqlite",
      "",
      "total-recall will automatically pick it up from:",
      ...DARWIN_SQLITE_CANDIDATES.map((p) => `  - ${p}`),
    ].join("\n"),
  );
}
