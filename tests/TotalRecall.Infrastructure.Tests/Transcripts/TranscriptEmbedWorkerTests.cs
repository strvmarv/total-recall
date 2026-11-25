using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Data.Sqlite;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Tests.TestSupport;
using TotalRecall.Infrastructure.Transcripts;
using TotalRecall.TestSupport.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

/// <summary>
/// The archive embed worker (search the archive, spec section 3, items 1 to 7): lease, probe, batches, failure
/// classes, budgets and the read-to-write races. A file-backed archive and a scripted embedder; no model. Not
/// Integration-trait.
/// </summary>
public sealed class TranscriptEmbedWorkerTests : IDisposable
{
    internal static readonly ArchiveFingerprint Fp = new("local", "bge-small-en-v1.5", "rev1", 384);
    private static readonly ArchiveFingerprint OpenAiFp = new("openai", "text-embedding-3-small", "", 384);
    private static readonly EmbedderDescriptor OpenAiDescriptor = new("openai", "text-embedding-3-small", "", 384);
    internal static readonly DateTimeOffset T0 = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    internal static readonly EmbedGateResult Ready = new(EmbedState.Ready, "ready");
    private static readonly EmbedBudget NoBudget = new(null, null);

    private readonly TranscriptTestDb _db = new();

    public void Dispose() => _db.Dispose();

    // ---------------- helpers ----------------

    private static string Text(int i) => $"chunk {i:000} text";

    /// <summary>One session with <paramref name="n"/> chunks whose contents are <see cref="Text"/>.</summary>
    private List<long> Seed(int n, string kind = "interactive", string project = "owner/repo")
    {
        var s = _db.AddSession("pi", "s-" + Guid.NewGuid().ToString("N"), projectKey: project, kind: kind);
        var ids = new List<long>();
        for (var i = 0; i < n; i++)
        {
            var id = _db.AddChunk(s, Text(i), i);
            _db.Exec($"UPDATE tx_chunk SET ts = '2026-01-02T03:04:05Z' WHERE id = {id}");
            ids.Add(id);
        }
        return ids;
    }

    private TranscriptEmbedWorker Worker(
        IEmbedder embedder,
        ITranscriptStore? store = null,
        TimeProvider? time = null,
        Action? seam = null) =>
        new(store ?? _db.Store, embedder, time ?? new ManualTimeProvider(T0), "host-a", 100, (_, _) => true, seam);

    private long Attempts(long id) => _db.Scalar($"SELECT embed_attempts FROM tx_chunk WHERE id = {id}");
    private long Embedded(long id) => _db.Scalar($"SELECT embedded FROM tx_chunk WHERE id = {id}");
    private long VecRows() => _db.Scalar("SELECT COUNT(*) FROM tx_chunk_vec");
    private string? LeaseValue() => _db.Text("SELECT value FROM tx_state WHERE key = 'embed.lease'");
    private long AttemptSum() => _db.Scalar("SELECT COALESCE(SUM(embed_attempts), 0) FROM tx_chunk");

    private static List<string> ChunkCalls(ScriptedEmbedder e) => e.Calls.Where(c => c != EmbedProbe.Text).ToList();
    private static int ProbeCalls(ScriptedEmbedder e) => e.Calls.Count(c => c == EmbedProbe.Text);

    // ---------------- happy path ----------------

    [Fact]
    public void Run_EmbedsAllInBatchesOf16_StampsFingerprint_WritesLastRun()
    {
        var ids = Seed(40);
        var e = new ScriptedEmbedder();
        var store = new DecoratingTranscriptStore(_db.Store);
        var batchSizes = new List<int>();
        store.OnStoreEmbeddings = (b, c, h, n) => { batchSizes.Add(b.Count); return _db.Store.StoreEmbeddings(b, c, h, n); };

        var run = Worker(e, store).Run(Ready, Fp, NoBudget, CancellationToken.None);

        Assert.Equal(new EmbedRun(40, 0, 0, Ready, EmbedStopReason.Done), run);
        Assert.Equal(new[] { 16, 16, 8 }, batchSizes.ToArray());
        Assert.All(ids, id => Assert.Equal(1, Embedded(id)));
        Assert.Equal(40, VecRows());
        Assert.Equal(Fp, _db.Store.ReadEmbedFingerprint());
        Assert.Null(LeaseValue()); // released in finally

        // the probe goes first, once; then every chunk text once, in id order
        Assert.Equal(EmbedProbe.Text, e.Calls[0]);
        Assert.Equal(1, ProbeCalls(e));
        Assert.Equal(Enumerable.Range(0, 40).Select(Text).ToList(), ChunkCalls(e));

        var last = _db.Store.ReadEmbedRun();
        Assert.NotNull(last);
        Assert.Equal(new EmbedRunRecord(T0, 40, 0, 0, "ready", "Ready", "local", "bge-small-en-v1.5", "done"), last);
    }

    [Fact]
    public void Run_GateNotReady_DoesNothing_StoppedByGate()
    {
        Seed(5);
        var e = new ScriptedEmbedder();
        var gate = EmbedGate.NotReady("model.onnx missing");

        var run = Worker(e).Run(gate, Fp, NoBudget, CancellationToken.None);

        Assert.Equal(EmbedStopReason.Gate, run.StoppedBy);
        Assert.Equal(gate, run.State);
        Assert.Equal(0, run.Embedded);
        Assert.Equal(5, run.Remaining);
        Assert.Empty(e.Calls);
        Assert.Null(LeaseValue());
        Assert.Null(_db.Store.ReadEmbedRun());
        Assert.Equal(0, VecRows());
    }

    [Fact]
    public void Run_NoWork_SendsNoProbe_Done()
    {
        var e = new ScriptedEmbedder();

        var run = Worker(e).Run(Ready, Fp, NoBudget, CancellationToken.None);

        Assert.Equal(new EmbedRun(0, 0, 0, Ready, EmbedStopReason.Done), run);
        Assert.Empty(e.Calls);
        Assert.Null(LeaseValue());
        Assert.Equal("done", _db.Store.ReadEmbedRun()!.StoppedBy);
    }

    [Fact]
    public void Run_PreferSession_EmbedsCurrentFirst_WithinEightChunks()
    {
        var older = Seed(50);
        var cur = _db.AddSession("pi", "cur");
        var curIds = new List<long>();
        for (var i = 0; i < 5; i++)
            curIds.Add(_db.AddChunk(cur, $"current text {i}", i));
        var embedder = new RecordingEmbedder("local", 384);

        var run = Worker(embedder).Run(
            Ready, Fp, new EmbedBudget(null, 8), CancellationToken.None, preferSession: ("pi", "cur"));

        Assert.Equal(EmbedStopReason.Budget, run.StoppedBy);
        Assert.Equal(8, run.Embedded);
        Assert.All(curIds, id => Assert.Equal(1, Embedded(id)));
        Assert.Equal(new long[] { 1, 1, 1 }, older.Take(3).Select(Embedded).ToArray());
        Assert.All(older.Skip(3), id => Assert.Equal(0, Embedded(id)));
        // the probe is the only call that is not a chunk; the 5 current chunks were embedded in this one run
        var chunkCalls = embedder.Calls.Where(c => c != EmbedProbe.Text).ToList();
        Assert.Equal(8, chunkCalls.Count);
        Assert.Equal(Enumerable.Range(0, 5).Select(i => $"current text {i}"), chunkCalls.Take(5));
    }

    [Fact]
    public void Run_PreferSession_PassesThePreferenceOnEveryBatchRead()
    {
        Seed(40);
        var store = new DecoratingTranscriptStore(_db.Store);
        var seen = new List<(string, string)?>();
        store.OnNextEmbedWork = (limit, exclude, prefer) => { seen.Add(prefer); return _db.Store.NextEmbedWork(limit, exclude, prefer); };

        Worker(new ScriptedEmbedder(), store).Run(
            Ready, Fp, NoBudget, CancellationToken.None, preferSession: ("pi", "cur"));

        Assert.True(seen.Count >= 3);
        Assert.All(seen, p => Assert.Equal(("pi", "cur"), p));
    }

    [Fact]
    public void Run_NoPreference_PassesNullOnEveryBatchRead()
    {
        Seed(20);
        var store = new DecoratingTranscriptStore(_db.Store);
        var seen = new List<(string, string)?>();
        store.OnNextEmbedWork = (limit, exclude, prefer) => { seen.Add(prefer); return _db.Store.NextEmbedWork(limit, exclude, prefer); };

        Worker(new ScriptedEmbedder(), store).Run(Ready, Fp, NoBudget, CancellationToken.None);

        Assert.NotEmpty(seen);
        Assert.All(seen, p => Assert.Null(p));
    }

    [Fact]
    public void Run_RecordRunFalse_LeavesLastRunUntouched()
    {
        Seed(20);
        var before = new EmbedRunRecord(T0.AddDays(-3), 7, 99, 1, "ready", "Ready", "local", "bge-small-en-v1.5", "done");
        _db.Store.WriteEmbedRun(before);
        var later = new ManualTimeProvider(T0);

        var quiet = Worker(new ScriptedEmbedder(), time: later).Run(
            Ready, Fp, new EmbedBudget(null, 16), CancellationToken.None, recordRun: false);

        Assert.Equal(16, quiet.Embedded);
        Assert.Equal(before, _db.Store.ReadEmbedRun());
        Assert.Null(LeaseValue()); // the lease is still released

        var loud = Worker(new ScriptedEmbedder(), time: later).Run(
            Ready, Fp, NoBudget, CancellationToken.None, recordRun: true);

        Assert.Equal(4, loud.Embedded);
        var after = _db.Store.ReadEmbedRun();
        Assert.NotNull(after);
        Assert.NotEqual(before, after);
        Assert.Equal(T0, after!.At);
        Assert.Equal(4, after.Embedded);
    }

    [Fact]
    public void Run_BudgetChunks_StopsBetweenBatches_ThenResumesWithNoDuplicates()
    {
        var ids = Seed(40);
        var e = new ScriptedEmbedder();

        var first = Worker(e).Run(Ready, Fp, new EmbedBudget(null, 16), CancellationToken.None);

        Assert.Equal(new EmbedRun(16, 24, 0, Ready, EmbedStopReason.Budget), first);
        Assert.Equal(16, VecRows());
        Assert.Null(LeaseValue());
        Assert.Equal("budget", _db.Store.ReadEmbedRun()!.StoppedBy);

        // a budget that is not a multiple of the batch size reads only what is left of it
        var second = Worker(e).Run(Ready, Fp, new EmbedBudget(null, 20), CancellationToken.None);
        Assert.Equal(new EmbedRun(20, 4, 0, Ready, EmbedStopReason.Budget), second);

        var third = Worker(e).Run(Ready, Fp, NoBudget, CancellationToken.None);
        Assert.Equal(new EmbedRun(4, 0, 0, Ready, EmbedStopReason.Done), third);

        Assert.All(ids, id => Assert.Equal(1, Embedded(id)));
        var chunkCalls = ChunkCalls(e);
        Assert.Equal(40, chunkCalls.Count);
        Assert.Equal(40, chunkCalls.Distinct().Count());
    }

    [Fact]
    public void Run_BudgetTime_UsesTheInjectedClock()
    {
        Seed(40);
        var clock = new ManualTimeProvider(T0);
        var e = new ScriptedEmbedder { OnCall = _ => clock.Advance(TimeSpan.FromSeconds(1)) };

        // probe (1 s) + one batch (16 s) passes the 10 s budget; it is checked between batches only
        var run = Worker(e, time: clock).Run(Ready, Fp, new EmbedBudget(TimeSpan.FromSeconds(10), null), CancellationToken.None);

        Assert.Equal(new EmbedRun(16, 24, 0, Ready, EmbedStopReason.Budget), run);

        // the wall clock does not count: with the clock standing still the same budget embeds everything
        var still = new ScriptedEmbedder();
        var rest = Worker(still, time: new ManualTimeProvider(T0)).Run(
            Ready, Fp, new EmbedBudget(TimeSpan.FromSeconds(10), null), CancellationToken.None);
        Assert.Equal(new EmbedRun(24, 0, 0, Ready, EmbedStopReason.Done), rest);
    }

    [Fact]
    public void Run_Cancelled_StopsBetweenBatches()
    {
        Seed(40);
        var e = new ScriptedEmbedder();
        using var cts = new CancellationTokenSource();
        var progress = new List<EmbedProgress>();

        var run = Worker(e).Run(Ready, Fp, NoBudget, cts.Token, p => { progress.Add(p); cts.Cancel(); });

        Assert.Equal(new EmbedRun(16, 24, 0, Ready, EmbedStopReason.Cancelled), run);
        Assert.Equal(new[] { new EmbedProgress(16, 24) }, progress.ToArray());
        Assert.Equal(16, VecRows());
        Assert.Null(LeaseValue());
        Assert.Equal("cancelled", _db.Store.ReadEmbedRun()!.StoppedBy);
    }

    [Fact]
    public void Run_Progress_ReportedAfterEveryBatch()
    {
        Seed(40);
        var progress = new List<EmbedProgress>();

        Worker(new ScriptedEmbedder()).Run(Ready, Fp, NoBudget, CancellationToken.None, progress.Add);

        Assert.Equal(new[] { new EmbedProgress(16, 24), new EmbedProgress(32, 8), new EmbedProgress(40, 0) }, progress.ToArray());
    }

    [Fact]
    public void Run_Progress_CountsTheWaitingChunksOnce_NotAfterEveryBatch()
    {
        Seed(40);
        var store = new DecoratingTranscriptStore(_db.Store);
        var progress = new List<EmbedProgress>();

        Worker(new ScriptedEmbedder(), store).Run(Ready, Fp, NoBudget, CancellationToken.None, progress.Add);

        Assert.Equal(3, progress.Count);
        // once before the first batch, once for the run's Remaining in finally; never a full count per batch
        Assert.Equal(2, store.CountEmbeddingCalls);
    }

    [Theory]
    [InlineData(0)] // fails alone once: still waiting
    [InlineData(2)] // reaches its third try: leaves the work list
    public void Run_Progress_RemainingMatchesTheStore_WithALoneFailure(int attemptsBefore)
    {
        var ids = Seed(20);
        _db.Exec($"UPDATE tx_chunk SET embed_attempts = {attemptsBefore} WHERE id = {ids[0]}");
        var e = new ScriptedEmbedder().Throw(Text(0), new KeyNotFoundException("bad"));
        var progress = new List<EmbedProgress>();
        var actual = new List<long>();

        Worker(e).Run(Ready, Fp, NoBudget, CancellationToken.None, p =>
        {
            progress.Add(p);
            actual.Add(_db.Store.CountEmbedding().Waiting);
        });

        var stillWaiting = attemptsBefore + 1 < 3 ? 1 : 0;
        Assert.Equal(new[] { new EmbedProgress(15, 4 + stillWaiting), new EmbedProgress(19, stillWaiting) }, progress.ToArray());
        Assert.Equal(actual, progress.Select(p => (long)p.Remaining).ToList());
    }

    // ---------------- lease ----------------

    [Fact]
    public void Run_LeaseHeldByAnother_StoppedByLease_EmbedderNeverCalled()
    {
        Seed(5);
        Assert.True(_db.Store.TryAcquireEmbedLease(new EmbedLeaseHolder("host-b", 5, "other-run"), T0, (_, _) => true));
        var e = new ScriptedEmbedder();

        var run = Worker(e).Run(Ready, Fp, NoBudget, CancellationToken.None);

        Assert.Equal(EmbedStopReason.Lease, run.StoppedBy);
        Assert.Equal(EmbedGate.AnotherRun(), run.State);
        Assert.Equal(0, run.Embedded);
        Assert.Equal(5, run.Remaining);
        Assert.Empty(e.Calls);
        Assert.StartsWith("host-b:5:other-run:", LeaseValue());
        Assert.Null(_db.Store.ReadEmbedRun()); // the holder's run owns the record
    }

    [Fact]
    public void Run_LeaseTakesItsTime_AndHoldsThisMachineAndPid()
    {
        Seed(1);
        string? seen = null;
        var worker = Worker(new ScriptedEmbedder(), seam: () => seen = LeaseValue());

        worker.Run(Ready, Fp, NoBudget, CancellationToken.None);

        Assert.NotNull(seen);
        Assert.StartsWith("host-a:100:", seen);
        Assert.EndsWith(":" + T0.ToUnixTimeMilliseconds(), seen);
    }

    [Fact]
    public void Run_EachRunHasItsOwnRunId()
    {
        Seed(2);
        var values = new List<string?>();
        var a = Worker(new ScriptedEmbedder(), seam: () => values.Add(LeaseValue()));
        a.Run(Ready, Fp, new EmbedBudget(null, 1), CancellationToken.None);
        a.Run(Ready, Fp, new EmbedBudget(null, 1), CancellationToken.None);

        Assert.Equal(2, values.Count);
        Assert.NotEqual(values[0], values[1]);
    }

    [Fact]
    public void Run_LostLeaseMidRun_StopsAtNextBatch_DoesNotDeleteNewHoldersLease()
    {
        var ids = Seed(40);
        var e = new ScriptedEmbedder();
        var seamCalls = 0;
        var taker = new EmbedLeaseHolder("host-b", 5, "taker");
        var worker = Worker(e, seam: () =>
        {
            // before the second write, another machine takes the lease over (this worker slept past ten minutes)
            if (++seamCalls == 2)
                Assert.True(_db.Store.TryAcquireEmbedLease(taker, T0.AddMinutes(11), (_, _) => true));
        });

        var run = worker.Run(Ready, Fp, NoBudget, CancellationToken.None);

        Assert.Equal(EmbedStopReason.Lease, run.StoppedBy);
        Assert.Equal(EmbedState.AnotherRunActive, run.State.State);
        Assert.Equal(16, run.Embedded);
        Assert.Equal(24, run.Remaining);
        Assert.Equal(16, VecRows());
        Assert.Equal(0, Embedded(ids[16]));
        Assert.Equal(2, seamCalls); // no third batch
        Assert.StartsWith("host-b:5:taker:", LeaseValue()); // not deleted in finally
        Assert.Equal(0, AttemptSum());
    }

    [Fact]
    public void Run_LostLeaseMidRun_DoesNotOverwriteNewHoldersLastRun()
    {
        Seed(40);
        var seamCalls = 0;
        var taker = new EmbedLeaseHolder("host-b", 5, "taker");
        var takersRecord = new EmbedRunRecord(T0.AddMinutes(12), 7, 3, 0, "ready", "Ready", "local", "bge-small-en-v1.5", "budget");
        var worker = Worker(new ScriptedEmbedder(), seam: () =>
        {
            if (++seamCalls != 2) return;
            // the new holder takes the lease over and finishes a run of its own before this worker writes again
            Assert.True(_db.Store.TryAcquireEmbedLease(taker, T0.AddMinutes(11), (_, _) => true));
            _db.Store.WriteEmbedRun(takersRecord);
        });

        var run = worker.Run(Ready, Fp, NoBudget, CancellationToken.None);

        Assert.Equal(EmbedStopReason.Lease, run.StoppedBy);
        Assert.Equal(takersRecord, _db.Store.ReadEmbedRun()); // the run that lost the lease writes no record
    }

    [Fact]
    public void Run_ExceptionInLoop_ReleasesLeaseInFinally_EndsFailed()
    {
        Seed(20);
        var store = new DecoratingTranscriptStore(_db.Store);
        var reads = 0;
        store.OnNextEmbedWork = (limit, exclude, prefer) =>
            ++reads == 2 ? throw new InvalidOperationException("read broke") : _db.Store.NextEmbedWork(limit, exclude, prefer);

        var run = Worker(new ScriptedEmbedder(), store).Run(Ready, Fp, NoBudget, CancellationToken.None);

        Assert.Equal(EmbedStopReason.Failed, run.StoppedBy);
        Assert.Equal(EmbedGate.Failed("read broke"), run.State);
        Assert.Equal(16, run.Embedded);
        Assert.Null(LeaseValue());
        Assert.Equal(1, store.ReleaseCalls);
        var last = _db.Store.ReadEmbedRun()!;
        Assert.Equal("failed", last.StoppedBy);
        Assert.Equal("embedding failed: read broke", last.State);
        Assert.Equal("EmbeddingFailed", last.StateKind);
    }

    // ---------------- probe and dimension ----------------

    [Fact]
    public void Run_FirstBatchProbe_WrongDimension_EndsInDimensionRefusal_NoChunkTextSent()
    {
        var ids = Seed(5);
        var e = new ScriptedEmbedder(OpenAiDescriptor).On(EmbedProbe.Text, () => new float[768]);

        var run = Worker(e).Run(Ready, OpenAiFp, NoBudget, CancellationToken.None);

        Assert.Equal(EmbedStopReason.Failed, run.StoppedBy);
        Assert.Equal(EmbedGate.Dimension(768), run.State);
        Assert.Equal(new[] { EmbedProbe.Text }, e.Calls.ToArray());
        Assert.Equal(0, AttemptSum());
        Assert.Equal(0, VecRows());
        Assert.Null(_db.Store.ReadEmbedFingerprint());
        var last = _db.Store.ReadEmbedRun()!;
        Assert.Equal("words only: the provider's vectors have 768 dimensions; the archive holds 384", last.State);
        Assert.Equal("DimensionRefused", last.StateKind);
        Assert.Equal("openai", last.Provider);
        Assert.Null(LeaseValue());
        Assert.All(ids, id => Assert.Equal(0, Embedded(id)));
    }

    [Fact]
    public void Run_ChunkVectorWrongDimension_EndsInDimensionRefusal_NothingStoredOrCounted()
    {
        Seed(5);
        var e = new ScriptedEmbedder().On(Text(2), () => new float[383]);

        var run = Worker(e).Run(Ready, Fp, NoBudget, CancellationToken.None);

        Assert.Equal(EmbedStopReason.Failed, run.StoppedBy);
        Assert.Equal(EmbedGate.Dimension(383), run.State);
        Assert.Equal(0, run.Embedded);
        Assert.Equal(0, VecRows());
        Assert.Equal(0, AttemptSum());
    }

    [Fact]
    public void Run_ProbeThrowsSystemic_EndsFailed_NoChunkTextSent()
    {
        Seed(3);
        var e = new ScriptedEmbedder(OpenAiDescriptor)
            .Throw(EmbedProbe.Text, new HttpRequestException("down", null, HttpStatusCode.ServiceUnavailable));

        var run = Worker(e).Run(Ready, OpenAiFp, NoBudget, CancellationToken.None);

        Assert.Equal(EmbedStopReason.Failed, run.StoppedBy);
        Assert.Equal(EmbedGate.Failed("down"), run.State);
        Assert.Equal(new[] { EmbedProbe.Text }, e.Calls.ToArray());
        Assert.Equal(0, AttemptSum());
    }

    [Fact]
    public void Run_ProbeThrowsLocalInvalidOperation_EndsNotReady_StoppedByGate()
    {
        Seed(3);
        var e = new ScriptedEmbedder().Throw(EmbedProbe.Text, new InvalidOperationException("model missing"));

        var run = Worker(e).Run(Ready, Fp, NoBudget, CancellationToken.None);

        Assert.Equal(EmbedStopReason.Gate, run.StoppedBy);
        Assert.Equal(EmbedGate.NotReady("model missing"), run.State);
        Assert.Equal(new[] { EmbedProbe.Text }, e.Calls.ToArray());
        Assert.Equal(0, AttemptSum());
    }

    [Fact]
    public void Run_ChunkThrowsLocalInvalidOperation_EndsNotReady_NothingCounted()
    {
        Seed(5);
        var e = new ScriptedEmbedder().Throw(Text(1), new InvalidOperationException("model missing"));

        var run = Worker(e).Run(Ready, Fp, NoBudget, CancellationToken.None);

        Assert.Equal(EmbedStopReason.Gate, run.StoppedBy);
        Assert.Equal(EmbedGate.NotReady("model missing"), run.State);
        Assert.Equal(0, run.Embedded);
        Assert.Equal(0, AttemptSum());
        Assert.Equal(0, VecRows());
        Assert.Equal(1, ChunkCalls(e).Count(c => c == Text(1))); // no single-chunk retry
    }

    // ---------------- failures ----------------

    [Fact]
    public void Run_BadChunkAtFront_CountedFromFirstFailure_RestEmbedded()
    {
        var ids = Seed(20);
        var e = new ScriptedEmbedder().Throw(Text(0), new KeyNotFoundException("bad"));

        var run = Worker(e).Run(Ready, Fp, NoBudget, CancellationToken.None);

        Assert.Equal(new EmbedRun(19, 1, 0, Ready, EmbedStopReason.Done, FailedAlone: 1), run);
        Assert.Equal(1, Attempts(ids[0]));
        Assert.Equal(0, Embedded(ids[0]));
        Assert.All(ids.Skip(1), id => Assert.Equal(1, Embedded(id)));
        Assert.Equal(2, ChunkCalls(e).Count(c => c == Text(0))); // the batch, then alone
        Assert.Equal(2, ProbeCalls(e));                           // the run's probe, then the health test
    }

    [Fact]
    public void Run_ThreeBadChunksAtLowestIds_AllCounted_RestEmbedded()
    {
        var ids = Seed(20);
        var e = new ScriptedEmbedder()
            .Throw(Text(0), new KeyNotFoundException("bad"))
            .Throw(Text(1), new KeyNotFoundException("bad"))
            .Throw(Text(2), new KeyNotFoundException("bad"));

        var run = Worker(e).Run(Ready, Fp, NoBudget, CancellationToken.None);

        Assert.Equal(new EmbedRun(17, 3, 0, Ready, EmbedStopReason.Done, FailedAlone: 3), run);
        Assert.All(ids.Take(3), id => Assert.Equal(1, Attempts(id)));
        Assert.All(ids.Skip(3), id => Assert.Equal(1, Embedded(id)));
        Assert.Equal(17, VecRows());
    }

    [Fact]
    public void Run_ProbeAlsoFails_EndsFailed_NothingCounted()
    {
        var ids = Seed(20);
        var e = new ScriptedEmbedder()
            .On(EmbedProbe.Text, () => ScriptedEmbedder.Vector(EmbedProbe.Text), () => throw new KeyNotFoundException("embedder broke"))
            .Throw(Text(0), new KeyNotFoundException("bad"));

        var run = Worker(e).Run(Ready, Fp, NoBudget, CancellationToken.None);

        Assert.Equal(EmbedStopReason.Failed, run.StoppedBy);
        Assert.Equal(EmbedState.EmbeddingFailed, run.State.State);
        Assert.StartsWith("embedding failed: ", run.State.Text);
        Assert.Equal(0, run.Embedded);
        Assert.Equal(0, AttemptSum());
        Assert.Equal(0, VecRows());
        Assert.Equal(20, run.Remaining);
    }

    [Fact]
    public void Run_FiveConsecutiveLoneFailures_EndsFailed()
    {
        var ids = Seed(20);
        var e = new ScriptedEmbedder();
        for (var i = 0; i < 6; i++)
            e.Throw(Text(i), new KeyNotFoundException("bad " + i));

        var run = Worker(e).Run(Ready, Fp, NoBudget, CancellationToken.None);

        Assert.Equal(EmbedStopReason.Failed, run.StoppedBy);
        Assert.Equal(EmbedState.EmbeddingFailed, run.State.State);
        Assert.Equal(0, run.Embedded);
        Assert.All(ids.Take(5), id => Assert.Equal(1, Attempts(id)));
        Assert.Equal(0, Attempts(ids[5]));
        Assert.DoesNotContain(Text(6), e.Calls);
        Assert.Equal(0, VecRows());
    }

    [Fact]
    public void Run_LoneFailuresBrokenBySuccess_DoNotEndTheRun()
    {
        var ids = Seed(12);
        var e = new ScriptedEmbedder();
        foreach (var i in new[] { 0, 1, 2, 3, 5, 6, 7, 8 })
            e.Throw(Text(i), new KeyNotFoundException("bad " + i));

        var run = Worker(e).Run(Ready, Fp, NoBudget, CancellationToken.None);

        Assert.Equal(EmbedStopReason.Done, run.StoppedBy);
        Assert.Equal(4, run.Embedded); // 4, 9, 10, 11
        Assert.Equal(8, AttemptSum());
    }

    [Fact]
    public void Run_FailedChunkNotReadTwiceInOneRun()
    {
        var ids = Seed(3);
        var e = new ScriptedEmbedder().Throw(Text(1), new KeyNotFoundException("bad"));
        var store = new DecoratingTranscriptStore(_db.Store);
        var excludes = new List<long[]>();
        store.OnNextEmbedWork = (limit, exclude, prefer) => { excludes.Add(exclude.ToArray()); return _db.Store.NextEmbedWork(limit, exclude, prefer); };

        var run = Worker(e, store).Run(Ready, Fp, NoBudget, CancellationToken.None);

        Assert.Equal(new EmbedRun(2, 1, 0, Ready, EmbedStopReason.Done, FailedAlone: 1), run);
        Assert.Equal(1, Attempts(ids[1]));
        Assert.Equal(2, ChunkCalls(e).Count(c => c == Text(1)));
        Assert.Equal(2, excludes.Count);
        Assert.Empty(excludes[0]);
        Assert.Equal(new[] { ids[1] }, excludes[1]);
    }

    [Fact]
    public void Run_ChunkFailsThreeRuns_LeavesWorkList_CountedGaveUp()
    {
        var ids = Seed(3);
        var e = new ScriptedEmbedder().Throw(Text(0), new KeyNotFoundException("bad"));

        var r1 = Worker(e).Run(Ready, Fp, NoBudget, CancellationToken.None);
        Assert.Equal(new EmbedRun(2, 1, 0, Ready, EmbedStopReason.Done, FailedAlone: 1), r1);
        Assert.Equal(1, Attempts(ids[0]));

        var r2 = Worker(e).Run(Ready, Fp, NoBudget, CancellationToken.None);
        Assert.Equal(new EmbedRun(0, 1, 0, Ready, EmbedStopReason.Done, FailedAlone: 1), r2);
        Assert.Equal(2, Attempts(ids[0]));

        var r3 = Worker(e).Run(Ready, Fp, NoBudget, CancellationToken.None);
        Assert.Equal(new EmbedRun(0, 0, 1, Ready, EmbedStopReason.Done, FailedAlone: 1), r3);
        Assert.Equal(3, Attempts(ids[0]));
        Assert.Equal(new EmbeddingCounts(3, 2, 0, 1), _db.Store.CountEmbedding());
        Assert.Equal(1, _db.Store.ReadEmbedRun()!.GaveUp);

        var callsBefore = e.Calls.Count;
        var r4 = Worker(e).Run(Ready, Fp, NoBudget, CancellationToken.None);
        Assert.Equal(new EmbedRun(0, 0, 0, Ready, EmbedStopReason.Done), r4);
        Assert.Equal(callsBefore, e.Calls.Count); // left the work list: not read, no probe
    }

    [Fact]
    public void Run_SystemicFailure_HttpRequestException_EndsFailed_NothingCounted()
    {
        var ids = Seed(20);
        var e = new ScriptedEmbedder(OpenAiDescriptor)
            .Throw(Text(3), new HttpRequestException("service unavailable", null, HttpStatusCode.ServiceUnavailable));

        var run = Worker(e).Run(Ready, OpenAiFp, NoBudget, CancellationToken.None);

        Assert.Equal(EmbedStopReason.Failed, run.StoppedBy);
        Assert.Equal(EmbedGate.Failed("service unavailable"), run.State);
        Assert.Equal(0, run.Embedded);
        Assert.Equal(20, run.Remaining);
        Assert.Equal(0, AttemptSum());
        Assert.Equal(0, VecRows());
        Assert.Equal(1, ChunkCalls(e).Count(c => c == Text(3))); // no single-chunk retry
        Assert.Equal(1, ProbeCalls(e));
        Assert.Null(LeaseValue());
    }

    [Fact]
    public void Run_SqliteBusyOnWrite_EndsFailed_NothingCounted()
    {
        Seed(20);
        var e = new ScriptedEmbedder();
        var store = new DecoratingTranscriptStore(_db.Store)
        {
            OnStoreEmbeddings = (_, _, _, _) => throw new SqliteException("database is locked", 5),
        };

        var run = Worker(e, store).Run(Ready, Fp, NoBudget, CancellationToken.None);

        Assert.Equal(EmbedStopReason.Failed, run.StoppedBy);
        Assert.Equal(EmbedGate.Failed("database is locked"), run.State);
        Assert.Equal(0, run.Embedded);
        Assert.Equal(0, AttemptSum());
        Assert.Equal(0, VecRows());
        Assert.Equal(16, ChunkCalls(e).Count); // one batch, no single-chunk retry
        Assert.Null(LeaseValue());
    }

    [Theory]
    [InlineData(1)]   // SQLITE_ERROR
    [InlineData(18)]  // SQLITE_TOOBIG
    [InlineData(19)]  // SQLITE_CONSTRAINT
    [InlineData(20)]  // SQLITE_MISMATCH
    [InlineData(787)] // SQLITE_CONSTRAINT_FOREIGNKEY (extended: primary code 19)
    public void Run_InsertRejectsValue_IsChunkFailure_RetriedAlone(int code)
    {
        var ids = Seed(3);
        var store = new DecoratingTranscriptStore(_db.Store);
        store.OnStoreEmbeddings = (b, c, h, n) =>
            b.Any(x => x.Id == ids[1]) ? throw new SqliteException("value rejected", code) : _db.Store.StoreEmbeddings(b, c, h, n);
        var e = new ScriptedEmbedder();

        var run = Worker(e, store).Run(Ready, Fp, NoBudget, CancellationToken.None);

        Assert.Equal(new EmbedRun(2, 1, 0, Ready, EmbedStopReason.Done, FailedAlone: 1), run);
        Assert.Equal(1, Attempts(ids[1]));
        Assert.Equal(0, Embedded(ids[1]));
    }

    [Theory]
    [InlineData(7)]   // SQLITE_NOMEM
    [InlineData(8)]   // SQLITE_READONLY
    [InlineData(10)]  // SQLITE_IOERR
    [InlineData(266)] // SQLITE_IOERR_READ (extended: primary code 10)
    [InlineData(11)]  // SQLITE_CORRUPT
    [InlineData(13)]  // SQLITE_FULL
    [InlineData(14)]  // SQLITE_CANTOPEN
    [InlineData(26)]  // SQLITE_NOTADB
    public void Run_StoreFaultOnWrite_EndsFailed_NothingCounted(int code)
    {
        Seed(20);
        var e = new ScriptedEmbedder();
        var store = new DecoratingTranscriptStore(_db.Store)
        {
            OnStoreEmbeddings = (_, _, _, _) => throw new SqliteException("store fault", code),
        };

        var run = Worker(e, store).Run(Ready, Fp, NoBudget, CancellationToken.None);

        Assert.Equal(EmbedStopReason.Failed, run.StoppedBy);
        Assert.Equal(EmbedGate.Failed("store fault"), run.State);
        Assert.Equal(0, run.Embedded);
        Assert.Equal(0, AttemptSum());
        Assert.Equal(0, VecRows());
        Assert.Equal(16, ChunkCalls(e).Count); // one batch, no single-chunk retry
        Assert.Equal(1, ProbeCalls(e));        // no probe after a chunk failure either
        Assert.Null(LeaseValue());
    }

    [Fact]
    public void Run_StoreFaultOnSingleChunkRetry_EndsFailed_NothingCounted()
    {
        Seed(3);
        var store = new DecoratingTranscriptStore(_db.Store);
        // the batch write rejects a value (a chunk failure), then the single-chunk write finds the disk full
        store.OnStoreEmbeddings = (b, _, _, _) =>
            b.Count > 1 ? throw new SqliteException("constraint failed", 19) : throw new SqliteException("database or disk is full", 13);

        var run = Worker(new ScriptedEmbedder(), store).Run(Ready, Fp, NoBudget, CancellationToken.None);

        Assert.Equal(EmbedStopReason.Failed, run.StoppedBy);
        Assert.Equal(EmbedGate.Failed("database or disk is full"), run.State);
        Assert.Equal(0, AttemptSum());
        Assert.Equal(0, VecRows());
    }

    [Fact]
    public void Run_Chunk4xx400_IsChunkFailure()
    {
        var ids = Seed(5);
        var e = new ScriptedEmbedder(OpenAiDescriptor)
            .Throw(Text(0), new HttpRequestException("bad request", null, HttpStatusCode.BadRequest));

        var run = Worker(e).Run(Ready, OpenAiFp, NoBudget, CancellationToken.None);

        Assert.Equal(new EmbedRun(4, 1, 0, Ready, EmbedStopReason.Done, FailedAlone: 1), run);
        Assert.Equal(1, Attempts(ids[0]));
        Assert.All(ids.Skip(1), id => Assert.Equal(1, Embedded(id)));
        Assert.Equal(OpenAiFp, _db.Store.ReadEmbedFingerprint());
    }

    [Fact]
    public void Run_MalformedRemoteResponse_InvalidOperationException_WithHealthyProbe_IsChunkFailure()
    {
        var ids = Seed(5);
        var e = new ScriptedEmbedder(OpenAiDescriptor)
            .Throw(Text(2), new InvalidOperationException("The requested operation requires an element of type 'Array'"));

        var run = Worker(e).Run(Ready, OpenAiFp, NoBudget, CancellationToken.None);

        Assert.Equal(new EmbedRun(4, 1, 0, Ready, EmbedStopReason.Done, FailedAlone: 1), run);
        Assert.Equal(1, Attempts(ids[2]));
        Assert.Equal(2, ProbeCalls(e));
    }

    // ---------------- races between the read and the write ----------------

    [Fact]
    public void Run_ContentChangedBetweenReadAndWrite_ChunkLeftUnembedded()
    {
        var ids = Seed(3);
        var e = new ScriptedEmbedder();
        var once = false;
        var worker = Worker(e, seam: () =>
        {
            if (once) return;
            once = true;
            _db.Exec($"UPDATE tx_chunk SET content = 'chunk changed' WHERE id = {ids[1]}");
        });

        var run = worker.Run(Ready, Fp, new EmbedBudget(null, 3), CancellationToken.None);

        Assert.Equal(2, run.Embedded);
        Assert.Equal(1, run.Remaining);
        Assert.Equal(0, Embedded(ids[1]));
        Assert.Equal(0, _db.Scalar($"SELECT COUNT(*) FROM tx_chunk_vec WHERE rowid = {ids[1]}"));
        Assert.Equal(0, Attempts(ids[1]));

        // the next run reads the new content and embeds that
        var next = worker.Run(Ready, Fp, NoBudget, CancellationToken.None);
        Assert.Equal(1, next.Embedded);
        Assert.Contains("chunk changed", e.Calls);
        Assert.Equal(1, _db.Scalar($"SELECT COUNT(*) FROM tx_chunk_vec WHERE rowid = {ids[1]}"));
    }

    [Fact]
    public void Run_KindFlippedBetweenReadAndWrite_VectorKindIsNew()
    {
        var ids = Seed(3, kind: "interactive");
        var session = _db.Scalar($"SELECT session_id FROM tx_chunk WHERE id = {ids[0]}");
        var worker = Worker(new ScriptedEmbedder(), seam: () =>
            _db.Exec($"UPDATE tx_session SET kind = 'automated' WHERE id = {session}"));

        worker.Run(Ready, Fp, NoBudget, CancellationToken.None);

        Assert.Equal(3, VecRows());
        Assert.Equal(3, _db.Scalar("SELECT COUNT(*) FROM tx_chunk_vec WHERE kind = 'automated'"));
    }

    [Fact]
    public void Run_ProjectChangedBetweenReadAndWrite_VectorProjectIsNew()
    {
        var ids = Seed(3, project: "owner/repo");
        var session = _db.Scalar($"SELECT session_id FROM tx_chunk WHERE id = {ids[0]}");
        var worker = Worker(new ScriptedEmbedder(), seam: () =>
            _db.Exec($"UPDATE tx_session SET project_key = 'moved/there' WHERE id = {session}"));

        worker.Run(Ready, Fp, NoBudget, CancellationToken.None);

        Assert.Equal(3, _db.Scalar("SELECT COUNT(*) FROM tx_chunk_vec WHERE project = 'moved/there'"));
        Assert.Equal(0, _db.Scalar("SELECT COUNT(*) FROM tx_chunk_vec WHERE project = 'owner/repo'"));
    }

    [Fact]
    public void Run_TsChangedBetweenReadAndWrite_VectorTsIsNew()
    {
        var ids = Seed(2);
        var once = false;
        var worker = Worker(new ScriptedEmbedder(), seam: () =>
        {
            if (once) return;
            once = true;
            // as ApplyIngest rewrites a chunk: the content is set (to the same value), which fires the reset trigger
            _db.Exec($"UPDATE tx_chunk SET ts = '2026-03-04T05:06:07Z', content = content WHERE id = {ids[0]}");
        });

        var run = worker.Run(Ready, Fp, NoBudget, CancellationToken.None);

        Assert.Equal(2, run.Embedded);
        Assert.Equal(1, Embedded(ids[0]));
        Assert.Equal(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero).ToUnixTimeSeconds(),
            _db.Scalar($"SELECT ts FROM tx_chunk_vec WHERE rowid = {ids[0]}"));
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero).ToUnixTimeSeconds(),
            _db.Scalar($"SELECT ts FROM tx_chunk_vec WHERE rowid = {ids[1]}"));
    }

    [Fact]
    public void Run_ChunkDeletedBetweenReadAndWrite_NoOrphan()
    {
        var ids = Seed(3);
        var worker = Worker(new ScriptedEmbedder(), seam: () =>
            _db.Exec($"DELETE FROM tx_chunk WHERE id = {ids[1]}"));

        var run = worker.Run(Ready, Fp, NoBudget, CancellationToken.None);

        Assert.Equal(new EmbedRun(2, 0, 0, Ready, EmbedStopReason.Done), run);
        Assert.Equal(2, VecRows());
        Assert.Equal(0, _db.Scalar($"SELECT COUNT(*) FROM tx_chunk_vec WHERE rowid = {ids[1]}"));
    }

    [Fact]
    public void Run_FingerprintMismatchOnWrite_EndsGate()
    {
        var ids = Seed(4);
        // one chunk was embedded by another model; its vector row appears between this worker's read and write
        var other = ids[3];
        _db.Exec($"UPDATE tx_chunk SET embedded = 1 WHERE id = {other}");
        var session = _db.Scalar($"SELECT session_id FROM tx_chunk WHERE id = {other}");
        var worker = Worker(new ScriptedEmbedder(), seam: () =>
        {
            using var conn = _db.Connect();
            using var tx = conn.BeginTransaction();
            foreach (var (k, v) in new[] { ("embed.provider", "openai"), ("embed.model", "other"), ("embed.revision", ""), ("embed.dimensions", "384") })
            {
                using var c = conn.CreateCommand();
                c.Transaction = tx;
                c.CommandText = "INSERT INTO tx_state (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value";
                c.Parameters.AddWithValue("$k", k);
                c.Parameters.AddWithValue("$v", v);
                c.ExecuteNonQuery();
            }
            using (var c = conn.CreateCommand())
            {
                c.Transaction = tx;
                c.CommandText = "INSERT INTO tx_chunk_vec(rowid, session_id, project, kind, ts, embedding) VALUES ($id, $s, 'owner/repo', 'interactive', 0, $e)";
                c.Parameters.AddWithValue("$id", other);
                c.Parameters.AddWithValue("$s", session);
                c.Parameters.AddWithValue("$e", MemoryMarshal.AsBytes(new ReadOnlySpan<float>(ScriptedEmbedder.Vector("x"))).ToArray());
                c.ExecuteNonQuery();
            }
            tx.Commit();
        });

        var run = worker.Run(Ready, Fp, NoBudget, CancellationToken.None);

        Assert.Equal(EmbedStopReason.Gate, run.StoppedBy);
        Assert.Equal(EmbedState.FingerprintMismatch, run.State.State);
        Assert.Equal("words only: vectors were made by openai/other; the running embedder is local/bge-small-en-v1.5", run.State.Text);
        Assert.Equal(0, run.Embedded);
        Assert.All(ids.Take(3), id => Assert.Equal(0, Embedded(id)));
        Assert.Equal(1, VecRows());
        Assert.Equal(0, AttemptSum());
        Assert.Null(LeaseValue());
    }

    // ---------------- resumption without duplicates ----------------

    [Fact]
    public void Run_Cancelled_ThenSecondRun_EachTextEmbeddedOnce()
    {
        var ids = Seed(40);
        var e = new ScriptedEmbedder();
        using var cts = new CancellationTokenSource();

        var first = Worker(e).Run(Ready, Fp, NoBudget, cts.Token, _ => cts.Cancel());
        Assert.Equal(EmbedStopReason.Cancelled, first.StoppedBy);

        var second = Worker(e).Run(Ready, Fp, NoBudget, CancellationToken.None);
        Assert.Equal(EmbedStopReason.Done, second.StoppedBy);

        var chunkCalls = ChunkCalls(e);
        Assert.Equal(40, chunkCalls.Count);
        Assert.Equal(40, chunkCalls.Distinct().Count());
        Assert.All(ids, id => Assert.Equal(1, Embedded(id)));
    }

    [Fact]
    public void Run_BudgetTime_ThenSecondRun_EachTextEmbeddedOnce()
    {
        var ids = Seed(40);
        var clock = new ManualTimeProvider(T0);
        var e = new ScriptedEmbedder { OnCall = _ => clock.Advance(TimeSpan.FromSeconds(1)) };
        var budget = new EmbedBudget(TimeSpan.FromSeconds(10), null);

        var first = Worker(e, time: clock).Run(Ready, Fp, budget, CancellationToken.None);
        Assert.Equal(EmbedStopReason.Budget, first.StoppedBy);
        var second = Worker(e, time: clock).Run(Ready, Fp, budget, CancellationToken.None);
        Assert.Equal(EmbedStopReason.Budget, second.StoppedBy);
        var third = Worker(e, time: clock).Run(Ready, Fp, budget, CancellationToken.None);
        Assert.Equal(EmbedStopReason.Done, third.StoppedBy);

        var chunkCalls = ChunkCalls(e);
        Assert.Equal(40, chunkCalls.Count);
        Assert.Equal(40, chunkCalls.Distinct().Count());
        Assert.All(ids, id => Assert.Equal(1, Embedded(id)));
    }
}

/// <summary>Worker tests that capture <see cref="Console.Error"/> (process-global), so they run alone.</summary>
[Collection("process-environment")]
public sealed class TranscriptEmbedWorkerLoggingTests : IDisposable
{
    private readonly TranscriptTestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static string Capture(Action action)
    {
        var original = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try { action(); }
        finally { Console.SetError(original); }
        return captured.ToString();
    }

    [Fact]
    public void Run_BusyOnFinallyWrites_AreCaughtAndLogged()
    {
        var s = _db.AddSession("pi", "s1");
        _db.AddChunk(s, "a", 0);
        _db.AddChunk(s, "b", 1);
        var store = new DecoratingTranscriptStore(_db.Store)
        {
            OnWriteEmbedRun = _ => throw new SqliteException("database is locked (write run)", 5),
            OnReleaseEmbedLease = _ => throw new SqliteException("database is locked (release)", 5),
        };
        var worker = new TranscriptEmbedWorker(store, new ScriptedEmbedder(),
            new ManualTimeProvider(TranscriptEmbedWorkerTests.T0), "host-a", 100, (_, _) => true);

        EmbedRun? run = null;
        var log = Capture(() => run = worker.Run(
            TranscriptEmbedWorkerTests.Ready, TranscriptEmbedWorkerTests.Fp, new EmbedBudget(null, null), CancellationToken.None));

        Assert.NotNull(run);
        Assert.Equal(EmbedStopReason.Done, run!.StoppedBy);
        Assert.Equal(2, run.Embedded);
        Assert.Equal(1, store.WriteRunCalls);
        Assert.Equal(1, store.ReleaseCalls);
        Assert.Contains("database is locked (write run)", log);
        Assert.Contains("database is locked (release)", log);
    }

    [Fact]
    public void Run_ChunkFailure_IsLoggedThroughLogChain()
    {
        var s = _db.AddSession("pi", "s1");
        _db.AddChunk(s, "a", 0);
        var e = new ScriptedEmbedder().Throw("a", new KeyNotFoundException("planted chunk failure"));
        var worker = new TranscriptEmbedWorker(_db.Store, e,
            new ManualTimeProvider(TranscriptEmbedWorkerTests.T0), "host-a", 100, (_, _) => true);

        var log = Capture(() => worker.Run(
            TranscriptEmbedWorkerTests.Ready, TranscriptEmbedWorkerTests.Fp, new EmbedBudget(null, null), CancellationToken.None));

        Assert.Contains("[total-recall] transcripts embed", log);
        Assert.Contains("KeyNotFoundException: planted chunk failure", log);
    }
}
