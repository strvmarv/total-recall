# Privacy Policy

**total-recall** — last updated April 2026

---

## Summary

total-recall stores all data **locally on your machine**. No data is transmitted to any external server, analytics service, or third party. The plugin has no network activity except two narrow, opt-in cases described below.

---

## What data is collected and where it is stored

total-recall stores the following data in a SQLite database on your local filesystem (default: `~/.total-recall/total-recall.db`):

| Data | Purpose |
|------|---------|
| Memories you store explicitly via `memory_store` | Persist context across sessions |
| Memories auto-imported from other AI tools (Claude Code, Copilot CLI, Cursor, Cline, OpenCode) | Consolidate existing memory into one store |
| Knowledge base documents you ingest via `kb_ingest_file` / `kb_ingest_dir` | Semantic retrieval of your own docs |
| Vector embeddings of the above | Semantic similarity search (computed locally) |
| Session metadata (timestamps, access counts, compaction log) | Tier management and eval reporting |
| Retrieval event logs | Local eval framework (`eval_report`, `eval_compare`) |
| Config snapshots | Track how config changes affect retrieval quality |

You control where the database lives by setting `TOTAL_RECALL_DB_PATH`.

---

## What data leaves your machine

### 1. Binary download on first launch (conditional)

If you install total-recall via Claude Code's `/plugin` flow using a `source: github` marketplace entry, the `bin/start.js` launcher downloads a prebuilt binary archive (~22 MB) from **GitHub Releases** (`github.com/strvmarv/total-recall`) on first launch. This is a one-time fetch of a compiled executable — no memory data is sent. The download URL is derived from the version in `package.json` and hits only `github.com` and `objects.githubusercontent.com`.

This does **not** happen when installing via `npm install -g @strvmarv/total-recall` — all platform binaries ship inside the npm tarball.

### 2. ONNX model fallback download (conditional)

The embedding model (`all-MiniLM-L6-v2`) is bundled in the `models/` directory. If the model file is missing (e.g., after a partial install or failed Git LFS fetch), the runtime downloads it from **HuggingFace** (`huggingface.co`). This is a one-time fetch of model weights — no memory data is sent.

### 3. Nothing else

There is no telemetry, no crash reporting, no usage analytics, no cloud sync, and no account system. The plugin does not phone home.

---

## Embeddings and AI processing

Vector embeddings are computed **locally** using the bundled ONNX runtime and `all-MiniLM-L6-v2` model. Your text is never sent to an embedding API or any remote model service.

---

## Data you ingest from other tools

On `session_start`, total-recall scans known memory locations for Claude Code, Copilot CLI, Cursor, Cline, OpenCode, and Hermes and imports entries it hasn't seen before. This import is local filesystem access only — no network calls are made. Imported entries are deduplicated via content hash in an `import_log` table.

---

## Data retention and deletion

All data is yours. To delete everything:

```bash
rm -rf ~/.total-recall/
```

To delete only memories while keeping config:

```bash
rm ~/.total-recall/total-recall.db
```

There is no remote copy to request deletion of.

---

## Cross-device sync

total-recall does not provide cloud sync. If you point `TOTAL_RECALL_DB_PATH` at a folder managed by a third-party sync service (Dropbox, iCloud Drive, OneDrive, etc.), that service's own privacy policy governs the data in transit and at rest on their infrastructure.

---

## Contact

Questions or concerns: open an issue at [github.com/strvmarv/total-recall/issues](https://github.com/strvmarv/total-recall/issues).
