# Relocating the database

By default, total-recall stores its SQLite database at `<TOTAL_RECALL_HOME>/total-recall.db` (typically `~/.total-recall/total-recall.db`). Set `TOTAL_RECALL_DB_PATH` to relocate **only** the database file — `config.toml`, the embedding model cache, and export directories stay anchored to `TOTAL_RECALL_HOME`.

**When to use it:**

- **Cloud-synced memories.** Point at a file under Dropbox / iCloud / OneDrive so your memory store survives machine loss and flows to other devices.
- **Shared database across workspaces.** Multiple Claude Code windows running in different project directories can read/write one memory store. The existing `project` field on memories gives you per-workspace views without needing separate DBs.

**How to configure:**

Shell export (simplest, per-terminal):

```bash
export TOTAL_RECALL_DB_PATH=~/Dropbox/total-recall/memories.db
```

Claude Code `.mcp.json` env block (per-host, survives shell sessions):

```json
{
  "mcpServers": {
    "total-recall": {
      "command": "node",
      "args": ["${CLAUDE_PLUGIN_ROOT}/bin/start.js"],
      "cwd": "${CLAUDE_PLUGIN_ROOT}",
      "env": {
        "TOTAL_RECALL_DB_PATH": "~/Dropbox/total-recall/memories.db"
      }
    }
  }
}
```

Two workspaces sharing one database:

> Set the same `TOTAL_RECALL_DB_PATH` in both workspace configs. Each Claude Code window opens its own SQLite connection; the OS's file locks serialize writes across processes. Use the `project` field on memories (set automatically from CWD when you store, filterable on `memory_search`) to keep per-workspace views clean.

**Rules:**

- Must be an absolute file path (e.g. `/Users/you/Dropbox/tr.db`), or start with `~/` which expands to your home directory.
- Bare `~` and trailing `/` or `\` are rejected — total-recall needs a full file path, not a directory.
- The parent directory is created automatically on first run if it doesn't exist.
- Invalid values cause the MCP server to **fail at startup** with a clear stderr message. No partial database will be created.
- The env var is read **once at startup**; changing it requires restarting the MCP host (close and reopen Claude Code).

**Cloud sync caveats (important):**

SQLite on sync drives is historically fragile. Dropbox in particular has mishandled `-wal` and `-shm` sidecar files under concurrent writes, leading to corruption. iCloud Drive is less exposed but still not guaranteed. We do **not** force a journal mode — you choose:

- If you see corruption symptoms on a sync drive, try `PRAGMA journal_mode=DELETE` in a one-shot sqlite CLI session to disable WAL. You accept worse concurrency in exchange for fewer sidecar files for the sync daemon to mishandle.
- See [https://www.sqlite.org/howtocorrupt.html](https://www.sqlite.org/howtocorrupt.html) for authoritative guidance on SQLite + network/sync filesystems.
- **Do not share the same DB file between machines while both are running.** Sync drives are eventually consistent; concurrent writes from two hosts will corrupt the DB because file locks are local-filesystem-only.

**Concurrent writers on the same machine (shared workspaces):**

- SQLite uses OS-level file locks. Two processes on the same machine writing to the same file serialize correctly — one waits, the other writes. No data loss.
- Reads are unblocked by other readers.
- For a memory system's write volume (a handful of stores per minute), contention is invisible.

**Manual migration recipe:**

If you already have a database at the default location and want to move it:

```bash
# 1. Stop the MCP server — close any Claude Code window(s) using total-recall
# 2. Move the file (and its sidecars, if WAL is active)
mv ~/.total-recall/total-recall.db    ~/Dropbox/total-recall/memories.db
mv ~/.total-recall/total-recall.db-wal ~/Dropbox/total-recall/memories.db-wal 2>/dev/null || true
mv ~/.total-recall/total-recall.db-shm ~/Dropbox/total-recall/memories.db-shm 2>/dev/null || true
# 3. Set the env var (shell, .mcp.json, or settings.json)
export TOTAL_RECALL_DB_PATH=~/Dropbox/total-recall/memories.db
# 4. Restart Claude Code or your MCP host
```

total-recall does **not** auto-migrate this kind of relocation — silent migration on partial failure invites corruption. (This is distinct from the automatic 0.7.x → 0.8.x TS-to-.NET schema migration, which IS handled by `AutoMigrationGuard` on first launch with a non-destructive backup of the old TS database to `total-recall.db.ts-backup`.)
