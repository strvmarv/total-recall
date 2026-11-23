using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>
/// One archived session as the read tools show it. <paramref name="Ref"/> is <c>&lt;host&gt;:&lt;host_session_id&gt;</c>;
/// <paramref name="ChunkCount"/> counts the session's stored chunks.
/// </summary>
public sealed record SessionRow(
    long Id,
    string Ref,
    string Project,
    string Kind,
    string? Title,
    string? StartedAt,
    string? EndedAt,
    int ExchangeCount,
    long ChunkCount);

/// <summary>
/// The filters of a session list. <paramref name="Project"/> is an exact key; <paramref name="Since"/> (at or after) and
/// <paramref name="Before"/> (strictly before) compare the last activity (<c>ended_at</c>, else <c>started_at</c>), so a
/// session with neither time matches neither bound. Without <paramref name="IncludeAutomated"/> only interactive sessions
/// are listed.
/// </summary>
public sealed record SessionFilter(string? Project, DateTimeOffset? Since, DateTimeOffset? Before, bool IncludeAutomated);

/// <summary>
/// A page of sessions, newest last activity first. <paramref name="Total"/> counts the matching sessions before the
/// limit; <paramref name="AutomatedHidden"/> is the number of automated sessions the filter left out (0 when automated
/// sessions were included).
/// </summary>
public sealed record SessionList(IReadOnlyList<SessionRow> Rows, long Total, long AutomatedHidden);

/// <summary>
/// The reads of one <c>transcript_get</c> or <c>transcript_sessions</c> call, run inside a single deferred read
/// transaction so a forget or an ingest commit between two queries cannot pair a session with another session's chunks.
/// Exchange numbers come from the stored chunks (<c>exchange_start</c>), never from <c>exchange_count</c>. Nothing here
/// writes. Dispose ends the transaction.
/// </summary>
public interface ITranscriptReads : IDisposable
{
    /// <summary>True when the archive holds at least one chunk (an <c>EXISTS</c>, not a count).</summary>
    bool AnyChunk();

    /// <summary>The session <c>&lt;host&gt;:&lt;hostSessionId&gt;</c>, or null when there is none.</summary>
    SessionRow? SessionByRef(string host, string hostSessionId);

    /// <summary>The session with this row id, or null when there is none.</summary>
    SessionRow? SessionById(long sessionId);

    /// <summary>The chunk with this id and the fields of its session, or null when there is none.</summary>
    ChunkRow? ChunkById(long chunkId);

    /// <summary>Every chunk of the exchange, by ascending part.</summary>
    IReadOnlyList<ChunkRow> ChunksOfExchange(long sessionId, int exchange);

    /// <summary>The distinct stored exchange numbers in <c>[from, to]</c>, ascending.</summary>
    IReadOnlyList<int> ExchangesInRange(long sessionId, int from, int to);

    /// <summary>Up to <paramref name="n"/> stored exchange numbers strictly below <paramref name="exchange"/>, nearest first.</summary>
    IReadOnlyList<int> ExchangesBefore(long sessionId, int exchange, int n);

    /// <summary>Up to <paramref name="n"/> stored exchange numbers strictly above <paramref name="exchange"/>, nearest first.</summary>
    IReadOnlyList<int> ExchangesAfter(long sessionId, int exchange, int n);

    /// <summary>
    /// Up to <paramref name="limit"/> sessions holding at least one chunk that match <paramref name="filter"/>: newest last
    /// activity first, sessions with neither time last, then descending id.
    /// </summary>
    SessionList ListSessions(SessionFilter filter, int limit);
}

/// <summary>The store's implementation: borrows the store's connection for the life of one deferred transaction.</summary>
internal sealed class SqliteTranscriptReads : ITranscriptReads
{
    private const string SessionColumns =
        "s.id, s.host, s.host_session_id, s.project_key, s.kind, s.title, s.started_at, s.ended_at, s.exchange_count, " +
        "(SELECT count(*) FROM tx_chunk c WHERE c.session_id = s.id)";

    private const string LastActivity = "COALESCE(s.ended_at, s.started_at)";

    private readonly SqliteConnection _conn;
    private SqliteTransaction? _tx;

    public SqliteTranscriptReads(SqliteConnection conn)
    {
        _conn = conn;
        _tx = conn.BeginTransaction(deferred: true);
    }

    public bool AnyChunk()
    {
        using var cmd = Command("SELECT EXISTS (SELECT 1 FROM tx_chunk)");
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    public SessionRow? SessionByRef(string host, string hostSessionId)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(hostSessionId);
        using var cmd = Command($"SELECT {SessionColumns} FROM tx_session s WHERE s.host = $h AND s.host_session_id = $s");
        cmd.Parameters.AddWithValue("$h", host);
        cmd.Parameters.AddWithValue("$s", hostSessionId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadSession(r) : null;
    }

    public SessionRow? SessionById(long sessionId)
    {
        using var cmd = Command($"SELECT {SessionColumns} FROM tx_session s WHERE s.id = $id");
        cmd.Parameters.AddWithValue("$id", sessionId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadSession(r) : null;
    }

    public ChunkRow? ChunkById(long chunkId)
    {
        using var cmd = Command(ChunkRows.Select + " WHERE c.id = $id");
        cmd.Parameters.AddWithValue("$id", chunkId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ChunkRows.Read(r) : null;
    }

    public IReadOnlyList<ChunkRow> ChunksOfExchange(long sessionId, int exchange)
    {
        using var cmd = Command(ChunkRows.Select + " WHERE c.session_id = $session AND c.exchange_start = $e ORDER BY c.part");
        cmd.Parameters.AddWithValue("$session", sessionId);
        cmd.Parameters.AddWithValue("$e", exchange);
        var rows = new List<ChunkRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add(ChunkRows.Read(r));
        return rows;
    }

    public IReadOnlyList<int> ExchangesInRange(long sessionId, int from, int to)
    {
        using var cmd = Command(
            "SELECT DISTINCT exchange_start FROM tx_chunk WHERE session_id = $session " +
            "AND exchange_start >= $from AND exchange_start <= $to ORDER BY exchange_start");
        cmd.Parameters.AddWithValue("$session", sessionId);
        cmd.Parameters.AddWithValue("$from", from);
        cmd.Parameters.AddWithValue("$to", to);
        return ReadNumbers(cmd);
    }

    public IReadOnlyList<int> ExchangesBefore(long sessionId, int exchange, int n) =>
        Nearest(sessionId, exchange, n, "exchange_start < $e ORDER BY exchange_start DESC");

    public IReadOnlyList<int> ExchangesAfter(long sessionId, int exchange, int n) =>
        Nearest(sessionId, exchange, n, "exchange_start > $e ORDER BY exchange_start");

    public SessionList ListSessions(SessionFilter filter, int limit)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // One aggregate under the filter without the automated clause gives both the total and the hidden count.
        var countSql = new StringBuilder(
            "SELECT count(*), COALESCE(SUM(CASE WHEN s.kind = 'automated' THEN 1 ELSE 0 END), 0) FROM tx_session s");
        AppendFilter(countSql, filter);
        long matching, automated;
        using (var cmd = Command(countSql.ToString()))
        {
            BindFilter(cmd, filter);
            using var r = cmd.ExecuteReader();
            r.Read();
            matching = r.GetInt64(0);
            automated = r.GetInt64(1);
        }

        var total = filter.IncludeAutomated ? matching : matching - automated;
        var hidden = filter.IncludeAutomated ? 0 : automated;
        if (limit <= 0)
            return new SessionList(Array.Empty<SessionRow>(), total, hidden);

        var pageSql = new StringBuilder($"SELECT {SessionColumns} FROM tx_session s");
        AppendFilter(pageSql, filter);
        if (!filter.IncludeAutomated) pageSql.Append(" AND s.kind <> 'automated'");
        pageSql.Append($" ORDER BY {LastActivity} IS NULL, {LastActivity} DESC, s.id DESC LIMIT $limit");

        var rows = new List<SessionRow>();
        using (var cmd = Command(pageSql.ToString()))
        {
            BindFilter(cmd, filter);
            cmd.Parameters.AddWithValue("$limit", (long)limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(ReadSession(r));
        }
        return new SessionList(rows, total, hidden);
    }

    public void Dispose()
    {
        var tx = _tx;
        _tx = null;
        // A read-only transaction: ending it is a rollback and writes nothing.
        tx?.Dispose();
    }

    // Every filter except the automated one; only sessions with a chunk are listed or counted.
    private static void AppendFilter(StringBuilder sql, SessionFilter f)
    {
        sql.Append(" WHERE EXISTS (SELECT 1 FROM tx_chunk c WHERE c.session_id = s.id)");
        if (f.Project is not null) sql.Append(" AND s.project_key = $project");
        // the archive writes every time through TranscriptTime.Iso, which sorts as text at full precision
        if (f.Since is not null) sql.Append($" AND {LastActivity} >= $since");
        if (f.Before is not null) sql.Append($" AND {LastActivity} < $before");
    }

    private static void BindFilter(SqliteCommand cmd, SessionFilter f)
    {
        if (f.Project is { } project) cmd.Parameters.AddWithValue("$project", project);
        if (f.Since is { } since) cmd.Parameters.AddWithValue("$since", TranscriptTime.Iso(since));
        if (f.Before is { } before) cmd.Parameters.AddWithValue("$before", TranscriptTime.Iso(before));
    }

    private IReadOnlyList<int> Nearest(long sessionId, int exchange, int n, string condition)
    {
        // a negative LIMIT is no limit in SQLite
        if (n <= 0)
            return Array.Empty<int>();
        using var cmd = Command($"SELECT DISTINCT exchange_start FROM tx_chunk WHERE session_id = $session AND {condition} LIMIT $n");
        cmd.Parameters.AddWithValue("$session", sessionId);
        cmd.Parameters.AddWithValue("$e", exchange);
        cmd.Parameters.AddWithValue("$n", (long)n);
        return ReadNumbers(cmd);
    }

    private static IReadOnlyList<int> ReadNumbers(SqliteCommand cmd)
    {
        var numbers = new List<int>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            numbers.Add(r.GetInt32(0));
        return numbers;
    }

    private static SessionRow ReadSession(SqliteDataReader r) =>
        new(
            r.GetInt64(0),
            r.GetString(1) + ":" + r.GetString(2),
            r.GetString(3),
            r.GetString(4),
            r.IsDBNull(5) ? null : r.GetString(5),
            r.IsDBNull(6) ? null : r.GetString(6),
            r.IsDBNull(7) ? null : r.GetString(7),
            r.GetInt32(8),
            r.GetInt64(9));

    private SqliteCommand Command(string sql)
    {
        var tx = _tx ?? throw new ObjectDisposedException(nameof(ITranscriptReads));
        var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }
}
