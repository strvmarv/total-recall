using Microsoft.Data.Sqlite;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>A migrated temporary database with a sync machine id, a store to seed memories with, and a scanner on the same connection.</summary>
internal sealed class ScanRig : IDisposable
{
    public const string MachineId = "4f2a9c1e";

    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "tr-scan-" + Guid.NewGuid().ToString("N"));
    public string DbPath => Path.Combine(Dir, "total-recall.db");
    public MsSqliteConnection Connection { get; }
    public SqliteStore Store { get; }

    public ScanRig(bool joined = true)
    {
        Connection = SyncDb.OpenMigrated(DbPath);
        Store = new SqliteStore(Connection);
        if (joined)
            SetMeta("git_sync_machine", MachineId);
    }

    public GitSyncScanner Scanner(long now) => new(Connection, () => now);

    /// <summary>A second connection to the same file (WAL), for concurrency tests.</summary>
    public (MsSqliteConnection Connection, SqliteStore Store) Other()
    {
        var conn = SyncDb.OpenMigrated(DbPath);
        return (conn, new SqliteStore(conn));
    }

    public string AddMemory(
        Tier tier,
        string id,
        string content = "Use bun, not npm.",
        IReadOnlyList<string>? tags = null,
        string? project = null,
        string? scope = null,
        EntryType? entryType = null,
        SourceTool? sourceTool = null,
        long? createdAt = 1_700_000_000_000L) =>
        Store.Insert(tier, ContentType.Memory, new InsertEntryOpts(
            Content: content, SourceTool: sourceTool, Project: project, Tags: tags, Id: id, Scope: scope,
            EntryType: entryType, CreatedAt: createdAt));

    public string AddKnowledge(Tier tier, string id, string content = "A knowledge chunk.") =>
        Store.Insert(tier, ContentType.Knowledge, new InsertEntryOpts(Content: content, Id: id));

    public void Pin(string id) => Store.SetSticky(ContentType.Memory, id, true);

    public Dictionary<string, GitSyncRow> Rows() => GitSyncRows.ReadAll(Connection);

    public List<(string Id, string Kind, string? Detail)> Issues()
    {
        var list = new List<(string, string, string?)>();
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = "SELECT entry_id, kind, detail FROM git_sync_issue ORDER BY entry_id, kind";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
        return list;
    }

    public string? Meta(string key)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM _meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetMeta(string key, string value) => SetMeta(Connection, key, value);

    public static void SetMeta(MsSqliteConnection conn, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO _meta (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public void Exec(string sql) => Exec(Connection, sql);

    public static void Exec(MsSqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Every row of the six memory and knowledge tables, the three vector tables and import_log, as text, to compare before and after.</summary>
    public string Dump()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var table in new[]
                 {
                     "hot_memories", "warm_memories", "cold_memories", "hot_knowledge", "warm_knowledge", "cold_knowledge",
                     "import_log", "compaction_log",
                 })
        {
            sb.Append("== ").Append(table).Append('\n');
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {table} ORDER BY 1";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                for (var i = 0; i < r.FieldCount; i++)
                    sb.Append(r.IsDBNull(i) ? "NULL" : r.GetValue(i)).Append('|');
                sb.Append('\n');
            }
        }
        foreach (var table in new[] { "hot_memories_vec", "warm_memories_vec", "cold_memories_vec" })
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = $"SELECT count(*) FROM {table}";
            sb.Append(table).Append('=').Append(cmd.ExecuteScalar()).Append('\n');
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        Connection.Dispose();
        MsSqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
