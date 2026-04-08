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
using System.Globalization;
using System.IO;
using System.Text;
using TotalRecall.Infrastructure.Json;
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
/// Result of <see cref="BenchmarkCandidates.Resolve"/>. Counts mirror the
/// number of accept/reject ids the caller passed in; <see cref="CorpusEntries"/>
/// is the set of JSON lines appended to the benchmark corpus file.
/// </summary>
public sealed record CandidateResolveResult(
    int Accepted,
    int Rejected,
    IReadOnlyList<string> CorpusEntries);

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

    /// <summary>
    /// Flip <c>status</c> for accepted/rejected rows, build benchmark corpus
    /// entries from the accepted rows, and append them to <paramref name="benchmarkFilePath"/>.
    /// Ports <c>resolveCandidates</c> in <c>src-ts/eval/benchmark-candidates.ts</c>.
    /// Missing accept ids are silently skipped (matches TS).
    /// </summary>
    public CandidateResolveResult Resolve(
        IReadOnlyList<string> acceptIds,
        IReadOnlyList<string> rejectIds,
        string benchmarkFilePath)
    {
        ArgumentNullException.ThrowIfNull(acceptIds);
        ArgumentNullException.ThrowIfNull(rejectIds);
        ArgumentNullException.ThrowIfNull(benchmarkFilePath);

        var corpusEntries = new List<string>();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // Plan 5 Task 5.10 — make accept/reject + file append atomic.
        // Row flips run inside a SqliteTransaction. The benchmark corpus
        // file is written to a sibling ".tmp" first and renamed into place
        // only AFTER the DB commit succeeds. On any failure we rollback
        // the transaction AND delete the temp file, so neither the DB
        // nor the corpus file land partially.
        var tempPath = benchmarkFilePath + ".tmp";
        using var tx = _conn.BeginTransaction();
        try
        {
            using var selectCmd = _conn.CreateCommand();
            selectCmd.Transaction = tx;
            selectCmd.CommandText =
                "SELECT query_text, top_result_content FROM benchmark_candidates WHERE id = $id";
            var pSelectId = selectCmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Text);

            using var acceptCmd = _conn.CreateCommand();
            acceptCmd.Transaction = tx;
            acceptCmd.CommandText = "UPDATE benchmark_candidates SET status = 'accepted' WHERE id = $id";
            var pAcceptId = acceptCmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Text);

            using var rejectCmd = _conn.CreateCommand();
            rejectCmd.Transaction = tx;
            rejectCmd.CommandText = "UPDATE benchmark_candidates SET status = 'rejected' WHERE id = $id";
            var pRejectId = rejectCmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Text);

            foreach (var id in acceptIds)
            {
                pSelectId.Value = id;
                string? query = null;
                string? topContent = null;
                using (var reader = selectCmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        query = reader.GetString(0);
                        topContent = reader.IsDBNull(1) ? null : reader.GetString(1);
                    }
                }
                if (query is null) continue;

                pAcceptId.Value = id;
                acceptCmd.ExecuteNonQuery();

                var expected = topContent is null
                    ? string.Empty
                    : (topContent.Length > 100 ? topContent.Substring(0, 100) : topContent);
                corpusEntries.Add(BuildCorpusEntry(query, expected, today));
            }

            foreach (var id in rejectIds)
            {
                pRejectId.Value = id;
                rejectCmd.ExecuteNonQuery();
            }

            // Build the new corpus bytes and stage them to <path>.tmp.
            // File.WriteAllText on the temp path is what can throw here
            // (unwritable parent directory, path-is-directory, etc.); if
            // it does, we roll back the transaction and the on-disk
            // corpus file is untouched.
            if (corpusEntries.Count > 0)
            {
                var existing = File.Exists(benchmarkFilePath)
                    ? File.ReadAllText(benchmarkFilePath)
                    : string.Empty;
                var trailing = existing.Length == 0 || existing.EndsWith("\n", StringComparison.Ordinal)
                    ? string.Empty
                    : "\n";
                var sb = new StringBuilder();
                sb.Append(existing);
                sb.Append(trailing);
                for (int i = 0; i < corpusEntries.Count; i++)
                {
                    if (i > 0) sb.Append('\n');
                    sb.Append(corpusEntries[i]);
                }
                sb.Append('\n');
                File.WriteAllText(tempPath, sb.ToString());
            }

            tx.Commit();
        }
        catch
        {
            try { tx.Rollback(); } catch { /* best-effort */ }
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best-effort */ }
            throw;
        }

        // DB is committed; now atomically swap the corpus file into place.
        // A failure here is much less likely (same filesystem, rename),
        // but if it happens we still try to clean up the temp file.
        if (corpusEntries.Count > 0)
        {
            try
            {
                File.Move(tempPath, benchmarkFilePath, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best-effort */ }
                throw;
            }
        }

        return new CandidateResolveResult(
            Accepted: acceptIds.Count,
            Rejected: rejectIds.Count,
            CorpusEntries: corpusEntries);
    }

    private static string BuildCorpusEntry(string query, string expectedContains, string addedDate)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append("\"query\":");
        JsonWriter.AppendString(sb, query);
        sb.Append(",\"expected_content_contains\":");
        JsonWriter.AppendString(sb, expectedContains);
        sb.Append(",\"expected_tier\":\"warm\"");
        sb.Append(",\"source\":\"grow\"");
        sb.Append(",\"added\":");
        JsonWriter.AppendString(sb, addedDate);
        sb.Append('}');
        return sb.ToString();
    }
}
