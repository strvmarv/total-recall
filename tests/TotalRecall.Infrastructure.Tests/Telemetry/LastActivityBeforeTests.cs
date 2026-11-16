using System;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using TotalRecall.Infrastructure.Usage;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.Telemetry;

/// <summary>
/// The three "last activity before a cutoff" readers behind <c>lastSessionAge</c>
/// (issue #132). Rows are seeded by raw INSERT because the writers stamp the real
/// clock. Not <c>Integration</c>-trait, so the standard verify runs them.
/// </summary>
public sealed class LastActivityBeforeTests
{
    private const long Cutoff = 1_000_000L;

    private static MsSqliteConnection Migrated()
    {
        var conn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        return conn;
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

    private static void AddRetrieval(MsSqliteConnection conn, long ts, string source = "assistant") =>
        Exec(conn,
            "INSERT INTO retrieval_events (id, timestamp, session_id, query_text, query_source, config_snapshot_id) " +
            $"VALUES ('r{ts}', {ts}, 's', 'q', '{source}', 'cfg')");

    private static void AddUsage(MsSqliteConnection conn, long ts) =>
        Exec(conn,
            "INSERT INTO usage_events (host, host_event_id, session_id, ts) " +
            $"VALUES ('claude-code', 'e{ts}', 's', {ts})");

    // ---------- compaction_log ----------

    [Fact]
    public void Compaction_CountsOnlyRowsStrictlyOlderThanTheCutoff()
    {
        using var conn = Migrated();
        AddCompaction(conn, Cutoff - 1, "session_end_decay");
        AddCompaction(conn, Cutoff, "session_end_decay");
        AddCompaction(conn, Cutoff + 1, "session_end_decay");

        Assert.Equal(Cutoff - 1, new CompactionLog(conn).GetLastActivityTimestampBefore(Cutoff));
    }

    [Fact]
    public void Compaction_ExcludesWarmSweepDecayAndMigrationReasons()
    {
        using var conn = Migrated();
        AddCompaction(conn, 100, "manual_compact");
        AddCompaction(conn, 200, "warm_sweep_decay");
        AddCompaction(conn, 300, "migration_hot_to_warm");
        AddCompaction(conn, 400, "migration_anything_else");

        Assert.Equal(100L, new CompactionLog(conn).GetLastActivityTimestampBefore(Cutoff));
    }

    [Fact]
    public void Compaction_UnderscoreInMigrationPrefixIsNotAWildcard()
    {
        using var conn = Migrated();
        AddCompaction(conn, 100, "manual_compact");
        AddCompaction(conn, 200, "migrationX_not_a_migration");

        Assert.Equal(200L, new CompactionLog(conn).GetLastActivityTimestampBefore(Cutoff));
    }

    [Fact]
    public void Compaction_OnlyExcludedRows_ReturnsNull()
    {
        using var conn = Migrated();
        AddCompaction(conn, 100, "warm_sweep_decay");
        AddCompaction(conn, 200, "migration_hot_to_warm");

        Assert.Null(new CompactionLog(conn).GetLastActivityTimestampBefore(Cutoff));
    }

    // ---------- retrieval_events ----------

    [Fact]
    public void Retrieval_CountsOnlyRowsStrictlyOlderThanTheCutoff_AnyQuerySource()
    {
        using var conn = Migrated();
        AddRetrieval(conn, 500, "memory_search");
        AddRetrieval(conn, Cutoff - 1, "mcp_tool");
        AddRetrieval(conn, Cutoff, "assistant");
        AddRetrieval(conn, Cutoff + 1, "assistant");

        Assert.Equal(Cutoff - 1, new RetrievalEventLog(conn).GetLastEventTimestampBefore(Cutoff));
    }

    [Fact]
    public void Retrieval_Empty_ReturnsNull()
    {
        using var conn = Migrated();
        Assert.Null(new RetrievalEventLog(conn).GetLastEventTimestampBefore(Cutoff));
    }

    // ---------- usage_events ----------

    [Fact]
    public void Usage_CountsOnlyRowsStrictlyOlderThanTheCutoff()
    {
        using var conn = Migrated();
        AddUsage(conn, 700);
        AddUsage(conn, Cutoff - 1);
        AddUsage(conn, Cutoff);
        AddUsage(conn, Cutoff + 1);

        Assert.Equal(Cutoff - 1, new UsageQueryService(conn).GetLastEventTimestampBefore(Cutoff));
    }

    [Fact]
    public void Usage_Empty_ReturnsNull()
    {
        using var conn = Migrated();
        Assert.Null(new UsageQueryService(conn).GetLastEventTimestampBefore(Cutoff));
    }
}
