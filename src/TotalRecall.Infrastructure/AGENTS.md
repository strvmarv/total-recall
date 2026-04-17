# TotalRecall.Infrastructure — Agent Guide

The infrastructure layer owns all side-effects: storage, embedding, importers, ingestion,
telemetry, search, and Cortex sync. Pure logic lives in `TotalRecall.Core` (F#).

**AOT requirement**: `IsAotCompatible=true` and `InvariantGlobalization=true` are set in the csproj.
No reflection, no dynamic code generation. All JSON via source-generated `JsonContext`.

---

## Subsystem Map

| Directory | Interfaces / Key Types | Purpose |
|-----------|----------------------|---------|
| `Storage/` | `IStore`, `SqliteStore`, `PostgresStore`, `RoutingStore` | CRUD for memory/knowledge entries across tiers |
| `Embedding/` | `IEmbedder`, `OnnxEmbedder`, `RemoteEmbedder`, `EmbedderFactory` | Embedding generation (local ONNX or remote API) |
| `Search/` | `IVectorSearch`, `IHybridSearch`, `IFtsSearch`, `VectorSearch`, `HybridSearch` | Vector + FTS hybrid retrieval |
| `Importers/` | `IImporter`, `ClaudeCodeImporter`, `CopilotCliImporter`, … | One-way import from host tool memory dirs |
| `Ingestion/` | `IFileIngester`, `FileIngester`, `HierarchicalIndex`, `IngestValidator` | File/dir ingestion into cold KB tier |
| `Telemetry/` | `CompactionLog`, `UsageEventLog`, `UsageDailyRollup`, `UsageWatermarkStore` | Retrieval events, compaction history, usage telemetry |
| `Usage/` | `IUsageImporter`, `UsageIndexer`, `UsageQueryService` | Token usage tracking and query |
| `Sync/` | `CortexClient`, `SyncQueue`, `SyncService`, `PeriodicSync`, `RoutingStore` | Bidirectional Cortex sync |
| `Config/` | `ConfigLoader`, `TomlConfig` | TOML config loading + `~/.total-recall/config.toml` |
| `Diagnostics/` | `ExceptionLogger` | AOT-safe exception chain logging |
| `Migration/` | `MigrationRunner`, `PostgresMigrationRunner` | Sequential schema migration framework |
| `Json/` | `JsonContext` (Infrastructure-level) | Source-generated JSON for Infrastructure DTOs |
| `Memory/` | Memory-specific helpers | Memory operation utilities |
| `Eval/` | Eval query services | Retrieval quality metrics |

---

## Storage Layer

### IStore

```csharp
interface IStore
{
    Task<Entry?> GetAsync(string id, ...);
    Task<IReadOnlyList<Entry>> ListAsync(Tier tier, ContentType type, ...);
    Task StoreAsync(Entry entry, ...);
    Task UpdateAsync(string id, ...);
    Task DeleteAsync(string id, ...);
    Task MoveAsync(string id, Tier targetTier, ...);  // for promote/demote
}
```

**Borrowed-connection pattern**: `SqliteStore` accepts a `SqliteConnection` and does NOT dispose it.
The process-lifetime connection is owned by `ServerComposition.OpenSqlite()` → `ServerCompositionHandles`.

### Backend Selection

`ServerComposition.OpenProduction()` reads `[storage] mode` from config:
- `"local"` (default) → `SqliteStore` + `VectorSearch` (sqlite-vec)
- `"postgres"` → `PostgresStore` + `PgvectorSearch`
- `"cortex"` → `RoutingStore` wrapping `SqliteStore` + `CortexClient` for bidirectional sync

**Fallback**: If Cortex/Postgres fails to open, the composition root catches the exception and
falls back to SQLite, setting `StorageMode` to `"sqlite (cortex failed)"`.

### Schema

8 sequential migrations in `Storage/Schema.cs`. **Never modify existing migrations** — only append.

| Migration | What it adds |
|-----------|-------------|
| 1 | 6 content tables (hot/warm/cold × memory/knowledge) + vec0 virtual tables + indexes + system tables |
| 2 | `_meta` KV store + `benchmark_candidates` |
| 3 | FTS5 virtual tables + insert/delete/update triggers + backfill |
| 4 | `compaction_log.source` column (default `'compaction'`) |
| 5 | Orphan row cleanup (one-time hot-fix for 0.6.7) |
| 6 | Usage telemetry: `usage_events`, `usage_daily`, `usage_watermarks` |
| 7 | `sync_queue` for Cortex outbound buffering |
| 8 | `scope TEXT NOT NULL DEFAULT ''` on all 6 content tables + indexes |

Table naming: `{tier}_{type}` (e.g., `hot_memories`, `cold_knowledge`). Vec tables: `{table}_vec`. FTS tables: `{table}_fts`.
`MigrationRunner.TableName(tier, type)` is the canonical helper — use it, don't inline strings.

---

## Embedding Layer

### IEmbedder

```csharp
interface IEmbedder
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
    int Dimensions { get; }  // 384 for local all-MiniLM-L6-v2
}
```

**Factory**: `EmbedderFactory.CreateFromConfig(cfg.Embedding)` selects the implementation:
- `provider = "local"` (default) → `OnnxEmbedder` using bundled `models/all-MiniLM-L6-v2/model.onnx`
- `provider = "openai"` → `RemoteEmbedder` with OpenAI-compatible endpoint
- `provider = "bedrock"` → `RemoteEmbedder` with Amazon Bedrock

**Model path**: `ModelManager.cs` handles model discovery. Falls back to HuggingFace download if
`models/all-MiniLM-L6-v2/model.onnx` is missing (e.g., after a git-source install without LFS).

---

## Search Layer

`HybridSearch` combines two sources:
1. **Vector search** (`VectorSearch` / `PgvectorSearch`) — cosine similarity via sqlite-vec / pgvector
2. **FTS search** (`FtsSearch` / `PostgresFtsSearch`) — BM25-ranked full-text via FTS5 / tsvector

Results are merged and re-ranked by `TotalRecall.Core.Ranking` (F# pure function).
`similarity_threshold` from config filters low-score vector results before merge.

`KbSearchHandler` can additionally query a `IRemoteBackend` (Cortex) for global team KB.

---

## Importers

### IImporter

```csharp
interface IImporter
{
    string SourceTool { get; }  // e.g., "claude-code"
    Task ImportAsync(CancellationToken ct);
}
```

Each importer:
1. Detects the host tool's memory directory (platform-specific paths)
2. Reads the host's memory format (markdown, JSON, etc.)
3. Deduplicates via `ImportLog` (content hash lookup in `import_log` table)
4. Calls `IStore.StoreAsync()` + `IVectorSearch.UpsertAsync()` for new entries

Importers run during `session_start` step 2. Order in `ServerComposition` matches the registration order.

**Adding a new importer**: implement `IImporter`, add to the `importers` list in both `OpenSqlite`
and `OpenPostgres` / `OpenCortexCore` in `ServerComposition.cs`.

Current importers: `ClaudeCode`, `CopilotCli`, `Cursor`, `Cline`, `OpenCode`, `Hermes`, `ProjectDocs`.

---

## Ingestion

`FileIngester` → `HierarchicalIndex` → `TotalRecall.Core.Chunker` (F# pure) → `IStore` + `IVectorSearch`.

Flow:
1. `IngestValidator` checks for duplicate collections (by source path hash)
2. `Chunker.chunk()` splits file content into semantic chunks (Markdown heading-aware or regex code parser)
3. Each chunk is embedded and stored as a `cold_knowledge` entry with `collection_id` linking siblings
4. `HierarchicalIndex` tracks parent/child relationships via `parent_id`

**Supported parsers** (in `TotalRecall.Core/Parsers.fs`): Markdown, code (regex-based).

---

## Telemetry

| Class | Table | Purpose |
|-------|-------|---------|
| `CompactionLog` | `compaction_log` | Records tier movements (promote/demote/compact) with semantic drift and preservation ratio |
| `UsageEventLog` | `usage_events` | Raw token usage events (one row per assistant turn) |
| `UsageDailyRollup` | `usage_daily` | Aggregated daily token counts; 30-day raw retention |
| `UsageWatermarkStore` | `usage_watermarks` | Per-host scan watermark for incremental indexing |
| `RetrievalEventLog` | `retrieval_events` | Per-search retrieval events with score, tier, and outcome signal |

**Retrieval event logging**: `memory_search` and `kb_search` handlers call `LogRetrievalEvent()`
after each search. Pass `ctx.ConfigSnapshotId` (set by `session_start`) so events are tagged to
the active config snapshot.

---

## Cortex Sync

`RoutingStore` wraps `SqliteStore`:
- Reads go to local SQLite (fast, offline-safe)
- Writes enqueue items to `sync_queue` AND write locally
- `SyncService` drains `sync_queue` → `CortexClient` HTTP calls
- `PeriodicSync` calls `SyncService` every N seconds (default 300s) on a background thread

**Offline resilience**: if Cortex is unreachable, local reads/writes continue. The queue
is durable (SQLite) and survives process restarts.

---

## Config

`ConfigLoader.LoadEffectiveConfig()` merges:
1. Embedded defaults (`Config/defaults.toml` compiled as an embedded resource)
2. User config at `~/.total-recall/config.toml` (TOML via Tomlyn)
3. Environment variable overrides (e.g., `TOTAL_RECALL_DB_PATH`, `TOTAL_RECALL_CORTEX_URL`)

`ConfigLoader.GetDbPath()` → `~/.total-recall/total-recall.db` (or `TOTAL_RECALL_DB_PATH`).
`ConfigLoader.GetDataDir()` → `~/.total-recall/`.

---

## Diagnostics

`ExceptionLogger.LogChain(prefix, ex)` walks the entire `InnerException` chain and writes to
`Console.Error` with indented `-> <Type>: <Message>` formatting. AOT-safe — no reflection.

**Use at every catch boundary** that can hit a static-constructor failure or P/Invoke failure:
- Migration guard
- Server composition (host startup)
- CLI commands that touch the embedder or open the DB

A bare `Console.Error.WriteLine(ex.Message)` at these boundaries hides `DllNotFoundException`
under `TypeInitializationException` and produces unactionable output.

---

## AOT Checklist (for new Infrastructure code)

- [ ] No `Type.GetType()`, `Activator.CreateInstance()`, or `Assembly.Load()`
- [ ] All JSON serialization uses source-generated `JsonContext` attributes
- [ ] No `dynamic` types
- [ ] P/Invoke signatures (sqlite-vec, ONNX) are declared as `[DllImport]` or via `NativeLibrary.Load`
- [ ] `IsAotCompatible=true` in the csproj — verify with `dotnet publish -p:PublishAot=true -r win-x64` producing 0 trim warnings
