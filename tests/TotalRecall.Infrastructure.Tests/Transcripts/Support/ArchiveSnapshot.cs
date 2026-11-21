using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace TotalRecall.TestSupport.Transcripts;

/// <summary>
/// The archive's rows as ordered text lists, for "a grown session equals the same file ingested once" (spec section 7,
/// Guarantees): every <c>tx_session</c> column except <c>id</c> and <c>source_mtime</c>, and every <c>tx_chunk</c> column
/// except <c>id</c>, with <c>session_id</c> replaced by the session's <c>host</c> and <c>host_session_id</c>. With
/// <c>includeIds</c> the row ids are kept as well (a second run must not change them). Reads the file directly through
/// Microsoft.Data.Sqlite, so it needs no internal API.
/// </summary>
public sealed class ArchiveSnapshot
{
    public IReadOnlyList<string> Sessions { get; }
    public IReadOnlyList<string> Chunks { get; }

    private ArchiveSnapshot(IReadOnlyList<string> sessions, IReadOnlyList<string> chunks)
    {
        Sessions = sessions;
        Chunks = chunks;
    }

    public static ArchiveSnapshot Read(string dbPath, bool includeIds = false)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        using var conn = new SqliteConnection(csb.ToString());
        conn.Open();

        var sessionSkip = includeIds ? new[] { "source_mtime" } : new[] { "id", "source_mtime" };
        var sessions = Rows(conn,
            "SELECT * FROM tx_session ORDER BY host, host_session_id",
            sessionSkip);

        var chunkSkip = includeIds ? Array.Empty<string>() : new[] { "id", "session_id" };
        var chunks = Rows(conn,
            "SELECT s.host AS session_host, s.host_session_id AS session_host_id, c.* FROM tx_chunk c " +
            "JOIN tx_session s ON s.id = c.session_id " +
            "ORDER BY s.host, s.host_session_id, c.exchange_start, c.part",
            chunkSkip);

        return new ArchiveSnapshot(sessions, chunks);
    }

    private static List<string> Rows(SqliteConnection conn, string sql, string[] skip)
    {
        var rows = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var sb = new StringBuilder();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                if (Array.IndexOf(skip, name) >= 0) continue;
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(name).Append('=');
                sb.Append(reader.IsDBNull(i)
                    ? "<null>"
                    : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture));
            }
            rows.Add(sb.ToString());
        }
        return rows;
    }
}
