using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using TotalRecall.Infrastructure.Eval;

namespace TotalRecall.Server.Tests.TestSupport;

/// <summary>Helpers for asserting which config snapshot id events were logged under.</summary>
internal static class ConfigSnapshotTestSupport
{
    /// <summary>A holder over the connection's real snapshot table; returns the id it will resolve to.</summary>
    public static (SessionConfigSnapshot holder, string id) NewHolder(SqliteConnection conn, string configJson = "{\"a\":1}")
    {
        var store = new ConfigSnapshotStore(conn);
        var holder = new SessionConfigSnapshot(store, configJson);
        return (holder, holder.Resolve()!);
    }

    /// <summary>The config_snapshot_id of every compaction_log row, oldest first.</summary>
    public static IReadOnlyList<string> CompactionSnapshotIds(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT config_snapshot_id FROM compaction_log ORDER BY timestamp, rowid";
        var ids = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }
}
