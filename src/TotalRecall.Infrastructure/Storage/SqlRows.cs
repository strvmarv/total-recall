using System;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Storage;

/// <summary>
/// The row-level SQL shared by <see cref="SqliteStore"/> and
/// <see cref="SqliteStoreTransaction"/>, so each statement has one
/// implementation. Every helper takes the connection and an optional
/// transaction; a non-null transaction is set on each command it runs.
/// </summary>
internal static class SqlRows
{
    internal static string BuildInsertSql(string table) => $@"
INSERT INTO {table}
  (id, content, summary, source, source_tool, project, tags,
   created_at, updated_at, last_accessed_at, access_count,
   decay_score, parent_id, collection_id, metadata, scope, entry_type,
   times_injected)
VALUES
  ($id, $content, $summary, $source, $source_tool, $project, $tags,
   $created_at, $updated_at, $last_accessed_at, $access_count,
   $decay_score, $parent_id, $collection_id, $metadata, $scope, $entry_type,
   $times_injected)";

    internal static void BindInsertParameters(
        SqliteCommand cmd,
        string id,
        InsertEntryOpts opts,
        long now)
    {
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$content", opts.Content);
        cmd.Parameters.AddWithValue("$summary", (object?)opts.Summary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$source", (object?)opts.Source ?? DBNull.Value);
        cmd.Parameters.AddWithValue(
            "$source_tool",
            opts.SourceTool is not null
                ? SourceToolMapping.ToDbValue(opts.SourceTool)
                : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$project", (object?)opts.Project ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tags", TagsJson.Encode(opts.Tags));
        cmd.Parameters.AddWithValue("$created_at", opts.CreatedAt ?? now);
        cmd.Parameters.AddWithValue("$updated_at", now);
        cmd.Parameters.AddWithValue("$last_accessed_at", now);
        cmd.Parameters.AddWithValue("$access_count", 0);
        cmd.Parameters.AddWithValue("$decay_score", 1.0);
        cmd.Parameters.AddWithValue("$parent_id", (object?)opts.ParentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$collection_id", (object?)opts.CollectionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$metadata", opts.MetadataJson ?? "{}");
        cmd.Parameters.AddWithValue("$scope", opts.Scope ?? "");
        cmd.Parameters.AddWithValue(
            "$entry_type",
            EntryTypeMapping.ToDbValue(opts.EntryType ?? EntryType.Preference));
        cmd.Parameters.AddWithValue("$times_injected", 0);
    }

    /// <summary>Insert a content row and return its new rowid.</summary>
    public static long InsertRow(
        MsSqliteConnection conn, SqliteTransaction? tx,
        string table, string id, InsertEntryOpts opts, long now)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = BuildInsertSql(table);
            BindInsertParameters(cmd, id, opts, now);
            cmd.ExecuteNonQuery();
        }

        using var rowidCmd = conn.CreateCommand();
        rowidCmd.Transaction = tx;
        rowidCmd.CommandText = "SELECT last_insert_rowid()";
        return (long)rowidCmd.ExecuteScalar()!;
    }

    /// <summary>Apply an <see cref="UpdateEntryOpts"/> to one content row.</summary>
    public static void UpdateRow(
        MsSqliteConnection conn, SqliteTransaction? tx,
        string table, string id, UpdateEntryOpts opts, long now)
    {
        var setClauses = new List<string> { "updated_at = $updated_at" };
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$updated_at", now);

        if (opts.Content is not null)
        {
            setClauses.Add("content = $content");
            cmd.Parameters.AddWithValue("$content", opts.Content);
        }
        if (opts.Summary is not null || opts.ClearSummary)
        {
            setClauses.Add("summary = $summary");
            cmd.Parameters.AddWithValue("$summary", (object?)opts.Summary ?? DBNull.Value);
        }
        if (opts.Tags is not null)
        {
            setClauses.Add("tags = $tags");
            cmd.Parameters.AddWithValue("$tags", TagsJson.Encode(opts.Tags));
        }
        if (opts.Project is not null || opts.ClearProject)
        {
            setClauses.Add("project = $project");
            cmd.Parameters.AddWithValue("$project", (object?)opts.Project ?? DBNull.Value);
        }
        if (opts.DecayScore.HasValue)
        {
            setClauses.Add("decay_score = $decay_score");
            cmd.Parameters.AddWithValue("$decay_score", opts.DecayScore.Value);
        }
        if (opts.MetadataJson is not null)
        {
            setClauses.Add("metadata = $metadata");
            cmd.Parameters.AddWithValue("$metadata", opts.MetadataJson);
        }
        if (opts.Scope is not null)
        {
            setClauses.Add("scope = $scope");
            cmd.Parameters.AddWithValue("$scope", opts.Scope);
        }
        if (opts.EntryType is not null)
        {
            setClauses.Add("entry_type = $entry_type");
            cmd.Parameters.AddWithValue("$entry_type", EntryTypeMapping.ToDbValue(opts.EntryType));
        }
        if (opts.ClearSourceTool)
        {
            setClauses.Add("source_tool = NULL");
        }
        else if (opts.SourceTool is not null)
        {
            setClauses.Add("source_tool = $source_tool");
            cmd.Parameters.AddWithValue("$source_tool", SourceToolMapping.ToDbValue(opts.SourceTool));
        }
        if (opts.CreatedAt.HasValue)
        {
            setClauses.Add("created_at = $created_at");
            cmd.Parameters.AddWithValue("$created_at", opts.CreatedAt.Value);
        }
        if (opts.Touch)
        {
            setClauses.Add("access_count = access_count + 1");
            setClauses.Add("last_accessed_at = $last_accessed_at");
            cmd.Parameters.AddWithValue("$last_accessed_at", now);
        }

        cmd.Parameters.AddWithValue("$id", id);
        cmd.CommandText = $"UPDATE {table} SET {string.Join(", ", setClauses)} WHERE id = $id";
        cmd.ExecuteNonQuery();
    }

    public static void DeleteRow(
        MsSqliteConnection conn, SqliteTransaction? tx, string table, string id)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"DELETE FROM {table} WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // Sticky is hot-only: both operate on the hot_* table regardless of
    // caller intent, since only those tables carry the `sticky` column.
    public static void SetStickyRow(
        MsSqliteConnection conn, SqliteTransaction? tx, ContentType type, string id, bool sticky)
    {
        var table = MigrationRunner.TableName(Tier.Hot, type);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"UPDATE {table} SET sticky = $sticky WHERE id = $id";
        cmd.Parameters.AddWithValue("$sticky", sticky ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public static bool IsStickyRow(
        MsSqliteConnection conn, SqliteTransaction? tx, ContentType type, string id)
    {
        var table = MigrationRunner.TableName(Tier.Hot, type);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT sticky FROM {table} WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        var result = cmd.ExecuteScalar();
        return result is long l && l != 0L;
    }

    /// <summary>
    /// Copy one row from <paramref name="fromTable"/> into
    /// <paramref name="toTable"/> exactly as <c>SqliteStore.Move</c> always
    /// has (<c>updated_at</c> = <paramref name="now"/>, every other column
    /// kept, <c>sticky</c> not carried) and return the target rowid. The
    /// source row is NOT deleted: callers do that after any vector work.
    /// Throws <see cref="InvalidOperationException"/> when the source row does
    /// not exist.
    /// </summary>
    public static long CopyRow(
        MsSqliteConnection conn, SqliteTransaction? tx,
        string fromTable, string toTable, string id, long now)
    {
        var entry = ReadRow(conn, tx, fromTable, id)
            ?? throw new InvalidOperationException($"Entry {id} not found in {fromTable}");

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $@"
INSERT INTO {toTable}
  (id, content, summary, source, source_tool, project, tags,
   created_at, updated_at, last_accessed_at, access_count,
   decay_score, parent_id, collection_id, metadata, scope, entry_type,
   times_injected)
VALUES
  ($id, $content, $summary, $source, $source_tool, $project, $tags,
   $created_at, $updated_at, $last_accessed_at, $access_count,
   $decay_score, $parent_id, $collection_id, $metadata, $scope, $entry_type,
   $times_injected)";

            cmd.Parameters.AddWithValue("$id", entry.Id);
            cmd.Parameters.AddWithValue("$content", entry.Content);
            cmd.Parameters.AddWithValue("$summary", ToDbString(entry.Summary));
            cmd.Parameters.AddWithValue("$source", ToDbString(entry.Source));
            cmd.Parameters.AddWithValue(
                "$source_tool",
                FSharpOption<SourceTool>.get_IsSome(entry.SourceTool)
                    ? SourceToolMapping.ToDbValue(entry.SourceTool.Value)
                    : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$project", ToDbString(entry.Project));
            cmd.Parameters.AddWithValue("$tags", TagsJson.EncodeFList(entry.Tags));
            cmd.Parameters.AddWithValue("$created_at", entry.CreatedAt);
            cmd.Parameters.AddWithValue("$updated_at", now);
            cmd.Parameters.AddWithValue("$last_accessed_at", entry.LastAccessedAt);
            cmd.Parameters.AddWithValue("$access_count", entry.AccessCount);
            cmd.Parameters.AddWithValue("$decay_score", entry.DecayScore);
            cmd.Parameters.AddWithValue("$parent_id", ToDbString(entry.ParentId));
            cmd.Parameters.AddWithValue("$collection_id", ToDbString(entry.CollectionId));
            cmd.Parameters.AddWithValue("$metadata", entry.MetadataJson);
            cmd.Parameters.AddWithValue("$scope", entry.Scope);
            cmd.Parameters.AddWithValue(
                "$entry_type", EntryTypeMapping.ToDbValue(entry.EntryType));
            cmd.Parameters.AddWithValue("$times_injected", entry.TimesInjected);
            cmd.ExecuteNonQuery();
        }

        using var rowidCmd = conn.CreateCommand();
        rowidCmd.Transaction = tx;
        rowidCmd.CommandText = "SELECT last_insert_rowid()";
        return (long)rowidCmd.ExecuteScalar()!;
    }

    private static object ToDbString(FSharpOption<string> opt) =>
        FSharpOption<string>.get_IsSome(opt) ? opt.Value : (object)DBNull.Value;

    /// <summary>The rowid of the content row with this id, or null.</summary>
    public static long? RowId(
        MsSqliteConnection conn, SqliteTransaction? tx, string table, string id)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT rowid FROM {table} WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is long l ? l : null;
    }

    /// <summary>Read one content row as an <see cref="Entry"/>, or null.</summary>
    public static Entry? ReadRow(
        MsSqliteConnection conn, SqliteTransaction? tx, string table, string id)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT * FROM {table} WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? SqliteStore.RowToEntry(reader) : null;
    }
}

/// <summary>Row-level SQL for the <c>*_vec</c> (sqlite-vec) tables.</summary>
internal static class VecRows
{
    /// <summary>
    /// Insert a vector row at <paramref name="rowid"/>, first removing any
    /// orphan vector row at that rowid (left by a content row deleted without
    /// its vector).
    /// </summary>
    public static void InsertWithOrphanCleanup(
        MsSqliteConnection conn, SqliteTransaction? tx,
        string vecTable, long rowid, ReadOnlyMemory<float> embedding)
    {
        Delete(conn, tx, vecTable, rowid);
        InsertBytes(conn, tx, vecTable, rowid, MemoryMarshal.AsBytes(embedding.Span).ToArray());
    }

    /// <summary>Insert raw vector bytes at <paramref name="rowid"/> (no cleanup).</summary>
    public static void InsertBytes(
        MsSqliteConnection conn, SqliteTransaction? tx,
        string vecTable, long rowid, byte[] embedding)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            $"INSERT INTO {vecTable} (rowid, embedding) VALUES ($rowid, $embedding)";
        cmd.Parameters.AddWithValue("$rowid", rowid);
        cmd.Parameters.AddWithValue("$embedding", embedding);
        cmd.ExecuteNonQuery();
    }

    public static void Delete(
        MsSqliteConnection conn, SqliteTransaction? tx, string vecTable, long rowid)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"DELETE FROM {vecTable} WHERE rowid = $rowid";
        cmd.Parameters.AddWithValue("$rowid", rowid);
        cmd.ExecuteNonQuery();
    }

    /// <summary>The stored vector bytes at this rowid, or null.</summary>
    public static byte[]? ReadBytes(
        MsSqliteConnection conn, SqliteTransaction? tx, string vecTable, long rowid)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT embedding FROM {vecTable} WHERE rowid = $rowid";
        cmd.Parameters.AddWithValue("$rowid", rowid);
        return cmd.ExecuteScalar() as byte[];
    }
}
