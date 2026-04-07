/**
 * Vitest/Node shim for bun:sqlite.
 *
 * bun:sqlite is a Bun-only built-in. When tests run under Node/vitest, this shim
 * maps the bun:sqlite Database API onto better-sqlite3, which has a compatible
 * synchronous interface.
 *
 * Mapped APIs:
 *   new Database(path)   → BetterSqlite3(path)
 *   db.run(sql, params)  → db.prepare(sql).run(...params)
 *   db.query(sql).get()  → db.prepare(sql).get()
 *   db.query(sql).all()  → db.prepare(sql).all()
 *   db.transaction(fn)   → db.transaction(fn)  (identical)
 *   db.close()           → db.close()          (identical)
 */
// eslint-disable-next-line @typescript-eslint/no-require-imports
const BetterSqlite3 = require("better-sqlite3");

// Statement shim — wraps a better-sqlite3 Statement
class StatementShim {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  private stmt: any;

  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  constructor(stmt: any) {
    this.stmt = stmt;
  }

  /**
   * get() — works both as bun:sqlite style (array or object binding) and
   * as better-sqlite3 style (spread args, for unmigrated call sites).
   */
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  get(...args: any[]): unknown {
    if (args.length === 0) return this.stmt.get();
    // bun:sqlite passes a single array; better-sqlite3 uses spread
    if (args.length === 1 && Array.isArray(args[0])) return this.stmt.get(...args[0]);
    return this.stmt.get(...args);
  }

  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  all(...args: any[]): unknown[] {
    if (args.length === 0) return this.stmt.all();
    if (args.length === 1 && Array.isArray(args[0])) return this.stmt.all(...args[0]);
    return this.stmt.all(...args);
  }

  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  run(...args: any[]): unknown {
    if (args.length === 0) return this.stmt.run();
    if (args.length === 1 && Array.isArray(args[0])) return this.stmt.run(...args[0]);
    return this.stmt.run(...args);
  }
}

// Database shim — wraps better-sqlite3 Database with bun:sqlite API
export class Database {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  private db: any;

  constructor(path: string) {
    // eslint-disable-next-line @typescript-eslint/no-unsafe-call
    this.db = new BetterSqlite3(path);
  }

  /** bun:sqlite: db.run(sql, params?) */
  run(sql: string, params?: unknown): void {
    if (params === undefined) {
      // eslint-disable-next-line @typescript-eslint/no-unsafe-call, @typescript-eslint/no-unsafe-member-access
      this.db.prepare(sql).run();
    } else if (Array.isArray(params)) {
      // eslint-disable-next-line @typescript-eslint/no-unsafe-call, @typescript-eslint/no-unsafe-member-access
      this.db.prepare(sql).run(...params);
    } else {
      // eslint-disable-next-line @typescript-eslint/no-unsafe-call, @typescript-eslint/no-unsafe-member-access
      this.db.prepare(sql).run(params);
    }
  }

  /** bun:sqlite: db.query(sql) → Statement */
  query(sql: string): StatementShim {
    // eslint-disable-next-line @typescript-eslint/no-unsafe-call, @typescript-eslint/no-unsafe-member-access
    return new StatementShim(this.db.prepare(sql));
  }

  /** bun:sqlite: db.prepare(sql) — kept for compatibility */
  prepare(sql: string): StatementShim {
    // eslint-disable-next-line @typescript-eslint/no-unsafe-call, @typescript-eslint/no-unsafe-member-access
    return new StatementShim(this.db.prepare(sql));
  }

  /** bun:sqlite: db.transaction(fn) — identical to better-sqlite3 */
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  transaction<T extends (...args: any[]) => any>(fn: T): T {
    // eslint-disable-next-line @typescript-eslint/no-unsafe-call, @typescript-eslint/no-unsafe-member-access, @typescript-eslint/no-unsafe-return
    return this.db.transaction(fn);
  }

  /** bun:sqlite: db.close() */
  close(): void {
    // eslint-disable-next-line @typescript-eslint/no-unsafe-call, @typescript-eslint/no-unsafe-member-access
    this.db.close();
  }

  /**
   * Extension loading — used by sqlite-vec.load(db).
   * Delegates to better-sqlite3's loadExtension().
   */
  loadExtension(path: string): void {
    // eslint-disable-next-line @typescript-eslint/no-unsafe-call, @typescript-eslint/no-unsafe-member-access
    this.db.loadExtension(path);
  }
}

export default Database;
