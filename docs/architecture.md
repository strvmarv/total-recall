# Architecture

```
npm wrapper layer (Node, zero runtime dependencies):
  bin/start.js (MCP bootstrap shim) — comes up instantly; answers initialize/ping/tools/list
    from catalog.json before the engine is ready; provisions (sha256-verified download via
    release provisioning.manifest.json) + spawns + proxies the engine; supervises and
    restarts on crash; the MCP connection never drops (no more MCP error -32000 on first
    launch after an update); emits notifications/tools/list_changed once proxying begins.

MCP Server (.NET 10 NativeAOT — C# imperative shell + F# functional core)
├── TotalRecall.Core (F#)        — pure functions: tokenizer, decay, hybrid ranking, parsers, chunker
├── TotalRecall.Infrastructure   — SQLite/Postgres storage, ONNX/remote embedder, importers, migrations
├── TotalRecall.Server           — MCP JSON-RPC server, 42 core tool handlers (53–54 with mode-dependent tools), lifecycle
├── TotalRecall.Web              — embedded ASP.NET Core minimal API + React SPA (the web UI)
├── TotalRecall.Cli              — CLI commands (status, eval, kb, memory, config, migrate, ui)
└── TotalRecall.Host             — composition root, AOT entry point, migration guard

Tiers:
  Hot (50 entries, 1200 chars/entry) → injected at session start; earned from warm by access
    └─ sticky flag → user-pinned; injected first, unbounded, never decays/compacts/evicts
  Warm (10K entries, default ingress) → BM25 + cosine hybrid search per query; promotes to hot on merit
  Cold (unlimited)   → hierarchical KB retrieval

Backends (selected by config):
  Local:    SQLite + sqlite-vec + bundled ONNX embedder (default, zero config)
  Postgres: Postgres/pgvector + HNSW indexes + tsvector FTS + per-user visibility
  Cortex:   Local SQLite + write-local-then-enqueue sync to Cortex; remote queries for global KB
```

**Data flow:**

1. `store` — write a memory, assign tier (warm by default), embed, persist
2. `search` — embed query, BM25 + cosine vector search across all tiers, merge with F# ranking, return results
3. `compact` — decay scores, compact hot→warm (summarize), demote warm→cold; earned warm→hot promotion runs in the warm sweep
4. `ingest` — chunk files with heading-aware Markdown and regex-based code parsing, embed chunks, store in cold tier

**Local mode:** all state lives in `~/.total-recall/total-recall.db`. The embedding model and the sqlite-vec native extension are bundled with the binary. Nothing else needs the network at runtime; by default the only outbound request is the periodic update check (about once a day) to `api.github.com`, off with `updates.check = false` or `TOTAL_RECALL_NO_UPDATE_CHECK`.

**Cortex mode:** user memories write locally first for low latency. A `RoutingStore` wraps every write: persist locally, enqueue to `sync_queue`. A background sync loop flushes the queue to Cortex every `sync_interval_seconds` (default: 300) and at session boundaries. Global knowledge (team KB, connectors) is read directly from Cortex.

## Core technologies

- [.NET 10 / NativeAOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/) — single-binary deployment, no runtime dependency
- [F# Core](https://learn.microsoft.com/en-us/dotnet/fsharp/) — pure functional core: tokenizer, parsers, decay, hybrid ranking
- [Microsoft.Data.Sqlite](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/) — embedded SQLite with extension loading
- [sqlite-vec](https://github.com/asg017/sqlite-vec) — vector similarity search in SQLite (loaded as a native extension via `LoadExtension`)
- [Microsoft.ML.OnnxRuntime](https://onnxruntime.ai/docs/get-started/with-csharp.html) — local ML inference, AOT-compatible
- [Microsoft.ML.Tokenizers](https://learn.microsoft.com/en-us/dotnet/api/microsoft.ml.tokenizers) — canonical BERT BasicTokenization + WordPiece
- [bge-small-en-v1.5](https://huggingface.co/BAAI/bge-small-en-v1.5) — sentence embeddings (384d, CLS pooling)
- Hand-rolled JSON-RPC stdio MCP server in `TotalRecall.Server` (no SDK dependency)
- [Spectre.Console](https://spectreconsole.net/) — CLI rendering for `total-recall status` / `eval` / `kb list`
