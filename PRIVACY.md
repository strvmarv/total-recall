# Privacy Policy

**total-recall** — last updated November 2026

---

## Summary

By default, total-recall stores all data **locally on your machine**, and no data is transmitted to any external server, analytics service, or third party. The plugin has no network activity except the cases described under "What data leaves your machine" below, including the configurations you can opt in to.

---

## What data is collected and where it is stored

By default, total-recall stores the following data in a SQLite database on your local filesystem (default: `~/.total-recall/total-recall.db`):

| Data | Purpose |
|------|---------|
| Memories you store explicitly via `memory_store` | Persist context across sessions |
| Memories auto-imported from other AI tools (Claude Code, Copilot CLI, Cursor, Cline, OpenCode) | Consolidate existing memory into one store |
| Knowledge base documents you ingest via `kb_ingest_file` / `kb_ingest_dir` | Semantic retrieval of your own docs |
| Vector embeddings of the above | Semantic similarity search (computed locally by default) |
| Session metadata (timestamps, access counts, compaction log) | Tier management and eval reporting |
| Retrieval event logs | Local eval framework (`eval_report`, `eval_compare`) |
| Config snapshots | Track how config changes affect retrieval quality |

You control where the database lives by setting `TOTAL_RECALL_DB_PATH`.

---

## What data leaves your machine

### 1. Binary download on first launch (conditional)

If you install total-recall via Claude Code's `/plugin` flow using a `source: github` marketplace entry, the `bin/start.js` shim downloads a prebuilt binary archive (about 100 MB) from **GitHub Releases** (`github.com/strvmarv/total-recall`) on first launch and verifies it by sha256 against the release `provisioning.manifest.json` before use. This is a one-time fetch of a compiled executable — no memory data is sent. The download URL is derived from the version in `package.json` and hits only `github.com` and `objects.githubusercontent.com`. No new data leaves the machine during this process.

This does **not** happen when installing via `npm install -g @strvmarv/total-recall` — all platform binaries ship inside the npm tarball.

### 2. ONNX embedding model (no runtime download)

The embedding model (`bge-small-en-v1.5`, ~133 MB) is fetched and sha256-verified at release **build** time and ships bundled inside the release/npm artifact in the `models/` directory. The runtime does **not** download it from HuggingFace or anywhere else — if the bundled model is missing, the binary fails fast with a clear error rather than reaching out to the network.

### 3. Web UI (local only)

`total-recall ui` binds a local loopback port (default 5577) and serves the management SPA with an ephemeral per-launch bearer token embedded in the page. No new data leaves the machine: the UI dispatches to the same local tool handlers as the MCP server, and the pricing table used to estimate costs in the Usage section is bundled local config — cost figures are estimates, not billed amounts.

### 4. Configurations you opt in to

Three optional configurations send data off your machine, to a system you choose and configure:

- **A Postgres backend** (`[storage] connection_string`): memories and knowledge are stored in that database instead of the local SQLite file.
- **Cortex** (`[storage] mode = "cortex"`): memories and the knowledge base entries you ingest (including the project docs ingested at session start) are synced to the Cortex instance you configure, telemetry (usage, retrieval events, the compaction log) is pushed to it, team knowledge base queries go to it, and your skills are uploaded only when `[skills] auto_import` is on.
- **A remote embedder** (`[embedding] provider = "openai"` or `"bedrock"`): the text to embed is sent to that service.

See [Configuration](docs/configuration.md) and [Connecting to Cortex](docs/cortex.md).

### 5. Nothing else

By default there is no telemetry, no crash reporting, no usage analytics, no cloud sync and no account system, and the plugin does not phone home.

---

## Embeddings and AI processing

By default, vector embeddings are computed **locally** using the bundled ONNX runtime and `bge-small-en-v1.5` model, and your text is not sent to an embedding API or any remote model service. That changes if you configure a remote embedder (above); with Cortex, synced content and knowledge base queries are processed by the Cortex instance.

---

## Data you ingest from other tools

On `session_start`, total-recall scans known memory locations for Claude Code, Copilot CLI, Cursor, Cline, OpenCode, and Hermes and imports entries it hasn't seen before. This import is local filesystem access only — no network calls are made by default (with a Postgres or Cortex backend, imported entries are stored or synced like any other memory, and with a remote embedder their text is sent to it to be embedded). Imported entries are deduplicated via content hash in an `import_log` table.

---

## Data retention and deletion

All data is yours. Most memories age out automatically (decay moves unused entries to colder tiers over time), but entries you mark **sticky** (via `memory_pin`) are exempt from decay and compaction — they persist indefinitely until you explicitly unpin (`memory_unpin`) or delete them.

To delete everything:

```bash
rm -rf ~/.total-recall/
```

To delete only memories while keeping config:

```bash
rm ~/.total-recall/total-recall.db
```

By default there is no remote copy to request deletion of. The commands above do not remove a copy in Postgres or Cortex; delete it in that system.

---

## Cross-device sync

By default total-recall does not provide cloud sync (Cortex, above, is the opt-in exception). If you point `TOTAL_RECALL_DB_PATH` at a folder managed by a third-party sync service (Dropbox, iCloud Drive, OneDrive, etc.), that service's own privacy policy governs the data in transit and at rest on their infrastructure.

---

## Contact

Questions or concerns: open an issue at [github.com/strvmarv/total-recall/issues](https://github.com/strvmarv/total-recall/issues).
