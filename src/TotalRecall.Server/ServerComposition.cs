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
//   2. OpenProduction() — detects config and opens either a Postgres data
//      source or a Sqlite connection, runs schema migrations, constructs
//      OnnxEmbedder + all Infrastructure singletons, and returns a
//      ServerCompositionHandles that the Host can dispose on shutdown.
//      The Host is responsible for running
//      AutoMigrationGuard.CheckAndMigrateAsync BEFORE calling this (so the
//      migration path can rename the old DB before we open a handle on it).
//
// Handler budget: 12 memory + 7 KB + 3 session + 5 eval + 2 config + 4 misc
// (status, import_host, compact_now, migrate_to_remote) = 33.
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
using Microsoft.FSharp.Core;
using Npgsql;
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
/// the underlying resource (Sqlite connection or Npgsql data source) releases
/// cleanly.
/// </summary>
public sealed class ServerCompositionHandles : IDisposable
{
    private readonly IDisposable _resource;
    public ToolRegistry Registry { get; }

    internal ServerCompositionHandles(IDisposable resource, ToolRegistry registry)
    {
        _resource = resource;
        Registry = registry;
    }

    public void Dispose()
    {
        try { _resource.Dispose(); } catch { /* best-effort */ }
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
        IStore store,
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

        // ---- Misc (4) ----
        registry.Register(new StatusHandler(store, sessionLifecycle, statusOptions));
        registry.Register(new ImportHostHandler()); // self-bootstraps the 7 importers
        registry.Register(new CompactNowHandler());
        registry.Register(new MigrateToRemoteHandler()); // self-bootstraps source+target stores

        return registry;
    }

    /// <summary>
    /// Detect config and open the appropriate backend (Postgres or SQLite).
    /// When a Postgres connection string is configured, opens a
    /// <see cref="NpgsqlDataSource"/>; otherwise opens a SQLite connection at
    /// the standard user path.
    /// </summary>
    /// <remarks>
    /// The caller is responsible for running
    /// <see cref="AutoMigrationGuard.CheckAndMigrateAsync"/> BEFORE calling
    /// this method, so the legacy TS-format DB (if any) is renamed out of
    /// the way before we open a handle.
    /// </remarks>
    public static ServerCompositionHandles OpenProduction(string? dbPath = null)
    {
        var cfg = new ConfigLoader().LoadEffectiveConfig();
        var hasPostgres = FSharpOption<Core.Config.StorageConfig>.get_IsSome(cfg.Storage)
            && FSharpOption<string>.get_IsSome(cfg.Storage.Value.ConnectionString);

        if (hasPostgres)
            return OpenPostgres(cfg);
        else
            return OpenSqlite(cfg, dbPath);
    }

    private static ServerCompositionHandles OpenSqlite(Core.Config.TotalRecallConfig cfg, string? dbPath)
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
            var embedder = EmbedderFactory.CreateFromConfig(cfg.Embedding);
            var hybrid = new HybridSearch(vec, fts, store);

            var compactionLog = new CompactionLog(conn);
            var importLog = new ImportLog(conn);

            var index = new HierarchicalIndex(store, embedder, vec);
            var validator = new IngestValidator(embedder, vec, store);
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

            // Usage tracking wiring — Phase 1 registers Claude Code only.
            // Phase 2 adds Copilot CLI. See token-usage-tracking spec §5.
            var usageEventLog = new TotalRecall.Infrastructure.Telemetry.UsageEventLog(conn);
            var usageWatermarks = new TotalRecall.Infrastructure.Telemetry.UsageWatermarkStore(conn);
            var usageImporters = new List<TotalRecall.Infrastructure.Usage.IUsageImporter>
            {
                new TotalRecall.Infrastructure.Usage.ClaudeCodeUsageImporter(),
            };
            var usageIndexer = new TotalRecall.Infrastructure.Usage.UsageIndexer(
                usageImporters, usageEventLog, usageWatermarks);

            var sessionLifecycle = new SessionLifecycle(
                importers, store, compactionLog,
                usageIndexer: usageIndexer);

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

    private static ServerCompositionHandles OpenPostgres(Core.Config.TotalRecallConfig cfg)
    {
        var connStr = cfg.Storage.Value.ConnectionString.Value;
        var userId = ResolveUserId(cfg);
        var dims = cfg.Embedding.Dimensions;

        var dsBuilder = new NpgsqlDataSourceBuilder(connStr);
        dsBuilder.UseVector();
        var dataSource = dsBuilder.Build();
        try
        {
            PostgresMigrationRunner.RunMigrations(dataSource, dims);

            var store = new PostgresStore(dataSource, userId);
            var vec = new PgvectorSearch(dataSource, userId);
            var fts = new PostgresFtsSearch(dataSource, userId);
            var embedder = EmbedderFactory.CreateFromConfig(cfg.Embedding);
            var hybrid = new HybridSearch(vec, fts, store);

            var compactionLog = new PostgresCompactionLog(dataSource);
            var importLog = new PostgresImportLog(dataSource);

            var index = new HierarchicalIndex(store, embedder, vec);
            var validator = new IngestValidator(embedder, vec, store);
            var fileIngester = new FileIngester(index, validator);

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

            var statusOptions = new StatusOptions(
                DbPath: connStr,
                EmbeddingModel: cfg.Embedding.Model,
                EmbeddingDimensions: dims);

            var registry = BuildRegistry(
                store, vec, embedder, hybrid,
                fileIngester, compactionLog, sessionLifecycle, statusOptions);

            return new ServerCompositionHandles(dataSource, registry);
        }
        catch
        {
            try { dataSource.Dispose(); } catch { }
            throw;
        }
    }

    private static string ResolveUserId(Core.Config.TotalRecallConfig cfg)
    {
        if (FSharpOption<Core.Config.UserConfig>.get_IsSome(cfg.User)
            && FSharpOption<string>.get_IsSome(cfg.User.Value.UserId))
            return cfg.User.Value.UserId.Value;
        var envUserId = Environment.GetEnvironmentVariable("TOTAL_RECALL_USER_ID");
        return envUserId ?? "local";
    }
}
