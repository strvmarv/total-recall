// Issue #132: lastSessionAge is the age of the newest activity of an EARLIER
// session, from usage_events, retrieval_events and compaction_log, and the
// running session never counts. Real SQLite readers over an in-memory
// database; rows are seeded by raw INSERT because the writers stamp the real
// clock. Not Integration-trait, so the standard verify runs them.

namespace TotalRecall.Server.Tests;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TotalRecall.Infrastructure.Importers;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using TotalRecall.Infrastructure.Usage;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

public sealed class SessionLifecycleLastSessionAgeTests
{
    private const long Hour = 3_600_000L;
    private const long Day = 24 * Hour;

    // An importer whose import step runs a callback, to simulate rows that
    // session_start itself writes after the lifecycle was constructed.
    private sealed class WritingImporter : IImporter
    {
        private readonly Action _onImport;
        public WritingImporter(Action onImport) => _onImport = onImport;
        public string Name => "writing-importer";
        public bool Detect() => true;
        public ImporterScanResult Scan() => new(MemoryFiles: 0, KnowledgeFiles: 0, SessionFiles: 0);
        public ImportResult ImportMemories(string? project = null)
        {
            _onImport();
            return ImportResult.Empty;
        }
        public ImportResult ImportKnowledge() => ImportResult.Empty;
    }

    private static void Exec(MsSqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void AddCompaction(MsSqliteConnection conn, long ts, string reason) =>
        Exec(conn,
            "INSERT INTO compaction_log (id, timestamp, source_tier, reason, config_snapshot_id) " +
            $"VALUES ('c{ts}-{reason}', {ts}, 'hot', '{reason}', 'cfg')");

    private static void AddRetrieval(MsSqliteConnection conn, long ts) =>
        Exec(conn,
            "INSERT INTO retrieval_events (id, timestamp, session_id, query_text, query_source, config_snapshot_id) " +
            $"VALUES ('r{ts}', {ts}, 's', 'q', 'assistant', 'cfg')");

    private static void AddUsage(MsSqliteConnection conn, long ts) =>
        Exec(conn,
            "INSERT INTO usage_events (host, host_event_id, session_id, ts) " +
            $"VALUES ('claude-code', 'e{ts}', 's', {ts})");

    private static MsSqliteConnection Migrated()
    {
        var conn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        return conn;
    }

    private static SessionLifecycle Build(
        MsSqliteConnection conn, Func<long>? nowMs = null, IEnumerable<IImporter>? importers = null)
    {
        var retrieval = new RetrievalEventLog(conn);
        return new SessionLifecycle(
            importers: new List<IImporter>(importers ?? Array.Empty<IImporter>()),
            store: new SqliteStore(conn),
            compactionLog: new CompactionLog(conn),
            sessionId: "s-" + Guid.NewGuid().ToString("N"),
            nowMs: nowMs,
            usageQuery: new UsageQueryService(conn),
            lastRetrievalBefore: retrieval.GetLastEventTimestampBefore);
    }

    // The report: no usage rows, an old one-off migration row, and a search from
    // 26 hours ago. The age comes from the search, not from the migration.
    [Fact]
    public async Task EmptyUsage_OldMigrationRow_RecentSearch_ReportsTheSearchAge()
    {
        const long now = 1_800_000_000_000L;
        using var conn = Migrated();
        AddCompaction(conn, now - 112 * Day, "migration_hot_to_warm");
        AddRetrieval(conn, now - 26 * Hour);

        var result = await Build(conn, () => now).EnsureInitializedAsync();

        Assert.Equal("1 day ago", result.LastSessionAge);
    }

    [Fact]
    public async Task TheNewestOfTheThreeSourcesWins()
    {
        const long now = 1_800_000_000_000L;
        using var conn = Migrated();
        AddCompaction(conn, now - 30 * Day, "session_end_decay");
        AddRetrieval(conn, now - 10 * Day);
        AddUsage(conn, now - 3 * Hour);

        var result = await Build(conn, () => now).EnsureInitializedAsync();

        Assert.Equal("3 hours ago", result.LastSessionAge);
    }

    [Fact]
    public async Task OnlyMigrationAndLegacySweepRows_ReportNoAge()
    {
        const long now = 1_800_000_000_000L;
        using var conn = Migrated();
        AddCompaction(conn, now - 112 * Day, "migration_hot_to_warm");
        AddCompaction(conn, now - 20 * Day, "warm_sweep_decay");

        var result = await Build(conn, () => now).EnsureInitializedAsync();

        Assert.Null(result.LastSessionAge);
    }

    // Everything at or after the moment this process came up belongs to the
    // running session and is ignored.
    [Fact]
    public async Task ActivityNewerThanTheLifecycleStart_IsIgnored()
    {
        const long now = 1_800_000_000_000L;
        using var conn = Migrated();
        AddRetrieval(conn, now - 5 * Day);
        AddRetrieval(conn, now);
        AddRetrieval(conn, now + 1);
        AddUsage(conn, now + Hour);
        AddCompaction(conn, now + 2 * Hour, "session_end_decay");

        var result = await Build(conn, () => now).EnsureInitializedAsync();

        Assert.Equal("5 days ago", result.LastSessionAge);
    }

    [Fact]
    public async Task OnlyRunningSessionActivity_ReportsNoAge()
    {
        const long now = 1_800_000_000_000L;
        using var conn = Migrated();
        AddRetrieval(conn, now + 1);
        AddUsage(conn, now + 2);

        var result = await Build(conn, () => now).EnsureInitializedAsync();

        Assert.Null(result.LastSessionAge);
    }

    // Rows written by session_start itself, with the real clock, must never
    // turn the age into "just now": the cutoff was taken at construction.
    [Fact]
    public async Task RowsWrittenDuringSessionStart_NeverMakeItJustNow()
    {
        using var conn = Migrated();
        var realNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        AddRetrieval(conn, realNow - 2 * Day - Hour);

        var compaction = new CompactionLog(conn);
        var retrieval = new RetrievalEventLog(conn);
        var importer = new WritingImporter(() =>
        {
            compaction.LogEvent(new CompactionLogEntry(
                SessionId: "s", SourceTier: "hot", TargetTier: "warm",
                SourceEntryIds: Array.Empty<string>(), TargetEntryId: null,
                DecayScores: new Dictionary<string, double>(),
                Reason: "session_end_decay", ConfigSnapshotId: "cfg"));
            retrieval.LogEvent(new RetrievalEventEntry(
                SessionId: "s", QueryText: "q", QuerySource: "assistant",
                Results: Array.Empty<RetrievalResultItem>(),
                TiersSearched: Array.Empty<string>(), ConfigSnapshotId: "cfg"));
            AddUsage(conn, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        });

        var result = await Build(conn, importers: new[] { importer }).EnsureInitializedAsync();

        Assert.Equal("2 days ago", result.LastSessionAge);
    }
}
