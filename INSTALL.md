# total-recall Installation Guide

This file is designed to be read by AI coding assistants. If you're an LLM helping a user install total-recall, follow the instructions for their platform below.

## What total-recall ships as

Since 0.8.0, total-recall is a **prebuilt .NET 8 NativeAOT binary** wrapped by a tiny Node launcher (`bin/start.js`) that detects the host platform and exec's the right per-RID binary. The npm package ships binaries for `linux-x64`, `linux-arm64`, `osx-arm64`, and `win-x64`. No `dotnet` runtime, no `bun`, and no system SQLite needed — the binary bundles its own `libe_sqlite3`, `libonnxruntime`, `vec0` (sqlite-vec extension), and the `all-MiniLM-L6-v2` ONNX embedding model.

## Prerequisites

- **Node.js >= 20.0.0** — required only for `npm install` and the `bin/start.js` launcher (~60 lines, zero runtime overhead). The actual MCP server is the prebuilt .NET binary.
- **Internet access** — only needed if you install via Claude Code's `/plugin` flow with a `source: github` marketplace entry. In that case `bin/start.js` downloads the matching per-RID archive (~22 MB) from GitHub Releases on first launch. The npm install path ships all RIDs in the tarball and doesn't need a runtime download.
- **Intel Mac (`darwin-x64`) is not currently shipped.** Apple Silicon (`osx-arm64`) is. All Apple hardware sold since November 2020 is arm64.

## Relocating the database

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

## Quick Install (Any Platform)

The MCP server is available as an npm package:

```bash
npm install -g @strvmarv/total-recall
```

This installs the current stable version (`0.7.2` TypeScript at the time of this writing; will be `0.8.x` .NET after the cutover). To install the .NET beta:

```bash
npm install -g @strvmarv/total-recall@beta
```

## Claude Code

### Option A: Plugin Install (Recommended)

Run in Claude Code:

```
/plugin install total-recall@strvmarv-total-recall-marketplace
```

If the marketplace isn't registered yet:

```
/plugin marketplace add strvmarv/total-recall-marketplace
/plugin install total-recall@strvmarv-total-recall-marketplace
```

### Option B: MCP Server Only (any tool)

Add to your Claude Code MCP config (`~/.claude.json`):

```json
{
  "mcpServers": {
    "total-recall": {
      "command": "total-recall"
    }
  }
}
```

This requires `npm install -g @strvmarv/total-recall` (or `@beta`) so the `total-recall` command is on PATH.

## GitHub Copilot CLI

Add to your Copilot CLI MCP config (`~/.copilot/mcp-config.json`):

```json
{
  "mcpServers": {
    "total-recall": {
      "command": "total-recall"
    }
  }
}
```

## OpenCode

See `.opencode/INSTALL.md` for the full OpenCode-specific install guide. The short version: `npm install -g @strvmarv/total-recall` then add the same `mcpServers` block to your OpenCode config.

## Cline (VS Code)

Add to your Cline MCP settings:

1. Open VS Code Command Palette
2. Search "Cline: MCP Settings"
3. Add:
```json
{
  "mcpServers": {
    "total-recall": {
      "command": "total-recall"
    }
  }
}
```

## Cursor

Add to your Cursor MCP config (`.cursor/mcp.json` or global settings):

```json
{
  "mcpServers": {
    "total-recall": {
      "command": "total-recall"
    }
  }
}
```

For full plugin support (skills + hooks), clone the repo:
```bash
git clone https://github.com/strvmarv/total-recall.git
```

## Verification

After installation:

```bash
total-recall --version
# Expected: total-recall 0.8.0 (or 0.7.2 if you're on stable)

total-recall status
# Expected: tier counts, KB info, embedding model "all-MiniLM-L6-v2", schema version
```

In a Claude Code (or other host) session, the first session output should include:

```
total-recall: initialized · X memories imported · Y docs ingested · system verified
```

You can verify it's working with:
- `/total-recall:commands status` — shows tier sizes and health
- `/total-recall:commands search "test"` — runs a test search

## What Happens on First Run

1. Creates `~/.total-recall/` directory if missing
2. Creates SQLite database with schema (`Schema.cs` MigrationRunner applies all migrations 1..5)
3. Loads the bundled `all-MiniLM-L6-v2` ONNX embedding model from `models/` (no download needed)
4. Scans for existing memories from host tools (Claude Code, Copilot CLI, Cursor, Cline, OpenCode, Hermes), deduplicates via content hash
5. Auto-ingests project docs (README, docs/, etc.) into a `<project>-project-docs` KB collection
6. Runs a quick smoke test (22-query benchmark) to verify retrieval quality

If you're upgrading from 0.7.x (TypeScript) to 0.8.x (.NET), the `AutoMigrationGuard` runs on first launch:

- Detects an existing TS-format database via the absence of the `.NET` schema marker
- Renames the existing `total-recall.db` to `total-recall.db.ts-backup` (the original is **never deleted**)
- Runs the .NET MigrationRunner against a fresh database, then re-imports the TS data with re-embedding (the .NET tokenizer is canonical BERT BasicTokenization, slightly more accurate than the prior hand-rolled WordPiece — see `docs/superpowers/specs/2026-04-07-rewrite-language-evaluation.md`).
- If the migration is interrupted partway through and you end up with both `total-recall.db` and `total-recall.db.ts-backup` on disk, the next launch's guard handles all 5 partial-state cases automatically (since 0.8.0-beta.7).
