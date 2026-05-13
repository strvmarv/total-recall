// src/TotalRecall.Infrastructure/Sync/SyncBacklogReader.cs
//
// Read-only inspector for the outbound sync backlog. Surfaces per-type
// counts of sync_queue rows, the count of unsynced skill_usage_events,
// the count of rows that have failed at least once (high-attempt rows
// that may indicate cortex-reachability problems), and the oldest
// unsynced row's timestamp across all sources.
//
// AOT-compatible: no reflection, no expression trees, no dynamic types.
// All JSON-bound DTOs sit in TotalRecall.Server/JsonContext.cs and are
// registered for source-gen.
//
// Powers the status MCP tool's syncBacklog field so operators can tell at
// a glance whether the queue is healthy or whether something's stuck.

using System;
using System.Globalization;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Sync;

public sealed class SyncBacklogReader
{
    private readonly MsSqliteConnection _conn;

    public SyncBacklogReader(MsSqliteConnection conn)
    {
        _conn = conn ?? throw new ArgumentNullException(nameof(conn));
    }

    public SyncBacklogSnapshot Read()
    {
        int memory = CountByType("memory");
        int usage = CountByType("usage");
        int retrieval = CountByType("retrieval");
        int compaction = CountByType("compaction");
        int skillUsage = CountSkillUsageUnsynced();
        int retrying = CountRetrying();
        DateTime? oldest = OldestUnsynced();

        return new SyncBacklogSnapshot(
            memory, usage, retrieval, compaction, skillUsage, retrying, oldest);
    }

    private int CountByType(string entityType)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sync_queue WHERE entity_type = $t";
        cmd.Parameters.AddWithValue("$t", entityType);
        return ScalarToInt(cmd.ExecuteScalar());
    }

    private int CountSkillUsageUnsynced()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM skill_usage_events WHERE synced_at IS NULL";
        return ScalarToInt(cmd.ExecuteScalar());
    }

    // Rows that have failed at least once (attempts > 0). Useful indicator
    // that the remote has been intermittently unreachable or rejecting
    // payloads. SyncQueue has no hard dead-letter gate — items back off
    // exponentially up to 1h but always retry.
    private int CountRetrying()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sync_queue WHERE attempts > 0";
        return ScalarToInt(cmd.ExecuteScalar());
    }

    private DateTime? OldestUnsynced()
    {
        // Take the min of (sync_queue.created_at) and
        // (skill_usage_events.occurred_at for unsynced rows). Either source
        // can be empty; null indicates no backlog at all.
        DateTime? syncQueueOldest = ReadOldestTimestamp(
            "SELECT MIN(created_at) FROM sync_queue");
        DateTime? skillUsageOldest = ReadOldestTimestamp(
            "SELECT MIN(occurred_at) FROM skill_usage_events WHERE synced_at IS NULL");

        if (syncQueueOldest is null) return skillUsageOldest;
        if (skillUsageOldest is null) return syncQueueOldest;
        return syncQueueOldest < skillUsageOldest ? syncQueueOldest : skillUsageOldest;
    }

    private DateTime? ReadOldestTimestamp(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        var r = cmd.ExecuteScalar();
        if (r is string s
            && DateTime.TryParse(
                s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
            return dt.ToUniversalTime();
        return null;
    }

    private static int ScalarToInt(object? value) => value switch
    {
        long l => (int)l,
        int i => i,
        _ => 0,
    };
}

public sealed record SyncBacklogSnapshot(
    int MemoryUnsynced,
    int UsageUnsynced,
    int RetrievalUnsynced,
    int CompactionUnsynced,
    int SkillUsageUnsynced,
    int Retrying,
    DateTime? OldestUnsyncedAt);
