using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using TotalRecall.Infrastructure.Transcripts;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

/// <summary>
/// The archive's search reads (search the archive, spec section 5 and "Store read API"): filtered nearest neighbours,
/// filtered word candidates, counts, chunk rows with session fields, and one read transaction per search.
/// Archives are seeded by raw SQL; vectors are unit vectors at a known angle to the query, so the order is known.
/// </summary>
public sealed class TranscriptSearchReadsTests : IDisposable
{
    private static readonly ChunkFilter All = new(null, null, null, false);
    private readonly TranscriptTestDb _db = new();

    public void Dispose() => _db.Dispose();

    /// <summary>A 384-float unit vector at <paramref name="angle"/> radians to the query vector (which is <see cref="Query"/>).</summary>
    private static float[] Unit(double angle)
    {
        var v = new float[384];
        v[0] = (float)Math.Cos(angle);
        v[1] = (float)Math.Sin(angle);
        return v;
    }

    private static float[] Query() => Unit(0);

    private static long EpochOf(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeSeconds();

    private long AddChunk(
        MsSqliteConnection conn, long session, string content, int start, string ts = "2026-03-01T00:00:00Z",
        string type = "dialogue", bool error = false, double? angle = null, string project = "owner/repo", string kind = "interactive")
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "INSERT INTO tx_chunk (session_id, exchange_start, exchange_end, part, chunk_type, has_error, ts, redaction_version, content) " +
                "VALUES ($s, $e, $e + 1, 0, $t, $err, $ts, 1, $c); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$s", session);
            cmd.Parameters.AddWithValue("$e", start);
            cmd.Parameters.AddWithValue("$t", type);
            cmd.Parameters.AddWithValue("$err", error ? 1 : 0);
            cmd.Parameters.AddWithValue("$ts", ts);
            cmd.Parameters.AddWithValue("$c", content);
            var id = Convert.ToInt64(cmd.ExecuteScalar());
            if (angle is { } a)
                AddVector(conn, id, session, project, kind, EpochOf(ts), Unit(a));
            return id;
        }
    }

    private static void AddVector(MsSqliteConnection conn, long chunkId, long sessionId, string project, string kind, long ts, float[] vector)
    {
        var bytes = new byte[384 * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO tx_chunk_vec (rowid, session_id, project, kind, ts, embedding) VALUES ($r, $s, $p, $k, $t, $e); " +
            "UPDATE tx_chunk SET embedded = 1 WHERE id = $r;";
        cmd.Parameters.AddWithValue("$r", chunkId);
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$p", project);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$t", ts);
        cmd.Parameters.AddWithValue("$e", bytes);
        cmd.ExecuteNonQuery();
    }

    private sealed record Seed(long S1, long S2, long S3, long S4, long C1a, long C1b, long C2, long C3, long C4);

    /// <summary>
    /// s1 owner/repo interactive, s2 owner/repo automated, s3 other/proj interactive, s4 other/proj interactive (March 2026 vs
    /// January 2026 chunks). Every chunk says "database locked" so word hits cover all of them.
    /// </summary>
    private Seed Populate()
    {
        var s1 = _db.AddSession("claude-code", "sess-1", "owner/repo", kind: "interactive", title: "First");
        var s2 = _db.AddSession("claude-code", "sess-2", "owner/repo", kind: "automated", title: "Auto");
        var s3 = _db.AddSession("pi", "sess-3", "other/proj", kind: "interactive", title: "Third");
        var s4 = _db.AddSession("pi", "sess-4", "other/proj", kind: "interactive", title: "Fourth");
        using var conn = _db.Connect();
        var c1a = AddChunk(conn, s1, "the database locked during ingest", 0, "2026-03-01T00:00:00Z", angle: 0.30);
        var c1b = AddChunk(conn, s1, "unrelated words about colour", 1, "2026-03-02T00:00:00Z", angle: 0.90);
        var c2 = AddChunk(conn, s2, "automated run saw database locked", 0, "2026-03-03T00:00:00Z", angle: 0.10, kind: "automated");
        var c3 = AddChunk(conn, s3, "another database locked database locked case", 0, "2026-03-04T00:00:00Z", angle: 0.50, project: "other/proj");
        var c4 = AddChunk(conn, s4, "old database locked note", 0, "2026-01-05T00:00:00Z", angle: 0.20, project: "other/proj", type: "compaction-summary", error: true);
        return new Seed(s1, s2, s3, s4, c1a, c1b, c2, c3, c4);
    }

    [Fact]
    public void Vector_FiltersInsideTheSearch_Session()
    {
        var seed = Populate();
        using var reads = _db.Store.BeginSearchReads();
        var hits = reads.Vector(Query(), All with { SessionId = seed.S1 }, 10);
        Assert.Equal(new[] { seed.C1a, seed.C1b }, hits.Select(h => h.ChunkId).ToArray());
        Assert.Equal(Math.Cos(0.30), hits[0].Similarity, 4);
        Assert.Equal(Math.Cos(0.90), hits[1].Similarity, 4);
    }

    [Fact]
    public void Vector_FilterProject_Kind_Since()
    {
        var seed = Populate();
        using var reads = _db.Store.BeginSearchReads();

        Assert.Equal(new[] { seed.C2, seed.C1a, seed.C1b },
            reads.Vector(Query(), All with { Project = "owner/repo" }, 10).Select(h => h.ChunkId).ToArray());
        Assert.Equal(new[] { seed.C4, seed.C1a, seed.C3, seed.C1b },
            reads.Vector(Query(), All with { InteractiveOnly = true }, 10).Select(h => h.ChunkId).ToArray());
        Assert.Equal(new[] { seed.C2, seed.C3, seed.C1b },
            reads.Vector(Query(), All with { SinceEpoch = EpochOf("2026-03-02T00:00:00Z") }, 10).Select(h => h.ChunkId).ToArray());

        var combined = new ChunkFilter(null, "owner/repo", EpochOf("2026-03-02T00:00:00Z"), true);
        Assert.Equal(new[] { seed.C1b }, reads.Vector(Query(), combined, 10).Select(h => h.ChunkId).ToArray());

        Assert.Equal(new[] { seed.C4 },
            reads.Vector(Query(), new ChunkFilter(seed.S4, "other/proj", EpochOf("2026-01-01T00:00:00Z"), true), 10)
                .Select(h => h.ChunkId).ToArray());
    }

    [Fact]
    public void Vector_ScopedQuery_ReturnsItsOwnChunks_AlthoughOthersAreNearer()
    {
        var sa = _db.AddSession("pi", "scoped", "owner/scoped");
        var sb = _db.AddSession("pi", "crowd", "owner/crowd");
        long mine;
        using (var conn = _db.Connect())
        {
            mine = AddChunk(conn, sa, "my far chunk", 0, angle: 1.2, project: "owner/scoped");
            for (var i = 0; i < 100; i++)
                AddChunk(conn, sb, "near chunk " + i, i, angle: 0.001 * (i + 1), project: "owner/crowd");
        }

        using var reads = _db.Store.BeginSearchReads();

        // the unscoped nearest 5 are all crowd chunks
        Assert.DoesNotContain(mine, reads.Vector(Query(), All, 5).Select(h => h.ChunkId));

        // scoped by session, by project, and by both: the filter applies inside the search, so the far chunk is found
        Assert.Equal(new[] { mine }, reads.Vector(Query(), All with { SessionId = sa }, 5).Select(h => h.ChunkId).ToArray());
        Assert.Equal(new[] { mine }, reads.Vector(Query(), All with { Project = "owner/scoped" }, 5).Select(h => h.ChunkId).ToArray());
        Assert.Equal(new[] { mine }, reads.Vector(Query(), new ChunkFilter(sa, "owner/scoped", null, true), 5).Select(h => h.ChunkId).ToArray());
    }

    [Fact]
    public void Vector_KGreaterThanRows_ReturnsAll()
    {
        var seed = Populate();
        using var reads = _db.Store.BeginSearchReads();
        var hits = reads.Vector(Query(), All, 500);
        Assert.Equal(5, hits.Count);
        Assert.Equal(seed.C2, hits[0].ChunkId);
        Assert.True(hits.Zip(hits.Skip(1), (a, b) => a.Similarity >= b.Similarity).All(x => x));
    }

    [Fact]
    public void Vector_WrongLength_Throws()
    {
        using var reads = _db.Store.BeginSearchReads();
        Assert.Throws<ArgumentException>(() => reads.Vector(new float[3], All, 5));
    }

    [Fact]
    public void Vector_NonPositiveK_IsEmpty()
    {
        Populate();
        using var reads = _db.Store.BeginSearchReads();
        Assert.Empty(reads.Vector(Query(), All, 0));
    }

    [Fact]
    public void Text_PhraseAndOr_BothExecute_OrderedByBm25()
    {
        var seed = Populate();
        using var reads = _db.Store.BeginSearchReads();

        var phrase = reads.Text(TranscriptQueryBuilder.Phrase("database locked")!, All, 10);
        Assert.Equal(new[] { seed.C1a, seed.C2, seed.C3, seed.C4 }.OrderBy(x => x), phrase.Select(h => h.ChunkId).OrderBy(x => x));
        Assert.DoesNotContain(seed.C1b, phrase.Select(h => h.ChunkId));
        // bm25 ascending (best, most negative, first)
        Assert.True(phrase.Zip(phrase.Skip(1), (a, b) => a.Bm25 <= b.Bm25).All(x => x));
        // the chunk that says it twice outranks the ones that say it once
        Assert.Equal(seed.C3, phrase[0].ChunkId);

        var words = reads.Text(TranscriptQueryBuilder.Words("colour database")!, All, 10);
        Assert.Contains(seed.C1b, words.Select(h => h.ChunkId));
        Assert.Contains(seed.C3, words.Select(h => h.ChunkId));

        Assert.Single(reads.Text(TranscriptQueryBuilder.Phrase("database locked")!, All, 1));
    }

    [Fact]
    public void Text_FiltersApplyInTheQuery()
    {
        var seed = Populate();
        using var reads = _db.Store.BeginSearchReads();
        var match = TranscriptQueryBuilder.Phrase("database locked")!;

        Assert.Equal(new[] { seed.C1a }, reads.Text(match, All with { SessionId = seed.S1 }, 10).Select(h => h.ChunkId).ToArray());
        Assert.Equal(new[] { seed.C3, seed.C4 }.OrderBy(x => x),
            reads.Text(match, All with { Project = "other/proj" }, 10).Select(h => h.ChunkId).OrderBy(x => x));
        Assert.Equal(new[] { seed.C1a, seed.C2, seed.C3 }.OrderBy(x => x),
            reads.Text(match, All with { SinceEpoch = EpochOf("2026-03-01T00:00:00Z") }, 10).Select(h => h.ChunkId).OrderBy(x => x));
        Assert.Equal(new[] { seed.C3 },
            reads.Text(match, new ChunkFilter(null, "other/proj", EpochOf("2026-03-01T00:00:00Z"), true), 10).Select(h => h.ChunkId).ToArray());
    }

    [Fact]
    public void Text_DoesNotMatchAutomatedWhenInteractiveOnly()
    {
        var seed = Populate();
        using var reads = _db.Store.BeginSearchReads();
        var match = TranscriptQueryBuilder.Phrase("database locked")!;

        Assert.Contains(seed.C2, reads.Text(match, All, 10).Select(h => h.ChunkId));
        Assert.DoesNotContain(seed.C2, reads.Text(match, All with { InteractiveOnly = true }, 10).Select(h => h.ChunkId));
        // a named session is searched whatever its kind (the caller drops InteractiveOnly for session scope)
        Assert.Equal(new[] { seed.C2 }, reads.Text(match, All with { SessionId = seed.S2 }, 10).Select(h => h.ChunkId).ToArray());
    }

    [Fact]
    public void CountInScope_Unembedded_VersusAll()
    {
        var seed = Populate();
        using (var conn = _db.Connect())
            AddChunk(conn, seed.S1, "no vector for this one", 5, "2026-03-05T00:00:00Z");

        using var reads = _db.Store.BeginSearchReads();
        Assert.Equal(6, reads.CountInScope(All, onlyUnembedded: false));
        Assert.Equal(1, reads.CountInScope(All, onlyUnembedded: true));
        Assert.Equal(3, reads.CountInScope(All with { SessionId = seed.S1 }, false));
        Assert.Equal(1, reads.CountInScope(All with { SessionId = seed.S1 }, true));
        Assert.Equal(0, reads.CountInScope(All with { SessionId = seed.S3 }, true));
        Assert.Equal(5, reads.CountInScope(All with { InteractiveOnly = true }, false));
        Assert.Equal(4, reads.CountInScope(All with { Project = "owner/repo" }, false));
        Assert.Equal(1, reads.CountInScope(All with { SinceEpoch = EpochOf("2026-03-05T00:00:00Z") }, false));
    }

    [Fact]
    public void Chunks_ReturnsSessionFields()
    {
        var seed = Populate();
        using var reads = _db.Store.BeginSearchReads();
        var rows = reads.Chunks(new[] { seed.C4, seed.C1a, 99999L });

        Assert.Equal(2, rows.Count);
        var c4 = rows[seed.C4];
        Assert.Equal(seed.C4, c4.Id);
        Assert.Equal(seed.S4, c4.SessionId);
        Assert.Equal("pi:sess-4", c4.SessionRef);
        Assert.Equal("other/proj", c4.Project);
        Assert.Equal("interactive", c4.Kind);
        Assert.Equal("Fourth", c4.Title);
        Assert.Equal("2026-01-05T00:00:00Z", c4.Ts);
        Assert.Equal("compaction-summary", c4.ChunkType);
        Assert.True(c4.HasError);
        Assert.Equal(0, c4.ExchangeStart);
        Assert.Equal(1, c4.ExchangeEnd);
        Assert.Equal("old database locked note", c4.Content);
        Assert.False(rows[seed.C1a].HasError);
        Assert.Equal("claude-code:sess-1", rows[seed.C1a].SessionRef);
    }

    [Fact]
    public void Chunks_Empty_AndMany()
    {
        var seed = Populate();
        using var reads = _db.Store.BeginSearchReads();
        Assert.Empty(reads.Chunks(Array.Empty<long>()));
        var ids = Enumerable.Range(1, 1200).Select(i => (long)i).ToArray(); // more than one parameter batch
        Assert.Equal(5, reads.Chunks(ids).Count);
    }

    [Fact]
    public void ResolveSession_Unknown_IsNull()
    {
        var seed = Populate();
        using var reads = _db.Store.BeginSearchReads();
        Assert.Equal(seed.S3, reads.ResolveSession("pi", "sess-3"));
        Assert.Null(reads.ResolveSession("pi", "nope"));
        Assert.Null(reads.ResolveSession("claude-code", "sess-3"));
    }

    [Fact]
    public void ReadsAreOneTransaction()
    {
        var seed = Populate();
        long late;
        using (var reads = _db.Store.BeginSearchReads())
        {
            Assert.Equal(5, reads.CountInScope(All, false)); // the snapshot starts here

            _db.Exec("DELETE FROM tx_chunk WHERE id = " + seed.C3);
            var s5 = _db.AddSession("pi", "sess-5");
            late = _db.AddChunk(s5, "late chunk database locked");
            _db.AddChunk(s5, "late chunk two", 1);

            Assert.Equal(5, reads.CountInScope(All, false));
            Assert.Equal(seed.C3, reads.Chunks(new[] { seed.C3 }).Keys.Single());
            Assert.Null(reads.ResolveSession("pi", "sess-5"));
            Assert.Equal(5, reads.Vector(Query(), All, 50).Count);
            var seen = reads.Text(TranscriptQueryBuilder.Phrase("database locked")!, All, 50).Select(h => h.ChunkId).ToArray();
            Assert.Contains(seed.C3, seen);
            Assert.DoesNotContain(late, seen);
        }

        // the store has one connection, so the next search begins after the first is disposed
        using var fresh = _db.Store.BeginSearchReads();
        Assert.Equal(6, fresh.CountInScope(All, false)); // 5 - 1 + 2
        var now = fresh.Text(TranscriptQueryBuilder.Phrase("database locked")!, All, 50).Select(h => h.ChunkId).ToArray();
        Assert.DoesNotContain(seed.C3, now);
        Assert.Contains(late, now);
        Assert.Empty(fresh.Chunks(new[] { seed.C3 }));
    }

    [Fact]
    public void Dispose_EndsTheTransaction_AndTheStoreStillWorks()
    {
        Populate();
        var reads = _db.Store.BeginSearchReads();
        reads.CountInScope(All, false);
        reads.Dispose();
        reads.Dispose(); // idempotent
        Assert.Equal(5, _db.Store.Status().Chunks);
        using var again = _db.Store.BeginSearchReads();
        Assert.Equal(5, again.CountInScope(All, false));
    }
}
