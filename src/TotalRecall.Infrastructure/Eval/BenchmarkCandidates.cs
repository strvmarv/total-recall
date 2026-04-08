// src/TotalRecall.Infrastructure/Eval/BenchmarkCandidates.cs
//
// Plan 5 Task 5.3a — partial port of src-ts/eval/benchmark-candidates.ts.
// Only the read side and the upsert-from-misses helper are landed in 5.3a;
// the resolve+write-to-retrieval.jsonl path used by `eval grow` is deferred
// to Task 5.3b.
//
// The benchmark_candidates table is created by Migration 2 in
// src/TotalRecall.Infrastructure/Storage/Schema.cs. Schema (snake_case):
//   id TEXT PK
//   query_text TEXT UNIQUE
//   top_score REAL
//   top_result_content TEXT
//   top_result_entry_id TEXT
//   first_seen INTEGER, last_seen INTEGER, times_seen INTEGER
//   status TEXT  -- 'pending' | 'accepted' | 'rejected'
//
// This file deliberately keeps a thin "borrows a connection" shape mirroring
// CompactionLog / RetrievalEventLog so the future Task 5.3b grow command
// can sit alongside it without rewriting the seam.

using System;
using System.Collections.Generic;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Eval;

/// <summary>One row of <c>benchmark_candidates</c>.</summary>
public sealed record CandidateRow(
    string Id,
    string QueryText,
    double TopScore,
    string? TopResultContent,
    string? TopResultEntryId,
    long FirstSeen,
    long LastSeen,
    int TimesSeen,
    string Status);

/// <summary>
/// Context attached to a miss when upserting it as a benchmark candidate.
/// Mirrors the TS <c>MissContext</c> shape — the optional content + entry id
/// surface what the retrieval surfaced for the miss query.
/// </summary>
public sealed record MissContext(string Query, string? TopContent, string? TopEntryId);

/// <summary>
/// Read/upsert helpers over <c>benchmark_candidates</c>. The 5.3a slice
/// only exposes <see cref="ListPending"/> and <see cref="UpsertFromMisses"/>;
/// the resolve / append-to-retrieval.jsonl path lands in Task 5.3b.
/// </summary>
public sealed class BenchmarkCandidates
{
    private readonly MsSqliteConnection _conn;

    public BenchmarkCandidates(MsSqliteConnection conn)
    {
        _conn = conn ?? throw new ArgumentNullException(nameof(conn));
    }

    /// <summary>
    /// List candidates whose status is <c>pending</c>, ordered by
    /// <c>times_seen DESC, top_score ASC</c>. Mirrors <c>listCandidates</c>
    /// in TS.
    /// </summary>
    public IReadOnlyList<CandidateRow> ListPending()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
SELECT id, query_text, top_score, top_result_content, top_result_entry_id,
       first_seen, last_seen, times_seen, status
FROM benchmark_candidates
WHERE status = 'pending'
ORDER BY times_seen DESC, top_score ASC";

        var rows = new List<CandidateRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new CandidateRow(
                Id: reader.GetString(0),
                QueryText: reader.GetString(1),
                TopScore: reader.IsDBNull(2) ? 0.0 : reader.GetDouble(2),
                TopResultContent: reader.IsDBNull(3) ? null : reader.GetString(3),
                TopResultEntryId: reader.IsDBNull(4) ? null : reader.GetString(4),
                FirstSeen: reader.GetInt64(5),
                LastSeen: reader.GetInt64(6),
                TimesSeen: reader.GetInt32(7),
                Status: reader.GetString(8)));
        }
        return rows;
    }

    /// <summary>
    /// Upsert one row per miss. New rows start at <c>times_seen=1</c> and
    /// status <c>pending</c>; existing rows bump <c>times_seen</c> and
    /// refresh <c>top_score</c> + <c>last_seen</c>. Mirrors TS
    /// <c>writeCandidates</c>.
    /// </summary>
    public void UpsertFromMisses(
        IReadOnlyList<MissEntry> misses,
        IReadOnlyList<MissContext> contexts)
    {
        ArgumentNullException.ThrowIfNull(misses);
        ArgumentNullException.ThrowIfNull(contexts);
        if (misses.Count == 0) return;

        var contextMap = new Dictionary<string, MissContext>(StringComparer.Ordinal);
        foreach (var c in contexts) contextMap[c.Query] = c;

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO benchmark_candidates
  (id, query_text, top_score, top_result_content, top_result_entry_id,
   first_seen, last_seen, times_seen, status)
VALUES
  ($id, $q, $score, $content, $eid, $first, $last, 1, 'pending')
ON CONFLICT(query_text) DO UPDATE SET
  top_score = excluded.top_score,
  last_seen = excluded.last_seen,
  times_seen = benchmark_candidates.times_seen + 1";

        var pId = cmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Text);
        var pQ = cmd.Parameters.Add("$q", Microsoft.Data.Sqlite.SqliteType.Text);
        var pScore = cmd.Parameters.Add("$score", Microsoft.Data.Sqlite.SqliteType.Real);
        var pContent = cmd.Parameters.Add("$content", Microsoft.Data.Sqlite.SqliteType.Text);
        var pEid = cmd.Parameters.Add("$eid", Microsoft.Data.Sqlite.SqliteType.Text);
        var pFirst = cmd.Parameters.Add("$first", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pLast = cmd.Parameters.Add("$last", Microsoft.Data.Sqlite.SqliteType.Integer);

        foreach (var m in misses)
        {
            contextMap.TryGetValue(m.Query, out var ctx);
            pId.Value = Guid.NewGuid().ToString();
            pQ.Value = m.Query;
            pScore.Value = m.TopScore ?? 0.0;
            pContent.Value = (object?)ctx?.TopContent ?? DBNull.Value;
            pEid.Value = (object?)ctx?.TopEntryId ?? DBNull.Value;
            pFirst.Value = m.Timestamp;
            pLast.Value = m.Timestamp;
            cmd.ExecuteNonQuery();
        }
    }
}
