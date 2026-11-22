using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.FSharp.Core;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Tests.TestSupport;
using TotalRecall.Infrastructure.Transcripts;
using TotalRecall.TestSupport.Transcripts;
using Xunit;
using Xunit.Abstractions;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

/// <summary>
/// The life of a vector row after the worker wrote it (search the archive, spec section 7 and section 8, test 11): every
/// forget removes the vectors and, after its truncating checkpoint, their bytes from the file and the write-ahead log;
/// an ingest that changes or drops a chunk removes its vector; a session whose kind flips on a resumed pass, or whose
/// project is rewritten, carries the change into its vector rows and the search's filters follow; a reused chunk id
/// meets no old vector; and a forget of everything on 10,000 vectors completes. Real archives, real ingest, the real
/// worker with <see cref="ScriptedEmbedder"/>; no model.
/// </summary>
public sealed class TranscriptVectorLifecycleTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly EmbedGateResult Ready = new(EmbedState.Ready, "ready");
    private static readonly ArchiveFingerprint Fp = new("local", "bge-small-en-v1.5", "rev1", 384);
    private static readonly EmbedBudget NoBudget = new(null, null);

    private readonly ITestOutputHelper _output;
    private readonly IngestHarness _h = new();

    public TranscriptVectorLifecycleTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => _h.Dispose();

    private TranscriptTestDb Db => _h.Db;

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static EmbedRun Embed(TranscriptTestDb db, IEmbedder embedder) =>
        new TranscriptEmbedWorker(db.Store, embedder, new ManualTimeProvider(T0), "host-a", 100, (_, _) => true)
            .Run(Ready, Fp, NoBudget, CancellationToken.None);

    private long Count(string sql) => Db.Scalar(sql);

    private static Core.Config.EmbeddingConfig LocalEmbedding() => new(
        "bge-small-en-v1.5", 384, FSharpOption<string>.None,
        FSharpOption<string>.None, FSharpOption<string>.None, FSharpOption<string>.None,
        FSharpOption<string>.None, FSharpOption<string>.None, FSharpOption<string>.None, FSharpOption<string>.None);

    private TranscriptSearchResponse Search(
        TranscriptTestDb db, IEmbedder embedder, string query, string? project = null, bool includeAutomated = false) =>
        new TranscriptSearchService(
                () => new TranscriptSearchContext(
                    new TranscriptLocation(db.Path, IsMainDatabase: false),
                    new TranscriptsSettings(true, string.Empty, null, 0, false),
                    "sqlite", LocalEmbedding(), embedder),
                new ManualTimeProvider(T0))
            .Search(new TranscriptSearchRequest(query, null, project, null, null, includeAutomated, 10), CancellationToken.None);

    /// <summary>
    /// A 384-float vector whose floats 100 to 115 are a pattern of 64 bytes unique to <paramref name="seed"/> (finite
    /// positive values whose bit patterns come from the seed), the rest the text's ordinary scripted vector.
    /// </summary>
    private static float[] PatternVector(int seed)
    {
        var v = ScriptedEmbedder.Vector("pattern " + seed.ToString(CultureInfo.InvariantCulture));
        for (var i = 0; i < 16; i++)
        {
            var mantissa = (int)(((uint)seed * 2654435761u + (uint)i * 40503u + 0x5bd1e995u) & 0x007FFFFF);
            v[100 + i] = BitConverter.Int32BitsToSingle(0x3E000000 | mantissa);
        }
        return v;
    }

    private static byte[] PatternBytes(int seed)
    {
        var v = PatternVector(seed);
        var bytes = new byte[64];
        Buffer.BlockCopy(v, 100 * sizeof(float), bytes, 0, 64);
        return bytes;
    }

    /// <summary>Whether <paramref name="needle"/> is in the database file, its <c>-wal</c> or its <c>-shm</c>, and where.</summary>
    private static string? FindBytes(TranscriptTestDb db, byte[] needle)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var hay = TranscriptTestDb.ReadShared(db.Path + suffix);
            if (hay.AsSpan().IndexOf(needle) >= 0)
                return "the file" + suffix;
        }
        return null;
    }

    /// <summary>Rows keyed by vector rowid in <c>tx_chunk_vec</c> and its per-row shadow tables, for <paramref name="ids"/>.</summary>
    private long VectorRowsFor(TranscriptTestDb db, IReadOnlyCollection<long> ids)
    {
        var list = string.Join(",", ids);
        var total = db.Scalar($"SELECT count(*) FROM tx_chunk_vec WHERE rowid IN ({list})");
        using var conn = db.Connect();
        var shadows = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            // The per-row shadow tables (sqlite-vec 0.1.9): <name>_rowids maps a rowid to its chunk slot, and
            // <name>_metadatatextNN keeps long text metadata by rowid. The block tables (_chunks, _vector_chunksNN,
            // _metadatachunksNN) are keyed by block, not by row: the byte scan covers them.
            cmd.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'table' AND " +
                "(name = 'tx_chunk_vec_rowids' OR name LIKE 'tx_chunk_vec_metadatatext%' OR name = 'tx_chunk_vec_auxiliary')";
            using var r = cmd.ExecuteReader();
            while (r.Read()) shadows.Add(r.GetString(0));
        }
        Assert.Contains("tx_chunk_vec_rowids", shadows);
        foreach (var table in shadows)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT count(*) FROM \"{table}\" WHERE rowid IN ({list})";
            total += Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        return total;
    }

    private sealed record Seeded(long SessionId, string HostSessionId, List<long> ChunkIds, List<int> Seeds);

    /// <summary>A session of three chunks whose vectors carry patterns <c>seedBase</c> to <c>seedBase + 2</c>.</summary>
    private static Seeded SeedPatterned(TranscriptTestDb db, ScriptedEmbedder embedder, string id, string project, int seedBase)
    {
        var s = db.AddSession("claude-code", id, projectKey: project);
        var ids = new List<long>();
        var seeds = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            var content = $"{project} | interactive\n\nchunk {i} of {id} about the release";
            ids.Add(db.AddChunk(s, content, i));
            var seed = seedBase + i;
            seeds.Add(seed);
            embedder.On(content, () => PatternVector(seed));
        }
        return new Seeded(s, id, ids, seeds);
    }

    private void AssertGone(TranscriptTestDb db, Seeded s, string after)
    {
        Assert.Equal(0, VectorRowsFor(db, s.ChunkIds));
        foreach (var seed in s.Seeds)
        {
            var where = FindBytes(db, PatternBytes(seed));
            Assert.True(where is null, $"after {after}, the vector bytes of {s.HostSessionId} (pattern {seed}) are still in {where}");
        }
    }

    private void AssertPresent(TranscriptTestDb db, Seeded s)
    {
        Assert.Equal(3, db.Scalar($"SELECT count(*) FROM tx_chunk_vec WHERE rowid IN ({string.Join(",", s.ChunkIds)})"));
        foreach (var seed in s.Seeds)
            Assert.True(FindBytes(db, PatternBytes(seed)) is not null, $"pattern {seed} of {s.HostSessionId} is not stored at all");
    }

    // ---- forget ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Forget_Session_Project_Everything_RemovesVectorsAndTheirBytes()
    {
        var db = Db;
        var embedder = new ScriptedEmbedder();
        var a = SeedPatterned(db, embedder, "sess-a", "owner/repo", 1000);
        var b = SeedPatterned(db, embedder, "sess-b", "owner/repo", 2000);
        var c = SeedPatterned(db, embedder, "sess-c", "other/proj", 3000);
        var run = Embed(db, embedder);
        Assert.Equal(9, run.Embedded);
        foreach (var s in new[] { a, b, c }) AssertPresent(db, s); // the scan finds what is there

        var r1 = db.Store.ForgetSession("claude-code", "sess-a");
        Assert.True(r1.WalTruncated, r1.Warning);
        AssertGone(db, a, "forgetting the session");
        AssertPresent(db, b);
        AssertPresent(db, c);

        var r2 = db.Store.ForgetProject("owner/repo");
        Assert.True(r2.WalTruncated, r2.Warning);
        AssertGone(db, b, "forgetting the project");
        AssertPresent(db, c);

        var r3 = db.Store.ForgetAll(T0);
        Assert.True(r3.WalTruncated, r3.Warning);
        AssertGone(db, c, "forgetting everything");
        Assert.Equal(0, db.Scalar("SELECT count(*) FROM tx_chunk_vec"));
        Assert.Equal(0, db.Scalar("SELECT count(*) FROM tx_chunk_vec_rowids"));
    }

    // ---- ingest ----------------------------------------------------------------------------------------------------

    private static IngestChunk Chunk(int unit, string content) =>
        new(unit, unit, 0, "dialogue", false, "2026-01-01T00:00:00.0000000Z", 1, content);

    private static SessionIngest Pass(IReadOnlyList<IngestChunk> chunks, SessionIngestState? prior) =>
        new("claude-code", "ingest-1", "owner/repo", "remote", "/w/repo", "/w/repo", "2026-01-01T00:00:00.0000000Z",
            "2026-01-01T01:00:00.0000000Z", "interactive", "A title", "/logs/ingest-1.jsonl", 4096, 1700000000000, "fp-1",
            10, chunks.Max(c => c.ExchangeEnd) + 1, 1, null, prior, 0, chunks);

    [Fact]
    public void Ingest_ChangedAndVanishedChunks_RemoveTheirVectors()
    {
        // Part 2's write path (ApplyIngest): four units, embedded; then a pass rebuilt from unit 0 that keeps unit 0,
        // changes unit 1 and no longer has units 2 and 3 (a file rewritten shorter).
        var first = Enumerable.Range(0, 4).Select(u => Chunk(u, $"header\n\nunit {u} as first written")).ToList();
        Assert.Equal(IngestOutcomeKind.Applied, Db.Store.ApplyIngest(Pass(first, null)).Kind);
        var embedder = new ScriptedEmbedder();
        Assert.Equal(4, Embed(Db, embedder).Embedded);
        var before = Rows();
        var idOf = before.ToDictionary(kv => kv.Value, kv => kv.Key);
        long unit0 = idOf[first[0].Content], unit1 = idOf[first[1].Content], unit2 = idOf[first[2].Content], unit3 = idOf[first[3].Content];

        var second = new List<IngestChunk> { first[0], Chunk(1, "header\n\nunit 1 rewritten with other words") };
        var outcome = Db.Store.ApplyIngest(Pass(second, Db.Store.GetIngestState("claude-code", "ingest-1")));
        Assert.Equal(new IngestOutcome(IngestOutcomeKind.Applied, ChunksChanged: 1, ChunksUnchanged: 1, ChunksRemoved: 2), outcome);

        Assert.Equal(1, Count($"SELECT count(*) FROM tx_chunk_vec WHERE rowid = {unit0}")); // unchanged: kept
        Assert.Equal(0, VectorRowsFor(Db, new[] { unit1 }));                // changed: removed
        Assert.Equal(0, VectorRowsFor(Db, new[] { unit2, unit3 }));         // vanished: removed
        Assert.Equal(0, Count($"SELECT embedded FROM tx_chunk WHERE id = {unit1}"));
        Assert.Equal(1, Count($"SELECT embedded FROM tx_chunk WHERE id = {unit0}"));
        // No orphan and no stale vector anywhere: a vector row exists exactly for the embedded chunks.
        Assert.Equal(0, Count("SELECT count(*) FROM tx_chunk_vec v WHERE NOT EXISTS (SELECT 1 FROM tx_chunk c WHERE c.id = v.rowid AND c.embedded = 1)"));
        Assert.Equal(1, Count("SELECT count(*) FROM tx_chunk_vec"));

        // The next run embeds the changed chunk with its new text, and only it.
        var calls = embedder.Calls.Count;
        Assert.Equal(1, Embed(Db, embedder).Embedded);
        Assert.Equal(new[] { EmbedProbe.Text, "header\n\nunit 1 rewritten with other words" }, embedder.Calls.Skip(calls).ToArray());
        Assert.Equal(2, Count("SELECT count(*) FROM tx_chunk_vec"));
    }

    private Dictionary<long, string> Rows()
    {
        var rows = new Dictionary<long, string>();
        using var conn = Db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, content FROM tx_chunk";
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows[r.GetInt64(0)] = r.GetString(1);
        return rows;
    }

    [Fact]
    public void KindFlip_OnResume_ChangesVectorKind_AndSearchFilterFollows()
    {
        var s = _h.Home.Place("automated-opening", _h.Cwd, "flip-1");
        // A compaction boundary and an assistant-only unit, so the lone user turn lies in a settled unit and the next
        // pass resumes (the setup of the ingester's kind-flip test).
        _h.Home.AppendLine(s,
            "{\"parentUuid\":null,\"isSidechain\":false,\"type\":\"system\",\"subtype\":\"compact_boundary\"," +
            "\"content\":\"Conversation compacted\",\"uuid\":\"00000000-0000-4000-8000-0000000000b1\"," +
            "\"timestamp\":\"2026-01-01T00:00:05.000Z\",\"cwd\":\"" + s.Cwd + "\",\"sessionId\":\"" + s.Id + "\"}");
        _h.Home.AppendLine(s,
            "{\"parentUuid\":null,\"isSidechain\":false,\"type\":\"assistant\",\"message\":{\"role\":\"assistant\"," +
            "\"content\":[{\"type\":\"text\",\"text\":\"Continuing after the boundary. zqxkindflip\"}]}," +
            "\"uuid\":\"00000000-0000-4000-8000-0000000000b2\",\"timestamp\":\"2026-01-01T00:00:06.000Z\"," +
            "\"cwd\":\"" + s.Cwd + "\",\"sessionId\":\"" + s.Id + "\"}");
        Assert.Equal(SessionIngestStatus.New, _h.Ingest(Db, s).Status);
        Assert.Equal("automated", Db.Text("SELECT kind FROM tx_session"));

        var embedder = new ScriptedEmbedder();
        Embed(Db, embedder);
        var chunks = Count("SELECT count(*) FROM tx_chunk");
        Assert.Equal(chunks, Count("SELECT count(*) FROM tx_chunk_vec WHERE kind = 'automated'"));

        const string query = "security problems in the migration code";
        var hidden = Search(Db, embedder, query);
        Assert.True(hidden.MeaningOn);
        Assert.Empty(hidden.Results); // an automated session is not in a default search, by meaning or by words
        var shown = Search(Db, embedder, query, includeAutomated: true);
        Assert.Contains(shown.Results, h => h.Via.Contains("meaning"));

        // A second person turn: the resumed pass finds two turns and the session becomes interactive.
        _h.Home.AppendExchange(s, "A second question from a person.", "A second answer.", 100);
        Assert.Equal(SessionIngestStatus.Updated, _h.Ingest(Db, s).Status);
        Assert.Equal("interactive", Db.Text("SELECT kind FROM tx_session"));
        Assert.Equal(0, Count("SELECT count(*) FROM tx_chunk_vec WHERE kind = 'automated'"));
        Assert.Equal(0, Count(
            "SELECT count(*) FROM tx_chunk_vec v JOIN tx_chunk c ON c.id = v.rowid JOIN tx_session t ON t.id = c.session_id " +
            "WHERE v.kind <> t.kind"));

        Embed(Db, embedder);
        var now = Count("SELECT count(*) FROM tx_chunk");
        Assert.Equal(now, Count("SELECT count(*) FROM tx_chunk_vec WHERE kind = 'interactive'"));
        Assert.Equal(now, Count("SELECT count(*) FROM tx_chunk_vec"));

        var found = Search(Db, embedder, query);
        Assert.True(found.MeaningOn);
        Assert.NotEmpty(found.Results);
        Assert.Contains(found.Results, h => h.Via.Contains("meaning"));
        Assert.All(found.Results, h => Assert.Equal("interactive", h.Kind));
    }

    [Fact]
    public void ProjectUpdate_CarriedIntoVectors()
    {
        var embedder = new ScriptedEmbedder();
        var moved = Db.AddSession("claude-code", "moving", projectKey: "owner/repo");
        var stays = Db.AddSession("claude-code", "staying", projectKey: "owner/repo");
        for (var i = 0; i < 2; i++)
        {
            Db.AddChunk(moved, $"header\n\nthe moving session talks about invoices {i}", i);
            Db.AddChunk(stays, $"header\n\nthe staying session talks about invoices {i}", i);
        }
        Assert.Equal(4, Embed(Db, embedder).Embedded);

        // As Part 8's reproject will: a raw update of the session's project key.
        Db.Exec($"UPDATE tx_session SET project_key = 'owner/renamed' WHERE id = {moved}");

        Assert.Equal(4, Count("SELECT count(*) FROM tx_chunk_vec")); // carried, not deleted
        Assert.Equal(2, Count($"SELECT count(*) FROM tx_chunk_vec WHERE session_id = {moved} AND project = 'owner/renamed'"));
        Assert.Equal(2, Count($"SELECT count(*) FROM tx_chunk_vec WHERE session_id = {stays} AND project = 'owner/repo'"));
        Assert.Equal(4, Count("SELECT count(*) FROM tx_chunk WHERE embedded = 1"));

        var renamed = Search(Db, embedder, "invoices", project: "owner/renamed");
        Assert.True(renamed.MeaningOn);
        Assert.Equal(2, renamed.Results.Count);
        Assert.All(renamed.Results, h => Assert.Equal("claude-code:moving", h.Session));
        Assert.All(renamed.Results, h => Assert.Contains("meaning", h.Via));

        var old = Search(Db, embedder, "invoices", project: "owner/repo");
        Assert.Equal(2, old.Results.Count);
        Assert.All(old.Results, h => Assert.Equal("claude-code:staying", h.Session));
    }

    [Fact]
    public void ChunkIdReuse_NoOldVector()
    {
        var embedder = new ScriptedEmbedder();
        var s = Db.AddSession("claude-code", "reuse");
        Db.AddChunk(s, "header\n\nthe first chunk", 0);
        var last = Db.AddChunk(s, "header\n\nthe old text of the last chunk", 1);
        Embed(Db, embedder);
        Assert.Equal(1, Count($"SELECT count(*) FROM tx_chunk_vec WHERE rowid = {last}"));

        Db.Exec($"DELETE FROM tx_chunk WHERE id = {last}");
        var reused = Db.AddChunk(s, "header\n\nthe new text in a reused id", 2);

        Assert.Equal(last, reused); // SQLite hands out the deleted highest id again
        Assert.Equal(0, Count($"SELECT count(*) FROM tx_chunk_vec WHERE rowid = {reused}"));
        Assert.Equal(0, Count($"SELECT embedded FROM tx_chunk WHERE id = {reused}"));
        Assert.Contains(Db.Store.NextEmbedWork(16, Array.Empty<long>()), w => w.Id == reused);

        Assert.Equal(1, Embed(Db, embedder).Embedded);
        using var conn = Db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT embedding FROM tx_chunk_vec WHERE rowid = {reused}";
        var stored = (byte[])cmd.ExecuteScalar()!;
        var expected = new byte[384 * sizeof(float)];
        Buffer.BlockCopy(ScriptedEmbedder.Vector("header\n\nthe new text in a reused id"), 0, expected, 0, expected.Length);
        Assert.Equal(expected, stored);
    }

    [Fact]
    public void ForgetAll_TenThousandChunks_Completes()
    {
        const int sessions = 100;
        const int perSession = 100;
        var bytes = new byte[384 * sizeof(float)];
        using (var conn = Db.Connect())
        using (var tx = conn.BeginTransaction())
        {
            using var session = conn.CreateCommand();
            session.Transaction = tx;
            session.CommandText =
                "INSERT INTO tx_session (host, host_session_id, project_key, project_key_source, kind) " +
                "VALUES ('claude-code', $id, $p, 'remote', 'interactive'); SELECT last_insert_rowid();";
            var sid = session.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Text);
            var sp = session.Parameters.Add("$p", Microsoft.Data.Sqlite.SqliteType.Text);

            using var chunk = conn.CreateCommand();
            chunk.Transaction = tx;
            chunk.CommandText =
                "INSERT INTO tx_chunk (session_id, exchange_start, exchange_end, part, chunk_type, redaction_version, content, embedded) " +
                "VALUES ($s, $e, $e, 0, 'dialogue', 1, $c, 1); SELECT last_insert_rowid();";
            var cs = chunk.Parameters.Add("$s", Microsoft.Data.Sqlite.SqliteType.Integer);
            var ce = chunk.Parameters.Add("$e", Microsoft.Data.Sqlite.SqliteType.Integer);
            var cc = chunk.Parameters.Add("$c", Microsoft.Data.Sqlite.SqliteType.Text);

            using var vec = conn.CreateCommand();
            vec.Transaction = tx;
            vec.CommandText =
                "INSERT INTO tx_chunk_vec (rowid, session_id, project, kind, ts, embedding) VALUES ($r, $s, $p, 'interactive', 0, $v)";
            var vr = vec.Parameters.Add("$r", Microsoft.Data.Sqlite.SqliteType.Integer);
            var vs = vec.Parameters.Add("$s", Microsoft.Data.Sqlite.SqliteType.Integer);
            var vp = vec.Parameters.Add("$p", Microsoft.Data.Sqlite.SqliteType.Text);
            var vv = vec.Parameters.Add("$v", Microsoft.Data.Sqlite.SqliteType.Blob);

            for (var i = 0; i < sessions; i++)
            {
                var project = i % 2 == 0 ? "owner/repo" : "other/proj";
                sid.Value = "bulk-" + i.ToString(CultureInfo.InvariantCulture);
                sp.Value = project;
                var s = Convert.ToInt64(session.ExecuteScalar(), CultureInfo.InvariantCulture);
                for (var j = 0; j < perSession; j++)
                {
                    cs.Value = s;
                    ce.Value = j;
                    cc.Value = $"header\n\nbulk chunk {j} of session {i}";
                    var id = Convert.ToInt64(chunk.ExecuteScalar(), CultureInfo.InvariantCulture);
                    var v = ScriptedEmbedder.Vector(id.ToString(CultureInfo.InvariantCulture));
                    Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
                    vr.Value = id;
                    vs.Value = s;
                    vp.Value = project;
                    vv.Value = bytes;
                    vec.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
        Assert.Equal(10_000, Count("SELECT count(*) FROM tx_chunk_vec"));

        var sw = Stopwatch.StartNew();
        var result = Db.Store.ForgetAll(T0);
        sw.Stop();

        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"ForgetAll of {sessions} sessions, 10,000 chunks with vectors: {sw.Elapsed.TotalMilliseconds:F0} ms"));
        Assert.Equal(10_000, result.Chunks);
        Assert.True(result.WalTruncated, result.Warning);
        Assert.Equal(0, Count("SELECT count(*) FROM tx_chunk"));
        Assert.Equal(0, Count("SELECT count(*) FROM tx_chunk_vec"));
        Assert.Equal(0, Count("SELECT count(*) FROM tx_chunk_vec_rowids"));
        Assert.True(sw.Elapsed < TimeSpan.FromMinutes(2), $"ForgetAll took {sw.Elapsed}");
    }
}
