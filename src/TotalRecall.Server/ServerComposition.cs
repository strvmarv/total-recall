// src/TotalRecall.Server/ServerComposition.cs
//
// Plan 6 Task 6.3a — production composition root for the MCP server.
//
// Responsibilities split between two entry points:
//
//   1. BuildRegistry(...) — pure function that takes pre-built infrastructure
//      singletons plus a SessionLifecycle and StatusOptions, constructs all
//      32 handlers, and registers them in a ToolRegistry. Pure means no I/O
//      and no disposal — all of that is the caller's job. This seam exists
//      for testability: a unit test can inject fakes and assert the handler
//      set is complete (count + expected names) without opening a real DB.
//
//   2. OpenProduction() — opens the real user DB at the standard path, runs
//      schema migrations, constructs OnnxEmbedder + all Infrastructure
//      singletons, and returns a ServerCompositionHandles that the Host can
//      dispose on shutdown. The Host is responsible for running
//      AutoMigrationGuard.CheckAndMigrateAsync BEFORE calling this (so the
//      migration path can rename the old DB before we open a handle on it).
//
// Handler budget: 12 memory + 7 KB + 3 session + 5 eval + 2 config + 3 misc
// (status, import_host, compact_now) = 32 per the plan's "33 handlers" naming
// (Plan 4's 12 + Plan 6's 20 + Status = 32 registered). The plan-text count
// of 33 is off-by-one; this file registers what actually exists under
// src/TotalRecall.Server/Handlers/.
//
// AOT: no reflection. Every handler is constructed via direct `new`. The
// Eval/Config/ImportHost/CompactNow handlers have no-arg constructors that
// self-bootstrap their own short-lived Sqlite connection per invocation;
// this file registers them via `new` and lets them own that lifecycle.
// Memory/KB/Session/Status handlers share the process-lifetime connection
// via the borrowed-connection pattern (Infrastructure classes take a
// MsSqliteConnection and don't dispose it).

using System;
using System.Collections.Generic;
using System.IO;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Importers;
using TotalRecall.Infrastructure.Ingestion;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using TotalRecall.Server.Handlers;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Server;

/// <summary>
/// Bundle of disposable resources owned by the production composition root.
/// The Host disposes this on shutdown (Ctrl+C / SIGTERM / shutdown RPC) so
/// the Sqlite connection and the ONNX session release cleanly.
/// </summary>
public sealed class ServerCompositionHandles : IDisposable
{
    public MsSqliteConnection Connection { get; }
    public ToolRegistry Registry { get; }

    internal ServerCompositionHandles(MsSqliteConnection connection, ToolRegistry registry)
    {
        Connection = connection;
        Registry = registry;
    }

    public void Dispose()
    {
        // SqliteStore + VectorSearch + CompactionLog etc. all borrow the
        // connection and do not dispose it, so disposing here is both safe
        // and necessary. OnnxEmbedder owns its own ORT session which is
        // released by the GC on process exit; there's no explicit dispose
        // in the current EmbedderFactory surface.
        try { Connection.Dispose(); } catch { /* best-effort */ }
    }
}

/// <summary>
/// Production composition root for the MCP server.
/// </summary>
public static class ServerComposition
{
    /// <summary>
    /// Build a ToolRegistry populated with all production handlers using
    /// the supplied Infrastructure singletons. Pure: no I/O, no disposal.
    /// Callable from both production (OpenProduction) and tests.
    /// </summary>
    public static ToolRegistry BuildRegistry(
        ISqliteStore store,
        IVectorSearch vectors,
        IEmbedder embedder,
        IHybridSearch hybrid,
        IFileIngester fileIngester,
        ICompactionLogReader compactionLog,
        ISessionLifecycle sessionLifecycle,
        StatusOptions statusOptions)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(vectors);
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(hybrid);
        ArgumentNullException.ThrowIfNull(fileIngester);
        ArgumentNullException.ThrowIfNull(compactionLog);
        ArgumentNullException.ThrowIfNull(sessionLifecycle);
        ArgumentNullException.ThrowIfNull(statusOptions);

        var registry = new ToolRegistry();

        // ---- Memory (12) ----
        registry.Register(new MemoryStoreHandler(store, embedder, vectors));
        registry.Register(new MemorySearchHandler(embedder, hybrid));
        registry.Register(new MemoryGetHandler(store));
        registry.Register(new MemoryUpdateHandler(store, embedder, vectors));
        registry.Register(new MemoryDeleteHandler(store, vectors));
        registry.Register(new MemoryPromoteHandler(store, vectors, embedder));
        registry.Register(new MemoryDemoteHandler(store, vectors, embedder));
        registry.Register(new MemoryInspectHandler(store, compactionLog));
        registry.Register(new MemoryHistoryHandler(compactionLog));
        registry.Register(new MemoryLineageHandler(compactionLog));
        registry.Register(new MemoryExportHandler(store));
        registry.Register(new MemoryImportHandler(store, vectors, embedder));

        // ---- KB (7) ----
        registry.Register(new KbSearchHandler(embedder, hybrid));
        registry.Register(new KbIngestFileHandler(fileIngester));
        registry.Register(new KbIngestDirHandler(fileIngester));
        registry.Register(new KbListCollectionsHandler(store));
        registry.Register(new KbRefreshHandler(store, vectors, fileIngester));
        registry.Register(new KbRemoveHandler(store, vectors));
        registry.Register(new KbSummarizeHandler(store));

        // ---- Session (3) ----
        registry.Register(new SessionStartHandler(sessionLifecycle));
        registry.Register(new SessionEndHandler(sessionLifecycle));
        registry.Register(new SessionContextHandler(store));

        // ---- Eval (5) — self-bootstrap production executors ----
        registry.Register(new EvalReportHandler());
        registry.Register(new EvalBenchmarkHandler());
        registry.Register(new EvalCompareHandler());
        registry.Register(new EvalSnapshotHandler());
        registry.Register(new EvalGrowHandler());

        // ---- Config (2) — self-bootstrap via ConfigLoader ----
        registry.Register(new ConfigGetHandler());
        registry.Register(new ConfigSetHandler());

        // ---- Misc (3) ----
        registry.Register(new StatusHandler(store, sessionLifecycle, statusOptions));
        registry.Register(new ImportHostHandler()); // self-bootstraps the 7 importers
        registry.Register(new CompactNowHandler());

        return registry;
    }

    /// <summary>
    /// Open the production Sqlite connection at the standard user path, run
    /// schema migrations, construct Infrastructure singletons + all handlers,
    /// and return the disposable handle bundle.
    /// </summary>
    /// <remarks>
    /// The caller is responsible for running
    /// <see cref="AutoMigrationGuard.CheckAndMigrateAsync"/> BEFORE calling
    /// this method, so the legacy TS-format DB (if any) is renamed out of
    /// the way before we open a handle.
    /// </remarks>
    public static ServerCompositionHandles OpenProduction(string? dbPath = null)
    {
        var resolvedDbPath = dbPath ?? ConfigLoader.GetDbPath();
        Directory.CreateDirectory(ConfigLoader.GetDataDir());
        var dbParent = Path.GetDirectoryName(resolvedDbPath);
        if (!string.IsNullOrEmpty(dbParent))
        {
            Directory.CreateDirectory(dbParent);
        }

        var conn = SqliteConnection.Open(resolvedDbPath);
        try
        {
            MigrationRunner.RunMigrations(conn);

            var store = new SqliteStore(conn);
            var vec = new VectorSearch(conn);
            var fts = new FtsSearch(conn);
            var embedder = EmbedderFactory.CreateProduction();
            var hybrid = new HybridSearch(vec, fts, store);

            var compactionLog = new CompactionLog(conn);
            var importLog = new ImportLog(conn);

            var index = new HierarchicalIndex(store, embedder, vec, conn);
            var validator = new IngestValidator(embedder, vec, conn);
            var fileIngester = new FileIngester(index, validator);

            // Host importer set — mirrors ImportHostCommand.Execute.
            var importers = new List<IImporter>
            {
                new ClaudeCodeImporter(store, embedder, vec, importLog),
                new CopilotCliImporter(store, embedder, vec, importLog),
                new CursorImporter(store, embedder, vec, importLog),
                new ClineImporter(store, embedder, vec, importLog),
                new OpenCodeImporter(store, embedder, vec, importLog),
                new HermesImporter(store, embedder, vec, importLog),
                new ProjectDocsImporter(fileIngester, index, importLog),
            };

            var sessionLifecycle = new SessionLifecycle(importers, store, compactionLog);

            var cfg = new ConfigLoader().LoadEffectiveConfig();
            var statusOptions = new StatusOptions(
                DbPath: resolvedDbPath,
                EmbeddingModel: cfg.Embedding.Model,
                EmbeddingDimensions: cfg.Embedding.Dimensions);

            var registry = BuildRegistry(
                store, vec, embedder, hybrid,
                fileIngester, compactionLog, sessionLifecycle, statusOptions);

            return new ServerCompositionHandles(conn, registry);
        }
        catch
        {
            try { conn.Dispose(); } catch { /* best-effort */ }
            throw;
        }
    }
}
