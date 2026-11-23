using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using TotalRecall.Infrastructure.Transcripts;
using Xunit;
using Xunit.Abstractions;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

/// <summary>
/// One-off measurement (spec section 5): the word queries over a synthetic archive of 100,000 chunks of natural-looking
/// text. Skipped; run by hand by removing the Skip, and read the output. Generated data is never committed.
/// </summary>
public sealed class TranscriptSearchMeasurement
{
    private readonly ITestOutputHelper _out;

    public TranscriptSearchMeasurement(ITestOutputHelper output) => _out = output;

    [Fact(Skip = "measurement")]
    public void WordsQuery_On100000Chunks()
    {
        using var db = new TranscriptTestDb();
        var rng = new Random(42);

        // Zipf-like vocabulary: a few very common words, a long tail of rare ones.
        var common = new[] { "the", "to", "and", "of", "in", "is", "it", "we", "that", "this", "for", "with", "on", "are", "be", "have",
            "not", "you", "can", "will", "should", "need", "file", "code", "test", "error", "run", "build", "use", "add" };
        var vocab = common.Concat(Enumerable.Range(0, 8000).Select(i => "w" + ToWord(i))).ToArray();
        string Word()
        {
            var u = rng.NextDouble();
            var idx = (int)(vocab.Length * Math.Pow(u, 3.0)); // skewed to the start
            return vocab[Math.Min(idx, vocab.Length - 1)];
        }

        const int sessions = 1000, perSession = 100;
        var sw = Stopwatch.StartNew();
        using (var conn = db.Connect())
        {
            using var tx = conn.BeginTransaction();
            var sid = new List<long>();
            for (var s = 0; s < sessions; s++)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText =
                    "INSERT INTO tx_session (host, host_session_id, project_key, project_key_source, kind) " +
                    "VALUES ('pi', $h, $p, 'remote', $k); SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$h", "s" + s);
                cmd.Parameters.AddWithValue("$p", "owner/proj" + (s % 20));
                cmd.Parameters.AddWithValue("$k", s % 10 == 0 ? "automated" : "interactive");
                sid.Add(Convert.ToInt64(cmd.ExecuteScalar()));
            }

            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText =
                "INSERT INTO tx_chunk (session_id, exchange_start, exchange_end, part, chunk_type, ts, redaction_version, content) " +
                "VALUES ($s, $e, $e, 0, 'dialogue', $ts, 1, $c)";
            var ps = ins.Parameters.Add("$s", SqliteType.Integer);
            var pe = ins.Parameters.Add("$e", SqliteType.Integer);
            var pt = ins.Parameters.Add("$ts", SqliteType.Text);
            var pc = ins.Parameters.Add("$c", SqliteType.Text);
            var sb = new StringBuilder();
            var rareAt = new HashSet<int> { 12_345, 54_321, 99_001 };
            var n = 0;
            for (var s = 0; s < sessions; s++)
            {
                for (var e = 0; e < perSession; e++, n++)
                {
                    sb.Clear();
                    for (var w = 0; w < 60; w++) sb.Append(Word()).Append(' ');
                    if (rareAt.Contains(n)) sb.Append("zebra quartz marmalade ledger ");
                    ps.Value = sid[s];
                    pe.Value = e;
                    pt.Value = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddHours(n % 8000).ToString("o");
                    pc.Value = sb.ToString();
                    ins.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
        _out.WriteLine($"built 100000 chunks in {sw.ElapsedMilliseconds} ms");

        var sentence = "how should we use the error code in this test to build the file and run it";
        var worst = "the to and of in is it we that this for with"; // the 12 commonest tokens, in nearly every chunk
        var rare = "zebra quartz marmalade ledger";
        var filters = new Dictionary<string, ChunkFilter>
        {
            ["no filter"] = new(null, null, null, false),
            ["project+interactive+since"] = new(null, "owner/proj3", new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), true),
        };

        using var reads = db.Store.BeginSearchReads();
        foreach (var (name, filter) in filters)
        {
            Measure($"words(common 12-token sentence) [{name}]", () => reads.Text(TranscriptQueryBuilder.Words(sentence)!, filter, 40));
            Measure($"words(12 commonest tokens)       [{name}]", () => reads.Text(TranscriptQueryBuilder.Words(worst)!, filter, 40));
            Measure($"phrase(common sentence)         [{name}]", () => reads.Text(TranscriptQueryBuilder.Phrase(sentence)!, filter, 40));
            Measure($"words(rare 4 tokens)             [{name}]", () => reads.Text(TranscriptQueryBuilder.Words(rare)!, filter, 40));
            Measure($"phrase(rare)                     [{name}]", () => reads.Text(TranscriptQueryBuilder.Phrase(rare)!, filter, 40));
        }
    }

    private void Measure(string label, Func<IReadOnlyList<TextHit>> run)
    {
        var times = new List<long>();
        var hits = 0;
        for (var i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            hits = run().Count;
            times.Add(sw.ElapsedMilliseconds);
        }
        times.Sort();
        _out.WriteLine($"{label}: first {times[0]}..median {times[2]}..max {times[^1]} ms, {hits} hits");
    }

    private static string ToWord(int i)
    {
        var sb = new StringBuilder();
        do { sb.Insert(0, (char)('a' + i % 26)); i /= 26; } while (i > 0);
        return sb.ToString();
    }
}
