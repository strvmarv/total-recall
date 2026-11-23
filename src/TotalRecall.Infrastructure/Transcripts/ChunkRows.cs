using Microsoft.Data.Sqlite;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>The one chunk SELECT (with its session's fields) and the one row mapping, shared by the search and read queries.</summary>
internal static class ChunkRows
{
    /// <summary>The select list and FROM clause; callers append their own WHERE.</summary>
    public const string Select =
        "SELECT c.id, c.session_id, s.host, s.host_session_id, s.project_key, s.kind, s.title, c.ts, c.chunk_type, " +
        "c.has_error, c.exchange_start, c.exchange_end, c.content " +
        "FROM tx_chunk c JOIN tx_session s ON s.id = c.session_id";

    /// <summary>Maps the current row of a query that starts with <see cref="Select"/>.</summary>
    public static ChunkRow Read(SqliteDataReader r) =>
        new(
            r.GetInt64(0),
            r.GetInt64(1),
            r.GetString(2) + ":" + r.GetString(3),
            r.GetString(4),
            r.GetString(5),
            r.IsDBNull(6) ? null : r.GetString(6),
            r.IsDBNull(7) ? null : r.GetString(7),
            r.GetString(8),
            r.GetInt64(9) != 0,
            r.GetInt32(10),
            r.GetInt32(11),
            r.GetString(12));
}
