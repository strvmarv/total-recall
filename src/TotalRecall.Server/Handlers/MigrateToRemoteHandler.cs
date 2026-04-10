// src/TotalRecall.Server/Handlers/MigrateToRemoteHandler.cs
//
// Task 15 — MCP tool for one-time migration from local SQLite to a configured
// remote Postgres instance, re-embedding every entry with the target embedder
// (dimensions / model may differ between local ONNX and remote embedder).
//
// Tool: migrate_to_remote
// Parameters:
//   dry_run           (bool,   default false) — count only, no writes
//   include_knowledge (bool,   default true)  — when false, skip Knowledge rows
//   visibility        (string, default "private") — metadata visibility tag
//
// Flow:
//   1. Validate arguments.
//   2. Iterate all 6 (Tier × ContentType) pairs.
//   3. Optionally skip Knowledge types.
//   4. For each entry in the source store:
//      a. Check if target already has the same id → skip (idempotent).
//      b. dry_run → just count.
//      c. Otherwise: embed with targetEmbedder, then InsertWithEmbedding
//         into target using InsertEntryOpts built from source entry, preserving
//         the original id via the new InsertEntryOpts.Id field.
//   5. Return {"migrated":N,"skipped":N,"errors":N,"dry_run":bool}.
//
// Self-bootstrapping (production): the no-arg constructor reads config via
// ConfigLoader, opens SQLite as source and Postgres as target, runs the
// migration, and disposes both. Requires [storage.connection_string] in config.
//
// Testable via the internal constructor that accepts pre-built fakes.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Core;
using Npgsql;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Server.Handlers;

public sealed class MigrateToRemoteHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "dry_run":           {"type":"boolean","description":"Report counts without writing (default false)"},
            "include_knowledge": {"type":"boolean","description":"Include Knowledge-type entries (default true)"},
            "visibility":        {"type":"string","enum":["private","team","public"],"description":"Visibility for migrated entries (default 'private')"}
          }
        }
        """).RootElement.Clone();

    // (Tier, ContentType) cartesian product — drives iteration order.
    private static readonly (Tier Tier, ContentType Type)[] _allPairs =
    {
        (Tier.Hot,  ContentType.Memory),
        (Tier.Hot,  ContentType.Knowledge),
        (Tier.Warm, ContentType.Memory),
        (Tier.Warm, ContentType.Knowledge),
        (Tier.Cold, ContentType.Memory),
        (Tier.Cold, ContentType.Knowledge),
    };

    private static readonly HashSet<string> _validVisibilities = new(StringComparer.Ordinal)
    {
        "private", "team", "public",
    };

    // Injected dependencies (test path) or null (production self-bootstrap path).
    private readonly IStore? _source;
    private readonly IStore? _target;
    private readonly IEmbedder? _targetEmbedder;
    private readonly IVectorSearch? _targetVectors;

    /// <summary>
    /// Production no-arg constructor. Opens SQLite as source and Postgres as
    /// target inside <see cref="ExecuteAsync"/> using the effective config.
    /// </summary>
    public MigrateToRemoteHandler() { }

    /// <summary>
    /// Test / composition seam. All four dependencies are supplied by the
    /// caller; no I/O takes place inside the constructor.
    /// </summary>
    public MigrateToRemoteHandler(
        IStore source,
        IStore target,
        IEmbedder targetEmbedder,
        IVectorSearch targetVectors)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _targetEmbedder = targetEmbedder ?? throw new ArgumentNullException(nameof(targetEmbedder));
        _targetVectors = targetVectors ?? throw new ArgumentNullException(nameof(targetVectors));
    }

    public string Name => "migrate_to_remote";
    public string Description => "Migrate local SQLite entries to a configured remote Postgres instance with re-embedding";
    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        // --- parse arguments ---
        bool dryRun = false;
        bool includeKnowledge = true;
        string visibility = "private";

        if (arguments.HasValue && arguments.Value.ValueKind == JsonValueKind.Object)
        {
            var args = arguments.Value;
            if (args.TryGetProperty("dry_run", out var drEl))
            {
                if (drEl.ValueKind != JsonValueKind.True && drEl.ValueKind != JsonValueKind.False)
                    throw new ArgumentException("dry_run must be a boolean");
                dryRun = drEl.GetBoolean();
            }
            if (args.TryGetProperty("include_knowledge", out var ikEl))
            {
                if (ikEl.ValueKind != JsonValueKind.True && ikEl.ValueKind != JsonValueKind.False)
                    throw new ArgumentException("include_knowledge must be a boolean");
                includeKnowledge = ikEl.GetBoolean();
            }
            if (args.TryGetProperty("visibility", out var visEl))
            {
                if (visEl.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("visibility must be a string");
                var v = visEl.GetString();
                if (v is null || !_validVisibilities.Contains(v))
                    throw new ArgumentException($"Invalid visibility: {v}. Must be private, team, or public");
                visibility = v;
            }
        }

        ct.ThrowIfCancellationRequested();

        // --- run with either injected or bootstrapped dependencies ---
        MigrateToRemoteResultDto result;
        if (_source is not null)
        {
            // Test / explicit-dependency path.
            result = RunMigration(_source, _target!, _targetEmbedder!, dryRun, includeKnowledge, visibility, ct);
        }
        else
        {
            result = RunProduction(dryRun, includeKnowledge, visibility, ct);
        }

        var jsonText = JsonSerializer.Serialize(result, JsonContext.Default.MigrateToRemoteResultDto);
        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        });
    }

    // -----------------------------------------------------------------------
    // Production self-bootstrap
    // -----------------------------------------------------------------------

    private static MigrateToRemoteResultDto RunProduction(
        bool dryRun,
        bool includeKnowledge,
        string visibility,
        CancellationToken ct)
    {
        var loader = new ConfigLoader();
        var cfg = loader.LoadEffectiveConfig();

        var hasPostgres = FSharpOption<Core.Config.StorageConfig>.get_IsSome(cfg.Storage)
            && FSharpOption<string>.get_IsSome(cfg.Storage.Value.ConnectionString);

        if (!hasPostgres)
            throw new InvalidOperationException(
                "migrate_to_remote requires [storage.connection_string] to be configured in total-recall config.");

        var connStr = cfg.Storage.Value.ConnectionString.Value;

        // Source: local SQLite
        var dbPath = ConfigLoader.GetDbPath();
        Directory.CreateDirectory(ConfigLoader.GetDataDir());
        var sqliteConn = SqliteConnection.Open(dbPath);
        try
        {
            MigrationRunner.RunMigrations(sqliteConn);
            var sourceStore = new SqliteStore(sqliteConn);

            // Target: Postgres
            var dims = cfg.Embedding.Dimensions;
            var dataSource = NpgsqlDataSource.Create(connStr);
            try
            {
                PostgresMigrationRunner.RunMigrations(dataSource, dims);

                // Resolve owner id the same way ServerComposition.ResolveUserId does.
                string ownerId;
                if (FSharpOption<Core.Config.UserConfig>.get_IsSome(cfg.User)
                    && FSharpOption<string>.get_IsSome(cfg.User.Value.UserId))
                    ownerId = cfg.User.Value.UserId.Value;
                else
                    ownerId = Environment.GetEnvironmentVariable("TOTAL_RECALL_USER_ID") ?? "local";

                var targetStore = new PostgresStore(dataSource, ownerId);
                var targetEmbedder = EmbedderFactory.CreateFromConfig(cfg.Embedding);

                return RunMigration(sourceStore, targetStore, targetEmbedder, dryRun, includeKnowledge, visibility, ct);
            }
            finally
            {
                try { dataSource.Dispose(); } catch { /* best-effort */ }
            }
        }
        finally
        {
            try { sqliteConn.Dispose(); } catch { /* best-effort */ }
        }
    }

    // -----------------------------------------------------------------------
    // Core migration loop (shared by test and production paths)
    // -----------------------------------------------------------------------

    private static MigrateToRemoteResultDto RunMigration(
        IStore source,
        IStore target,
        IEmbedder targetEmbedder,
        bool dryRun,
        bool includeKnowledge,
        string visibility,
        CancellationToken ct)
    {
        int migrated = 0;
        int skipped = 0;
        int errors = 0;

        foreach (var (tier, type) in _allPairs)
        {
            if (!includeKnowledge && type == ContentType.Knowledge)
                continue;

            ct.ThrowIfCancellationRequested();

            IReadOnlyList<Entry> entries;
            try
            {
                entries = source.List(tier, type);
            }
            catch
            {
                errors++;
                continue;
            }

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    // Idempotency check — skip if already present in target.
                    var existing = target.Get(tier, type, entry.Id);
                    if (existing is not null)
                    {
                        skipped++;
                        continue;
                    }

                    if (dryRun)
                    {
                        migrated++;
                        continue;
                    }

                    // Build visibility metadata. Preserve original metadata if
                    // present; overlay the requested visibility. Keep it simple:
                    // emit only visibility (non-private) to avoid overwriting
                    // richer metadata the source entry may carry in MetadataJson.
                    string? metadataJson = entry.MetadataJson is "{}" or "" or null
                        ? (visibility != "private" ? $"{{\"visibility\":\"{visibility}\"}}" : null)
                        : entry.MetadataJson;

                    var opts = new InsertEntryOpts(
                        Content: entry.Content,
                        Summary: FSharpOption<string>.get_IsSome(entry.Summary) ? entry.Summary.Value : null,
                        Source: FSharpOption<string>.get_IsSome(entry.Source) ? entry.Source.Value : null,
                        SourceTool: FSharpOption<SourceTool>.get_IsSome(entry.SourceTool) ? entry.SourceTool.Value : null,
                        Project: FSharpOption<string>.get_IsSome(entry.Project) ? entry.Project.Value : null,
                        Tags: entry.Tags.IsEmpty ? null : Microsoft.FSharp.Collections.ListModule.ToArray(entry.Tags),
                        ParentId: FSharpOption<string>.get_IsSome(entry.ParentId) ? entry.ParentId.Value : null,
                        CollectionId: FSharpOption<string>.get_IsSome(entry.CollectionId) ? entry.CollectionId.Value : null,
                        MetadataJson: metadataJson,
                        Id: entry.Id);

                    var embedding = targetEmbedder.Embed(entry.Content);
                    target.InsertWithEmbedding(tier, type, opts, embedding);
                    migrated++;
                }
                catch
                {
                    errors++;
                }
            }
        }

        return new MigrateToRemoteResultDto(
            Migrated: migrated,
            Skipped: skipped,
            Errors: errors,
            DryRun: dryRun);
    }
}
