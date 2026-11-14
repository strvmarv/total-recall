# First run

What total-recall does the first time it starts, and how the engine gets onto the machine.

## Provisioning on first launch

total-recall is a prebuilt .NET 10 NativeAOT engine wrapped by a small Node launcher (`bin/start.js`) that picks the engine for the platform: `linux-x64`, `linux-arm64`, `osx-arm64` or `win-x64`. Intel Macs (`darwin-x64`) are not shipped; Apple Silicon is. The engine bundles its own SQLite, ONNX runtime, `sqlite-vec` extension and the `bge-small-en-v1.5` embedding model, so no `dotnet` runtime, `bun` or system SQLite is needed. Node.js 20 or later is needed for the launcher.

The npm package ships the engine for all four platforms, so an install through npm needs no download at run time.

A git-sourced plugin install (which is what the marketplace entry gives Claude Code) fetches the repository without `binaries/`, and git runs no install script, so the first launch downloads the engine. `bin/start.js` fetches the archive for the platform (about 100 MB, which includes the embedding model; 97 to 101 MB was observed for 4.7.0) from GitHub Releases and checks its sha256 against the release `provisioning.manifest.json`. The download runs inside the launcher: the MCP connection stays up the whole time, and any tool call meanwhile returns `{ "status": "not_ready", "phase": "provisioning" }`. Wait a few seconds and retry. When provisioning finishes the engine starts by itself and memory features come up with no restart.

## What happens on first run

1. Creates `~/.total-recall/` if it is missing.
2. Creates the SQLite database and applies the schema migrations.
3. Loads the bundled `bge-small-en-v1.5` ONNX embedding model from `models/`. Nothing is downloaded for it at run time: it was fetched and sha256-verified at build time and ships inside the archive.
4. Scans for existing memories from host tools (Claude Code, Copilot CLI, Cursor, Cline, OpenCode, Hermes) and deduplicates them by content hash.
5. Ingests the project's docs (README, `docs/` and so on) into a `<project>-project-docs` knowledge-base collection.
6. Runs a quick smoke test (a 22-query benchmark) to check retrieval quality.

## Upgrading from 0.7.x

If you are upgrading from 0.7.x (TypeScript) to 0.8.x (.NET), the `AutoMigrationGuard` runs on first launch:

- Detects an existing TS-format database by the absence of the `.NET` schema marker.
- Renames the existing `total-recall.db` to `total-recall.db.ts-backup`. The original is **never deleted**.
- Runs the .NET MigrationRunner against a fresh database, then re-imports the TS data with re-embedding (the .NET tokenizer is canonical BERT BasicTokenization, slightly more accurate than the earlier hand-rolled WordPiece).
- If the migration is interrupted partway and both `total-recall.db` and `total-recall.db.ts-backup` are on disk, the next launch's guard handles all 5 partial-state cases automatically (since 0.8.0-beta.7).

To move the database to another location, see [Relocating the database](database-location.md).
