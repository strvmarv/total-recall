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
using TotalRecall.Infrastructure.Skills;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Sync;
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
    private readonly Infrastructure.Sync.PeriodicSync? _periodicSync;
    public ToolRegistry Registry { get; }

    /// <summary>
    /// The <see cref="IStore"/> backing this composition (SqliteStore, PostgresStore,
    /// or RoutingStore in cortex mode). Exposed for test assertions.
    /// </summary>
    public IStore Store { get; }

    /// <summary>
    /// Human-friendly label for the effective storage backend, e.g. "sqlite",
    /// "postgres", "cortex", or "sqlite (cortex failed)" when a fallback occurred.
    /// </summary>
    public string StorageMode { get; }

    internal ServerCompositionHandles(IDisposable resource, ToolRegistry registry, IStore store, string storageMode = "sqlite", Infrastructure.Sync.PeriodicSync? periodicSync = null)
    {
        _resource = resource;
        Registry = registry;
        Store = store;
        StorageMode = storageMode;
        _periodicSync = periodicSync;
    }

    public void Dispose()
    {
        try { _periodicSync?.Dispose(); } catch { /* best-effort */ }
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
        StatusOptions statusOptions,
        Infrastructure.Sync.SyncService? syncService = null,
        Infrastructure.Sync.IRemoteBackend? remoteBackend = null,
        Infrastructure.Sync.PeriodicSync? periodicSync = null,
        string? scopeDefault = null,
        RetrievalEventLog? retrievalLog = null,
        Infrastructure.Sync.SyncQueue? syncQueue = null,
        CompactionLog? compactionLogWriter = null)
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
        registry.Register(new MemoryStoreHandler(store, embedder, vectors, scopeDefault));
        registry.Register(new MemorySearchHandler(embedder, hybrid, scopeDefault, retrievalLog, syncQueue));
        registry.Register(new MemoryGetHandler(store));
        registry.Register(new MemoryUpdateHandler(store, embedder, vectors));
        registry.Register(new MemoryDeleteHandler(store, vectors));
        registry.Register(new MemoryPromoteHandler(store, vectors, embedder, compactionLogWriter, syncQueue));
        registry.Register(new MemoryDemoteHandler(store, vectors, embedder, compactionLogWriter, syncQueue));
        registry.Register(new MemoryInspectHandler(store, compactionLog));
        registry.Register(new MemoryHistoryHandler(compactionLog));
        registry.Register(new MemoryLineageHandler(compactionLog));
        registry.Register(new MemoryExportHandler(store));
        registry.Register(new MemoryImportHandler(store, vectors, embedder));

        // ---- KB (7) ----
        registry.Register(new KbSearchHandler(embedder, hybrid, remoteBackend, scopeDefault, retrievalLog, syncQueue));
        registry.Register(new KbIngestFileHandler(fileIngester, scopeDefault));
        registry.Register(new KbIngestDirHandler(fileIngester, scopeDefault));
        registry.Register(new KbListCollectionsHandler(store));
        registry.Register(new KbRefreshHandler(store, vectors, fileIngester));
        registry.Register(new KbRemoveHandler(store, vectors));
        registry.Register(new KbSummarizeHandler(store));

        // ---- Session (3) ----
        registry.Register(new SessionStartHandler(sessionLifecycle, periodicSync));
        registry.Register(new SessionEndHandler(sessionLifecycle, store, compactionLogWriter, syncService: syncService));
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

        var configuredMode = "local";
        if (FSharpOption<Core.Config.StorageConfig>.get_IsSome(cfg.Storage)
            && FSharpOption<string>.get_IsSome(cfg.Storage.Value.Mode))
        {
            configuredMode = cfg.Storage.Value.Mode.Value;
        }
        else if (FSharpOption<Core.Config.StorageConfig>.get_IsSome(cfg.Storage)
            && FSharpOption<string>.get_IsSome(cfg.Storage.Value.ConnectionString))
        {
            configuredMode = "postgres";
        }

        // Map config values to friendly display names.
        static string FriendlyName(string mode) => mode switch
        {
            "local" => "sqlite",
            _ => mode
        };

        try
        {
            return configuredMode switch
            {
                "cortex" => OpenCortex(cfg, dbPath, FriendlyName(configuredMode)),
                "postgres" => OpenPostgres(cfg, FriendlyName(configuredMode)),
                _ => OpenSqlite(cfg, dbPath, FriendlyName(configuredMode))
            };
        }
        catch (Exception ex) when (configuredMode is "cortex" or "postgres")
        {
            // Non-local backend failed — fall back to SQLite so the session
            // is still usable, and surface what happened in the storage label.
            var friendly = FriendlyName(configuredMode);
            Console.Error.WriteLine(
                $"[total-recall] {friendly} storage failed, falling back to sqlite: {ex.Message}");
            return OpenSqlite(cfg, dbPath, $"sqlite ({friendly} failed)");
        }
    }

    private static ServerCompositionHandles OpenSqlite(Core.Config.TotalRecallConfig cfg, string? dbPath, string storageMode = "sqlite")
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
            EmbedderFingerprint.EnsureMatches(store, embedder);
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
                new TotalRecall.Infrastructure.Usage.CopilotCliUsageImporter(),
            };
            var usageRollup = new TotalRecall.Infrastructure.Telemetry.UsageDailyRollup(conn);
            var usageIndexer = new TotalRecall.Infrastructure.Usage.UsageIndexer(
                usageImporters, usageEventLog, usageWatermarks, rollup: usageRollup);

            // Skill scanner — local extra_dirs only (no cortex client in SQLite mode).
            ISkillImportService? sqliteSkillService = null;
            {
                var extraDirs = Array.Empty<string>();
                if (FSharpOption<Core.Config.SkillConfig>.get_IsSome(cfg.Skill)
                    && FSharpOption<string[]>.get_IsSome(cfg.Skill.Value.ExtraDirs))
                    extraDirs = cfg.Skill.Value.ExtraDirs.Value;
                if (extraDirs.Length > 0)
                    sqliteSkillService = new SkillImportService(
                        new ClaudeCodeSkillScanner(),
                        NullSkillClient.Instance,
                        new CustomDirsSkillScanner(extraDirs));
            }

            var sessionLifecycle = new SessionLifecycle(
                importers, store, compactionLog,
                usageIndexer: usageIndexer,
                storageMode: storageMode,
                skillImportService: sqliteSkillService,
                tokenBudget: cfg.Tiers.Hot.TokenBudget,
                maxEntries: cfg.Tiers.Hot.MaxEntries);

            var statusOptions = new StatusOptions(
                DbPath: resolvedDbPath,
                EmbeddingModel: cfg.Embedding.Model,
                EmbeddingDimensions: cfg.Embedding.Dimensions);

            // Phase 5: retrieval telemetry — log locally in sqlite-only mode.
            // syncQueue stays null because there is no cortex to push to.
            var retrievalLog = new RetrievalEventLog(conn);

            var registry = BuildRegistry(
                store, vec, embedder, hybrid,
                fileIngester, compactionLog, sessionLifecycle, statusOptions,
                scopeDefault: ResolveScopeDefault(cfg),
                retrievalLog: retrievalLog,
                compactionLogWriter: compactionLog);

            // Task 13 — `usage_status` MCP tool. SQLite-only for now: the
            // Postgres composition path has no usage indexer wired (Phase 2
            // out-of-scope), so it correspondingly exposes no read tool.
            var usageQuery = new TotalRecall.Infrastructure.Usage.UsageQueryService(conn);
            registry.Register(new UsageStatusHandler(usageQuery));

            return new ServerCompositionHandles(conn, registry, store, storageMode);
        }
        catch
        {
            try { conn.Dispose(); } catch { /* best-effort */ }
            throw;
        }
    }

    private static ServerCompositionHandles OpenPostgres(Core.Config.TotalRecallConfig cfg, string storageMode = "postgres")
    {
        if (!FSharpOption<Core.Config.StorageConfig>.get_IsSome(cfg.Storage)
            || !FSharpOption<string>.get_IsSome(cfg.Storage.Value.ConnectionString))
        {
            throw new InvalidOperationException(
                "Storage mode is \"postgres\" but no connection string is configured. " +
                "Set 'connection_string' in [storage], or set TOTAL_RECALL_PG_CONNECTION_STRING.");
        }

        var connStr = cfg.Storage.Value.ConnectionString.Value;
        var userId = ResolveUserId(cfg);
        var dims = cfg.Embedding.Dimensions;

        var dsBuilder = new NpgsqlDataSourceBuilder(connStr);
        dsBuilder.UseVector();
        var dataSource = dsBuilder.Build();
        try
        {
            PostgresMigrationRunner.RunMigrations(dataSource, dims);

            var store = new PostgresStore(dataSource, userId, cfg.Tiers.Hot.MaxEntries);
            var vec = new PgvectorSearch(dataSource, userId);
            var fts = new PostgresFtsSearch(dataSource, userId);
            var embedder = EmbedderFactory.CreateFromConfig(cfg.Embedding);
            EmbedderFingerprint.EnsureMatches(store, embedder);
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

            var sessionLifecycle = new SessionLifecycle(importers, store, compactionLog,
                storageMode: storageMode,
                tokenBudget: cfg.Tiers.Hot.TokenBudget,
                maxEntries: cfg.Tiers.Hot.MaxEntries);

            var statusOptions = new StatusOptions(
                DbPath: connStr,
                EmbeddingModel: cfg.Embedding.Model,
                EmbeddingDimensions: dims);

            var registry = BuildRegistry(
                store, vec, embedder, hybrid,
                fileIngester, compactionLog, sessionLifecycle, statusOptions,
                scopeDefault: ResolveScopeDefault(cfg));

            return new ServerCompositionHandles(dataSource, registry, store, storageMode);
        }
        catch
        {
            try { dataSource.Dispose(); } catch { }
            throw;
        }
    }

    private static ServerCompositionHandles OpenCortex(Core.Config.TotalRecallConfig cfg, string? dbPath, string storageMode = "cortex")
    {
        // Cortex mode uses SQLite locally, plus a RoutingStore that enqueues
        // writes for eventual push to the remote Cortex backend.
        var resolvedDbPath = dbPath ?? ConfigLoader.GetDbPath();
        Directory.CreateDirectory(ConfigLoader.GetDataDir());
        var dbParent = Path.GetDirectoryName(resolvedDbPath);
        if (!string.IsNullOrEmpty(dbParent))
        {
            Directory.CreateDirectory(dbParent);
        }

        if (!FSharpOption<Core.Config.CortexConfig>.get_IsSome(cfg.Cortex))
        {
            throw new InvalidOperationException(
                "Storage mode is \"cortex\" but the [cortex] config section is missing or incomplete. " +
                "Provide both 'url' and 'pat' in [cortex], or set TOTAL_RECALL_CORTEX_URL and TOTAL_RECALL_CORTEX_PAT.");
        }

        var cortexUrl = cfg.Cortex.Value.Url;
        var cortexPat = cfg.Cortex.Value.Pat;

        return OpenCortexCore(cfg, resolvedDbPath, cortexUrl, cortexPat, storageMode);
    }

    /// <summary>
    /// Test-friendly entry point for cortex mode that takes explicit parameters
    /// instead of requiring config files.
    /// </summary>
    public static ServerCompositionHandles OpenCortexForTest(
        string sqliteDbPath, string cortexUrl, string cortexPat)
    {
        var cfg = new ConfigLoader().LoadEffectiveConfig();
        return OpenCortexCore(cfg, sqliteDbPath, cortexUrl, cortexPat, "cortex");
    }

    private static ServerCompositionHandles OpenCortexCore(
        Core.Config.TotalRecallConfig cfg, string resolvedDbPath,
        string cortexUrl, string cortexPat, string storageMode = "cortex")
    {
        var conn = SqliteConnection.Open(resolvedDbPath);
        try
        {
            MigrationRunner.RunMigrations(conn);

            var localStore = new SqliteStore(conn);
            var vec = new VectorSearch(conn);
            var fts = new FtsSearch(conn);
            var embedder = EmbedderFactory.CreateFromConfig(cfg.Embedding);
            var hybrid = new HybridSearch(vec, fts, localStore);

            var cortexClient = CortexClient.Create(cortexUrl, cortexPat);
            var syncQueue = new SyncQueue(conn);
            var routingStore = new RoutingStore(localStore, cortexClient, syncQueue);
            var skillCache = new SqliteSkillCache(conn);
            var syncService = new Infrastructure.Sync.SyncService(
                localStore, cortexClient, syncQueue, conn, skillCache);

            var syncIntervalSeconds = FSharpOption<Core.Config.CortexConfig>.get_IsSome(cfg.Cortex)
                && FSharpOption<int>.get_IsSome(cfg.Cortex.Value.SyncIntervalSeconds)
                ? cfg.Cortex.Value.SyncIntervalSeconds.Value
                : 300;

            Infrastructure.Sync.PeriodicSync? periodicSync = syncIntervalSeconds > 0
                ? new Infrastructure.Sync.PeriodicSync(syncService, syncIntervalSeconds)
                : null;

            var compactionLog = new CompactionLog(conn);
            var importLog = new ImportLog(conn);

            var index = new HierarchicalIndex(routingStore, embedder, vec);
            var validator = new IngestValidator(embedder, vec, routingStore);
            var fileIngester = new FileIngester(index, validator);

            var importers = new List<IImporter>
            {
                new ClaudeCodeImporter(routingStore, embedder, vec, importLog),
                new CopilotCliImporter(routingStore, embedder, vec, importLog),
                new CursorImporter(routingStore, embedder, vec, importLog),
                new ClineImporter(routingStore, embedder, vec, importLog),
                new OpenCodeImporter(routingStore, embedder, vec, importLog),
                new HermesImporter(routingStore, embedder, vec, importLog),
                new ProjectDocsImporter(fileIngester, index, importLog),
            };

            var usageEventLog = new TotalRecall.Infrastructure.Telemetry.UsageEventLog(conn);
            var usageWatermarks = new TotalRecall.Infrastructure.Telemetry.UsageWatermarkStore(conn);
            var usageImporters = new List<TotalRecall.Infrastructure.Usage.IUsageImporter>
            {
                new TotalRecall.Infrastructure.Usage.ClaudeCodeUsageImporter(),
                new TotalRecall.Infrastructure.Usage.CopilotCliUsageImporter(),
            };
            var usageRollup = new TotalRecall.Infrastructure.Telemetry.UsageDailyRollup(conn);
            var usageIndexer = new TotalRecall.Infrastructure.Usage.UsageIndexer(
                usageImporters, usageEventLog, usageWatermarks,
                rollup: usageRollup,
                syncQueue: syncQueue);

            // Plan 2: skill infrastructure — cortex-mode only. Scanner walks
            // local ~/.claude/skills and {project}/.claude/skills; client POSTs
            // bundles to the cortex /api/me/skills/import endpoint; the import
            // service orchestrates both and is injected into SessionLifecycle
            // so session_start folds skill counts into importSummary.
            var skillClient = CortexSkillClient.Create(cortexUrl, cortexPat);
            var skillScanner = new ClaudeCodeSkillScanner();
            var extraSkillDirs = Array.Empty<string>();
            if (FSharpOption<Core.Config.SkillConfig>.get_IsSome(cfg.Skill))
            {
                var skillCfg = cfg.Skill.Value;
                if (FSharpOption<string[]>.get_IsSome(skillCfg.ExtraDirs))
                {
                    extraSkillDirs = skillCfg.ExtraDirs.Value;
                }
            }
            ICustomDirsSkillScanner? customDirsScanner = extraSkillDirs.Length > 0
                ? new CustomDirsSkillScanner(extraSkillDirs)
                : null;
            var skillImportService = new SkillImportService(skillScanner, skillClient, customDirsScanner);

            var sessionLifecycle = new SessionLifecycle(
                importers, routingStore, compactionLog,
                usageIndexer: usageIndexer,
                storageMode: storageMode,
                skillImportService: skillImportService,
                tokenBudget: cfg.Tiers.Hot.TokenBudget,
                maxEntries: cfg.Tiers.Hot.MaxEntries);

            var statusOptions = new StatusOptions(
                DbPath: resolvedDbPath,
                EmbeddingModel: cfg.Embedding.Model,
                EmbeddingDimensions: cfg.Embedding.Dimensions);

            // Phase 5: retrieval telemetry — log locally AND enqueue for
            // push in cortex mode so SyncService.FlushAsync picks it up.
            var retrievalLog = new RetrievalEventLog(conn);

            var registry = BuildRegistry(
                routingStore, vec, embedder, hybrid,
                fileIngester, compactionLog, sessionLifecycle, statusOptions,
                syncService: syncService, remoteBackend: cortexClient,
                periodicSync: periodicSync,
                scopeDefault: ResolveScopeDefault(cfg),
                retrievalLog: retrievalLog,
                syncQueue: syncQueue,
                compactionLogWriter: compactionLog);

            var usageQuery = new TotalRecall.Infrastructure.Usage.UsageQueryService(conn);
            registry.Register(new UsageStatusHandler(usageQuery));

            // Plan 2: skill_* MCP handlers — cortex-mode only (skills live in cortex).
            registry.Register(new SkillSearchHandler(skillClient));
            registry.Register(new SkillGetHandler(skillClient));
            registry.Register(new SkillListHandler(skillClient));
            registry.Register(new SkillDeleteHandler(skillClient));
            registry.Register(new SkillImportHostHandler(
                skillImportService, () => Environment.CurrentDirectory));

            return new ServerCompositionHandles(conn, registry, routingStore, storageMode, periodicSync);
        }
        catch
        {
            try { conn.Dispose(); } catch { /* best-effort */ }
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

    /// <summary>
    /// Resolves the configured scope default from <c>[scope] default</c> in
    /// the TOML config. Returns <c>null</c> when the section or key is absent.
    /// </summary>
    private static string? ResolveScopeDefault(Core.Config.TotalRecallConfig cfg)
    {
        if (!FSharpOption<Core.Config.ScopeConfig>.get_IsSome(cfg.Scope))
            return null;
        var scopeCfg = cfg.Scope.Value;
        if (!FSharpOption<string>.get_IsSome(scopeCfg.Default))
            return null;
        return scopeCfg.Default.Value;
    }
}
