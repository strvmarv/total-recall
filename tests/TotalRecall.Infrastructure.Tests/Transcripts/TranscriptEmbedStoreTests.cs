using System;
using System.Collections.Generic;
using System.Linq;
using TotalRecall.Infrastructure.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

/// <summary>
/// The store's embed side (search the archive, spec sections 2 and 3): the work queue, the one-transaction vector
/// write, the lease, the fingerprint, the counts and the last-run record. Not <c>Integration</c>-trait.
/// </summary>
public sealed class TranscriptEmbedStoreTests : IDisposable
{
    private static readonly ArchiveFingerprint Fp = new("local", "bge-small-en-v1.5", "rev1", 384);
    private static readonly DateTimeOffset T0 = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly EmbedLeaseHolder Me = new("host-a", 100, "run-1");

    private readonly TranscriptTestDb _db = new();

    public void Dispose() => _db.Dispose();

    private ITranscriptStore Store => _db.Store;

    private static float[] Vec(int seed = 1, int length = 384)
    {
        var rng = new Random(seed);
        var v = new float[length];
        for (var i = 0; i < length; i++)
            v[i] = (float)(rng.NextDouble() - 0.5);
        return v;
    }

    private static ChunkEmbedding Emb(long id, string content, int seed = 1) => new(id, content, Vec(seed));

    private long Chunk(long session, string content, int start = 0, string? ts = "2026-01-02T03:04:05Z")
    {
        var id = _db.AddChunk(session, content, start);
        if (ts is not null)
            _db.Exec($"UPDATE tx_chunk SET ts = '{ts}' WHERE id = {id}");
        return id;
    }

    private void Lease(EmbedLeaseHolder holder) =>
        Assert.True(Store.TryAcquireEmbedLease(holder, T0, (_, _) => true));

    // ---------------- queue ----------------

    [Fact]
    public void NextEmbedWork_ReturnsUnembeddedByAscendingId_SkipsGaveUpAndExcluded()
    {
        var s = _db.AddSession("pi", "s1");
        var a = Chunk(s, "a", 0);
        var embedded = Chunk(s, "b", 1);
        var gaveUp = Chunk(s, "c", 2);
        var excluded = Chunk(s, "d", 3);
        var e = Chunk(s, "e", 4);
        var f = Chunk(s, "f", 5);
        _db.Exec($"UPDATE tx_chunk SET embedded = 1 WHERE id = {embedded}");
        _db.Exec($"UPDATE tx_chunk SET embed_attempts = 3 WHERE id = {gaveUp}");

        var work = Store.NextEmbedWork(10, new[] { excluded });

        Assert.Equal(new[] { a, e, f }, work.Select(w => w.Id).ToArray());
        Assert.Equal(new[] { "a", "e", "f" }, work.Select(w => w.Content).ToArray());

        var limited = Store.NextEmbedWork(2, Array.Empty<long>());
        Assert.Equal(new[] { a, excluded }, limited.Select(w => w.Id).ToArray());
    }

    // ---------------- queue: a preferred session ----------------

    private static readonly (string Host, string HostSessionId) Cur = ("pi", "cur");

    /// <summary>Bulk-seeds waiting chunks of a session in one transaction (ids ascend with <paramref name="count"/>).</summary>
    private void Bulk(long session, int count, int firstExchange = 0)
    {
        using var conn = _db.Connect();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i + 1 < $count) " +
            "INSERT INTO tx_chunk (session_id, exchange_start, exchange_end, part, chunk_type, redaction_version, content) " +
            "SELECT $s, $first + i, $first + i, 0, 'dialogue', 1, 'bulk ' || (i + $first) FROM n";
        cmd.Parameters.AddWithValue("$count", count);
        cmd.Parameters.AddWithValue("$s", session);
        cmd.Parameters.AddWithValue("$first", firstExchange);
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    private List<long> SessionChunkIds(long session)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id FROM tx_chunk WHERE session_id = {session} ORDER BY id";
        using var r = cmd.ExecuteReader();
        var list = new List<long>();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }

    [Fact]
    public void NextEmbedWork_PreferSession_ReturnsItsChunksFirst_ThenTheRest()
    {
        var older = _db.AddSession("pi", "older");
        Bulk(older, 50);
        var cur = _db.AddSession("pi", "cur");
        Bulk(cur, 5);
        var olderIds = SessionChunkIds(older);
        var curIds = SessionChunkIds(cur);

        var work = Store.NextEmbedWork(8, Array.Empty<long>(), Cur);

        Assert.Equal(curIds.Concat(olderIds.Take(3)).ToArray(), work.Select(w => w.Id).ToArray());
    }

    [Fact]
    public void NextEmbedWork_PreferSession_LowerIds_NoChunkTwice()
    {
        var cur = _db.AddSession("pi", "cur");
        Bulk(cur, 5);
        var older = _db.AddSession("pi", "older");
        Bulk(older, 50);
        var curIds = SessionChunkIds(cur);
        var olderIds = SessionChunkIds(older);
        Assert.True(curIds.Max() < olderIds.Min());

        var work = Store.NextEmbedWork(8, Array.Empty<long>(), Cur);

        var got = work.Select(w => w.Id).ToArray();
        Assert.Equal(8, got.Length);
        Assert.Equal(8, got.Distinct().Count());
        Assert.Equal(curIds.Concat(olderIds.Take(3)).ToArray(), got);
    }

    [Fact]
    public void NextEmbedWork_PreferUnknownSession_IsThePlainRead()
    {
        var s = _db.AddSession("pi", "other");
        Bulk(s, 12);

        var plain = Store.NextEmbedWork(8, Array.Empty<long>());
        var preferred = Store.NextEmbedWork(8, Array.Empty<long>(), ("pi", "no-such-session"));
        var otherHost = Store.NextEmbedWork(8, Array.Empty<long>(), ("claude-code", "other"));

        Assert.Equal(8, plain.Count);
        Assert.Equal(plain.Select(w => w.Id), preferred.Select(w => w.Id));
        Assert.Equal(plain.Select(w => w.Id), otherHost.Select(w => w.Id));
    }

    [Fact]
    public void NextEmbedWork_PreferSession_FewerWaitingThanLimit_AndNothingElse_ReturnsJustThose()
    {
        var cur = _db.AddSession("pi", "cur");
        Bulk(cur, 3);

        var work = Store.NextEmbedWork(8, Array.Empty<long>(), Cur);

        Assert.Equal(SessionChunkIds(cur), work.Select(w => w.Id).ToList());
    }

    [Fact]
    public void HasWaitingEmbedWork_TrueOnlyForASessionWithWaitingChunks()
    {
        var done = _db.AddSession("pi", "done");
        var doneChunk = Chunk(done, "x", 0);
        _db.Exec($"UPDATE tx_chunk SET embedded = 1 WHERE id = {doneChunk}");
        var waiting = _db.AddSession("pi", "waiting");
        Chunk(waiting, "y", 0);
        var gaveUp = _db.AddSession("pi", "gaveup");
        var gaveUpChunk = Chunk(gaveUp, "z", 0);
        _db.Exec($"UPDATE tx_chunk SET embed_attempts = 3 WHERE id = {gaveUpChunk}");
        var retrying = _db.AddSession("pi", "retrying");
        var retryChunk = Chunk(retrying, "w", 0);
        _db.Exec($"UPDATE tx_chunk SET embed_attempts = 2 WHERE id = {retryChunk}");
        _db.AddSession("pi", "empty");

        Assert.False(Store.HasWaitingEmbedWork("pi", "done"));
        Assert.True(Store.HasWaitingEmbedWork("pi", "waiting"));
        Assert.False(Store.HasWaitingEmbedWork("pi", "gaveup"));
        Assert.True(Store.HasWaitingEmbedWork("pi", "retrying"));
        Assert.False(Store.HasWaitingEmbedWork("pi", "empty"));
        Assert.False(Store.HasWaitingEmbedWork("pi", "unknown"));
        Assert.False(Store.HasWaitingEmbedWork("claude-code", "waiting"));
    }

    [Fact]
    public void NextEmbedWork_PreferSession_RespectsExcludeAndAttempts()
    {
        var cur = _db.AddSession("pi", "cur");
        var a = Chunk(cur, "a", 0);
        var b = Chunk(cur, "b", 1);
        var c = Chunk(cur, "c", 2);
        var d = Chunk(cur, "d", 3);
        _db.Exec($"UPDATE tx_chunk SET embed_attempts = 3 WHERE id = {c}");
        _db.Exec($"UPDATE tx_chunk SET embedded = 1 WHERE id = {d}");
        var older = _db.AddSession("pi", "older");
        var o1 = Chunk(older, "o1", 0);
        var o2 = Chunk(older, "o2", 1);
        var oGone = Chunk(older, "o3", 2);
        _db.Exec($"UPDATE tx_chunk SET embed_attempts = 3 WHERE id = {oGone}");

        var work = Store.NextEmbedWork(10, new[] { b, o1 }, Cur);

        // the session's waiting chunks first without the excluded b, the gave-up c and the embedded d; then the rest
        Assert.Equal(new[] { a, o2 }, work.Select(w => w.Id).ToArray());
        Assert.Equal(new[] { "a", "o2" }, work.Select(w => w.Content).ToArray());
        Assert.All(work, w => Assert.True(w.Attempts < 3));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void NextEmbedWork_PreferSession_UsesTheSessionIndex(int excludeCount)
    {
        var sqls = new[]
        {
            TranscriptStore.BuildSessionFirstSql(excludeCount),
            TranscriptStore.HasWaitingEmbedWorkSql,
        };

        foreach (var sql in sqls)
        {
            using var conn = _db.Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
            cmd.Parameters.AddWithValue("$host", "pi");
            cmd.Parameters.AddWithValue("$hid", "cur");
            cmd.Parameters.AddWithValue("$limit", 8);
            for (var i = 0; i < excludeCount; i++)
                cmd.Parameters.AddWithValue("$x" + i, (long)i + 1);
            var plan = new List<string>();
            using (var r = cmd.ExecuteReader())
                while (r.Read()) plan.Add(r.GetString(3));

            var chunkLine = Assert.Single(plan, l => l.Contains("tx_chunk", StringComparison.Ordinal));
            Assert.Contains("SEARCH", chunkLine);
            Assert.Contains("USING INDEX", chunkLine);
            Assert.Contains("session_id=", chunkLine);
            Assert.DoesNotContain(plan, l => l.Contains("idx_tx_chunk_unembedded", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void NextEmbedWork_PreferSession_LargeBacklog_StaysFast()
    {
        var older = _db.AddSession("pi", "older");
        Bulk(older, 20_000);
        var cur = _db.AddSession("pi", "cur");
        Bulk(cur, 5);
        var curIds = SessionChunkIds(cur);
        Store.NextEmbedWork(8, Array.Empty<long>(), Cur); // warm the statement and the page cache

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var work = Store.NextEmbedWork(8, Array.Empty<long>(), Cur);
        watch.Stop();

        Assert.Equal(curIds, work.Take(5).Select(w => w.Id).ToList());
        Assert.Equal(8, work.Count);
        Assert.True(watch.ElapsedMilliseconds < 500, $"took {watch.ElapsedMilliseconds} ms");
    }

    // ---------------- write ----------------

    [Fact]
    public void StoreEmbeddings_InsertsVectorAndMarksEmbedded_WithLiveScopeColumns()
    {
        var s = _db.AddSession("pi", "s1", projectKey: "acme/app", kind: "automated");
        var c = Chunk(s, "hello", 0, "2026-01-02T03:04:05Z");
        Lease(Me);

        var outcome = Store.StoreEmbeddings(new[] { Emb(c, "hello") }, Fp, Me, T0);

        Assert.Equal(new EmbedBatchOutcome(EmbedBatchKind.Stored, 1, 0), outcome);
        Assert.Equal(1, _db.Scalar($"SELECT embedded FROM tx_chunk WHERE id = {c}"));
        Assert.Equal(s, _db.Scalar($"SELECT session_id FROM tx_chunk_vec WHERE rowid = {c}"));
        Assert.Equal("acme/app", _db.Text($"SELECT project FROM tx_chunk_vec WHERE rowid = {c}"));
        Assert.Equal("automated", _db.Text($"SELECT kind FROM tx_chunk_vec WHERE rowid = {c}"));
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero).ToUnixTimeSeconds(),
            _db.Scalar($"SELECT ts FROM tx_chunk_vec WHERE rowid = {c}"));
    }

    [Theory]
    [InlineData("not a time")]
    [InlineData(null)]
    public void StoreEmbeddings_UnparsableOrMissingTs_GivesZero(string? ts)
    {
        var s = _db.AddSession("pi", "s1");
        var c = Chunk(s, "hello", 0, ts);
        Lease(Me);

        Store.StoreEmbeddings(new[] { Emb(c, "hello") }, Fp, Me, T0);

        Assert.Equal(0, _db.Scalar($"SELECT ts FROM tx_chunk_vec WHERE rowid = {c}"));
        Assert.Equal(1, _db.Scalar($"SELECT embedded FROM tx_chunk WHERE id = {c}"));
    }

    [Fact]
    public void StoreEmbeddings_ContentChangedSinceRead_SkipsChunk()
    {
        var s = _db.AddSession("pi", "s1");
        var c = Chunk(s, "old text");
        var other = Chunk(s, "other", 1);
        Lease(Me);
        _db.Exec($"UPDATE tx_chunk SET content = 'new text' WHERE id = {c}");

        var outcome = Store.StoreEmbeddings(new[] { Emb(c, "old text"), Emb(other, "other", 2) }, Fp, Me, T0);

        Assert.Equal(new EmbedBatchOutcome(EmbedBatchKind.Stored, 1, 1), outcome);
        Assert.Equal(0, _db.Scalar($"SELECT embedded FROM tx_chunk WHERE id = {c}"));
        Assert.Equal(0, _db.Scalar($"SELECT COUNT(*) FROM tx_chunk_vec WHERE rowid = {c}"));
        Assert.Equal(1, _db.Scalar($"SELECT embedded FROM tx_chunk WHERE id = {other}"));
    }

    [Fact]
    public void StoreEmbeddings_KindFlippedAfterRead_VectorCarriesNewKind()
    {
        var s = _db.AddSession("pi", "s1", kind: "interactive");
        var c = Chunk(s, "hello");
        Lease(Me);
        _db.Exec($"UPDATE tx_session SET kind = 'automated', project_key = 'moved/there' WHERE id = {s}");

        Store.StoreEmbeddings(new[] { Emb(c, "hello") }, Fp, Me, T0);

        Assert.Equal("automated", _db.Text($"SELECT kind FROM tx_chunk_vec WHERE rowid = {c}"));
        Assert.Equal("moved/there", _db.Text($"SELECT project FROM tx_chunk_vec WHERE rowid = {c}"));
    }

    [Fact]
    public void StoreEmbeddings_ChunkDeletedAfterRead_Skipped_NoOrphanVector()
    {
        var s = _db.AddSession("pi", "s1");
        var c = Chunk(s, "hello");
        Lease(Me);
        _db.Exec($"DELETE FROM tx_chunk WHERE id = {c}");

        var outcome = Store.StoreEmbeddings(new[] { Emb(c, "hello") }, Fp, Me, T0);

        Assert.Equal(new EmbedBatchOutcome(EmbedBatchKind.Stored, 0, 1), outcome);
        Assert.Equal(0, _db.Scalar("SELECT COUNT(*) FROM tx_chunk_vec"));
    }

    [Fact]
    public void StoreEmbeddings_WrongLengthVector_Throws_StoresNothing()
    {
        var s = _db.AddSession("pi", "s1");
        var a = Chunk(s, "a", 0);
        var b = Chunk(s, "b", 1);
        Lease(Me);

        Assert.Throws<ArgumentException>(() => Store.StoreEmbeddings(
            new[] { Emb(a, "a"), new ChunkEmbedding(b, "b", Vec(2, 383)) }, Fp, Me, T0));

        Assert.Equal(0, _db.Scalar("SELECT COUNT(*) FROM tx_chunk_vec"));
        Assert.Equal(0, _db.Scalar("SELECT COUNT(*) FROM tx_chunk WHERE embedded = 1"));
        Assert.Null(Store.ReadEmbedFingerprint());
    }

    [Fact]
    public void StoreEmbeddings_StampsFingerprint_WhenNoneStored()
    {
        var s = _db.AddSession("pi", "s1");
        var c = Chunk(s, "hello");
        Lease(Me);

        Store.StoreEmbeddings(new[] { Emb(c, "hello") }, Fp, Me, T0);

        Assert.Equal(Fp, Store.ReadEmbedFingerprint());
        Assert.Equal("local", _db.Text("SELECT value FROM tx_state WHERE key = 'embed.provider'"));
        Assert.Equal("384", _db.Text("SELECT value FROM tx_state WHERE key = 'embed.dimensions'"));
    }

    [Fact]
    public void StoreEmbeddings_StampsAgain_WhenStoredButNoVectorRowBacksIt()
    {
        var s = _db.AddSession("pi", "s1");
        var c = Chunk(s, "hello");
        Lease(Me);
        var old = new ArchiveFingerprint("openai", "other-model", "x", 384);
        _db.Exec("INSERT INTO tx_state (key, value) VALUES ('embed.provider', 'openai'), ('embed.model', 'other-model'), " +
                 "('embed.revision', 'x'), ('embed.dimensions', '384')");
        Assert.Equal(old, Store.ReadEmbedFingerprint());

        var outcome = Store.StoreEmbeddings(new[] { Emb(c, "hello") }, Fp, Me, T0);

        Assert.Equal(EmbedBatchKind.Stored, outcome.Kind);
        Assert.Equal(Fp, Store.ReadEmbedFingerprint());
    }

    [Fact]
    public void StoreEmbeddings_MismatchWithVectorsPresent_WritesNothing_ReturnsFingerprintMismatch()
    {
        var s = _db.AddSession("pi", "s1");
        var a = Chunk(s, "a", 0);
        var b = Chunk(s, "b", 1);
        Lease(Me);
        Store.StoreEmbeddings(new[] { Emb(a, "a") }, Fp, Me, T0);
        var other = Fp with { Revision = "rev2" };

        var outcome = Store.StoreEmbeddings(new[] { Emb(b, "b", 2) }, other, Me, T0);

        Assert.Equal(new EmbedBatchOutcome(EmbedBatchKind.FingerprintMismatch, 0, 0), outcome);
        Assert.Equal(0, _db.Scalar($"SELECT embedded FROM tx_chunk WHERE id = {b}"));
        Assert.Equal(1, _db.Scalar("SELECT COUNT(*) FROM tx_chunk_vec"));
        Assert.Equal(Fp, Store.ReadEmbedFingerprint());
    }

    [Fact]
    public void StoreEmbeddings_LeaseNotHeld_ReturnsLeaseLost()
    {
        var s = _db.AddSession("pi", "s1");
        var c = Chunk(s, "hello");

        // no lease at all
        var none = Store.StoreEmbeddings(new[] { Emb(c, "hello") }, Fp, Me, T0);
        Assert.Equal(new EmbedBatchOutcome(EmbedBatchKind.LeaseLost, 0, 0), none);

        // another run's lease
        Lease(new EmbedLeaseHolder("host-b", 5, "run-2"));
        var taken = Store.StoreEmbeddings(new[] { Emb(c, "hello") }, Fp, Me, T0);
        Assert.Equal(EmbedBatchKind.LeaseLost, taken.Kind);

        Assert.Equal(0, _db.Scalar($"SELECT embedded FROM tx_chunk WHERE id = {c}"));
        Assert.Equal(0, _db.Scalar("SELECT COUNT(*) FROM tx_chunk_vec"));
        Assert.Null(Store.ReadEmbedFingerprint());
    }

    [Fact]
    public void StoreEmbeddings_RenewsLeaseTime()
    {
        var s = _db.AddSession("pi", "s1");
        var c = Chunk(s, "hello");
        Lease(Me);
        var later = T0.AddMinutes(7);

        Store.StoreEmbeddings(new[] { Emb(c, "hello") }, Fp, Me, later);

        Assert.Equal($"host-a:100:run-1:{later.ToUnixTimeMilliseconds()}",
            _db.Text("SELECT value FROM tx_state WHERE key = 'embed.lease'"));
    }

    // ---------------- failure count ----------------

    [Fact]
    public void RecordEmbedFailure_IncrementsOnlyWhenContentMatches()
    {
        var s = _db.AddSession("pi", "s1");
        var c = Chunk(s, "text");

        Assert.False(Store.RecordEmbedFailure(c, "other text"));
        Assert.Equal(0, _db.Scalar($"SELECT embed_attempts FROM tx_chunk WHERE id = {c}"));

        Assert.True(Store.RecordEmbedFailure(c, "text"));
        Assert.True(Store.RecordEmbedFailure(c, "text"));
        Assert.Equal(2, _db.Scalar($"SELECT embed_attempts FROM tx_chunk WHERE id = {c}"));
        Assert.False(Store.RecordEmbedFailure(c + 100, "text"));
    }

    [Fact]
    public void ResetEmbedAttempts_ZeroesAll_ReturnsCount()
    {
        var s = _db.AddSession("pi", "s1");
        var a = Chunk(s, "a", 0);
        var b = Chunk(s, "b", 1);
        Chunk(s, "c", 2);
        _db.Exec($"UPDATE tx_chunk SET embed_attempts = 3 WHERE id = {a}");
        _db.Exec($"UPDATE tx_chunk SET embed_attempts = 1 WHERE id = {b}");

        Assert.Equal(2, Store.ResetEmbedAttempts());

        Assert.Equal(0, _db.Scalar("SELECT COUNT(*) FROM tx_chunk WHERE embed_attempts <> 0"));
        Assert.Equal(0, Store.ResetEmbedAttempts());
    }

    [Fact]
    public void ChunkWithThreeAttempts_CountsAsGaveUp_AndContentChangeResetsIt()
    {
        var s = _db.AddSession("pi", "s1");
        var c = Chunk(s, "text");
        _db.Exec($"UPDATE tx_chunk SET embed_attempts = 3 WHERE id = {c}");

        Assert.Equal(new EmbeddingCounts(1, 0, 0, 1), Store.CountEmbedding());

        _db.Exec($"UPDATE tx_chunk SET content = 'changed' WHERE id = {c}");

        Assert.Equal(new EmbeddingCounts(1, 0, 1, 0), Store.CountEmbedding());
    }

    // ---------------- lease ----------------

    [Fact]
    public void Lease_AcquireFree_Succeeds_SecondHolderRefused()
    {
        var other = new EmbedLeaseHolder("host-b", 9, "run-2");

        Assert.True(Store.TryAcquireEmbedLease(Me, T0, (_, _) => true));
        Assert.False(Store.TryAcquireEmbedLease(other, T0.AddSeconds(5), (_, _) => true));
        Assert.Equal($"host-a:100:run-1:{T0.ToUnixTimeMilliseconds()}",
            _db.Text("SELECT value FROM tx_state WHERE key = 'embed.lease'"));
    }

    [Fact]
    public void Lease_StaleOverTenMinutes_TakenOver()
    {
        var other = new EmbedLeaseHolder("host-b", 9, "run-2");
        Assert.True(Store.TryAcquireEmbedLease(Me, T0, (_, _) => true));

        Assert.False(Store.TryAcquireEmbedLease(other, T0.AddMinutes(10), (_, _) => true));
        Assert.True(Store.TryAcquireEmbedLease(other, T0.AddMinutes(10).AddSeconds(1), (_, _) => true));

        Assert.StartsWith("host-b:9:run-2:", _db.Text("SELECT value FROM tx_state WHERE key = 'embed.lease'"));
    }

    [Fact]
    public void Lease_DeadPidSameMachine_TakenOver_LivePidOtherMachine_Not()
    {
        Assert.True(Store.TryAcquireEmbedLease(Me, T0, (_, _) => true));
        var calls = new List<(string, int)>();

        // same machine, holder dead: taken over, and the delegate saw the holder's machine and pid
        var sameMachine = new EmbedLeaseHolder("host-a", 200, "run-2");
        Assert.True(Store.TryAcquireEmbedLease(sameMachine, T0.AddSeconds(1), (m, p) => { calls.Add((m, p)); return false; }));
        Assert.Equal(new[] { ("host-a", 100) }, calls.ToArray());

        // another machine: the delegate is never called and the lease is not taken
        calls.Clear();
        var otherMachine = new EmbedLeaseHolder("host-b", 300, "run-3");
        Assert.False(Store.TryAcquireEmbedLease(otherMachine, T0.AddSeconds(2), (m, p) => { calls.Add((m, p)); return false; }));
        Assert.Empty(calls);

        // same machine, holder alive: refused
        var third = new EmbedLeaseHolder("host-a", 400, "run-4");
        Assert.False(Store.TryAcquireEmbedLease(third, T0.AddSeconds(3), (_, _) => true));
        Assert.StartsWith("host-a:200:run-2:", _db.Text("SELECT value FROM tx_state WHERE key = 'embed.lease'"));
    }

    [Fact]
    public void Lease_ReleaseOnlyIfRunIdMatches()
    {
        var victim = Me;
        var taker = new EmbedLeaseHolder("host-a", 101, "run-2");
        Assert.True(Store.TryAcquireEmbedLease(victim, T0, (_, _) => true));
        Assert.True(Store.TryAcquireEmbedLease(taker, T0.AddMinutes(11), (_, _) => true));

        Store.ReleaseEmbedLease(victim);
        Assert.StartsWith("host-a:101:run-2:", _db.Text("SELECT value FROM tx_state WHERE key = 'embed.lease'"));

        Store.ReleaseEmbedLease(taker);
        Assert.Equal(0, _db.Scalar("SELECT COUNT(*) FROM tx_state WHERE key = 'embed.lease'"));
    }

    [Fact]
    public void Lease_UnreadableStoredValue_TakenOver()
    {
        _db.Exec("INSERT INTO tx_state (key, value) VALUES ('embed.lease', 'garbage')");

        Assert.True(Store.TryAcquireEmbedLease(Me, T0, (_, _) => true));
    }

    // ---------------- fingerprint, counts, last run ----------------

    [Fact]
    public void CheckFingerprint_Unstamped_WhenNoneStored_AndWhenNoVectorBacksTheStoredOne()
    {
        Assert.Equal(FingerprintState.Unstamped, Store.CheckFingerprint(Fp));

        _db.Exec("INSERT INTO tx_state (key, value) VALUES ('embed.provider', 'openai'), ('embed.model', 'm'), " +
                 "('embed.revision', 'r'), ('embed.dimensions', '384')");
        Assert.Equal(FingerprintState.Unstamped, Store.CheckFingerprint(Fp));
    }

    [Fact]
    public void CheckFingerprint_Match_AndMismatchOnAnyOfTheFourFacts()
    {
        var s = _db.AddSession("pi", "s1");
        var c = Chunk(s, "hello");
        Lease(Me);
        Store.StoreEmbeddings(new[] { Emb(c, "hello") }, Fp, Me, T0);

        Assert.Equal(FingerprintState.Match, Store.CheckFingerprint(Fp));
        Assert.Equal(FingerprintState.Mismatch, Store.CheckFingerprint(Fp with { Provider = "openai" }));
        Assert.Equal(FingerprintState.Mismatch, Store.CheckFingerprint(Fp with { Model = "x" }));
        Assert.Equal(FingerprintState.Mismatch, Store.CheckFingerprint(Fp with { Revision = "x" }));
        Assert.Equal(FingerprintState.Mismatch, Store.CheckFingerprint(Fp with { Dimensions = 768 }));
    }

    [Fact]
    public void CountEmbedding_Totals()
    {
        Assert.Equal(new EmbeddingCounts(0, 0, 0, 0), Store.CountEmbedding());

        var s = _db.AddSession("pi", "s1");
        var a = Chunk(s, "a", 0);
        Chunk(s, "b", 1);
        var c = Chunk(s, "c", 2);
        var d = Chunk(s, "d", 3);
        Lease(Me);
        Store.StoreEmbeddings(new[] { Emb(a, "a") }, Fp, Me, T0);
        _db.Exec($"UPDATE tx_chunk SET embed_attempts = 2 WHERE id = {c}");
        _db.Exec($"UPDATE tx_chunk SET embed_attempts = 3 WHERE id = {d}");

        Assert.Equal(new EmbeddingCounts(4, 1, 2, 1), Store.CountEmbedding());
    }

    [Fact]
    public void WriteEmbedRun_ReadEmbedRun_RoundTrip()
    {
        Assert.Null(Store.ReadEmbedRun());

        var run = new EmbedRunRecord(T0, 12, 30, 2, "words only: \"quoted\" é", "WordsOnly", "local", "bge-small-en-v1.5", "budget");
        Store.WriteEmbedRun(run);
        Assert.Equal(run, Store.ReadEmbedRun());

        var next = run with { Embedded = 0, StoppedBy = "done" };
        Store.WriteEmbedRun(next);
        Assert.Equal(next, Store.ReadEmbedRun());
        Assert.Equal(1, _db.Scalar("SELECT COUNT(*) FROM tx_state WHERE key = 'embed.last_run'"));
    }

    [Fact]
    public void ReadEmbedRun_UnreadableRecord_IsNull()
    {
        _db.Exec("INSERT INTO tx_state (key, value) VALUES ('embed.last_run', '{not json')");

        Assert.Null(Store.ReadEmbedRun());
    }
}
