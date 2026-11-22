using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Tests.TestSupport;
using TotalRecall.Infrastructure.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

/// <summary>
/// Two embed workers on one archive file, each with its own connection and its own run id (search the archive, spec
/// section 3 item 1 and section 8, test 12): only the lease holder embeds and each chunk is embedded once; a stale
/// lease is taken over, and so is one whose process on this machine is gone, while a fresh lease of another machine is
/// not. The workers use the real process check (no injected liveness) unless said otherwise; no model.
/// </summary>
public sealed class TranscriptEmbedConcurrencyTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private static readonly EmbedGateResult Ready = new(EmbedState.Ready, "ready");
    private static readonly ArchiveFingerprint Fp = new("local", "bge-small-en-v1.5", "rev1", 384);
    private static readonly EmbedBudget NoBudget = new(null, null);

    private readonly TranscriptTestDb _db = new();
    private readonly List<ITranscriptStore> _stores = new();

    public void Dispose()
    {
        foreach (var s in _stores) s.Dispose();
        _db.Dispose();
    }

    /// <summary>A second, independent store (its own connection) on the same archive file.</summary>
    private ITranscriptStore SecondStore()
    {
        var open = TranscriptStore.Open(new TranscriptLocation(_db.Path, IsMainDatabase: false), create: false);
        Assert.Equal(TranscriptOpenStatus.Opened, open.Status);
        _stores.Add(open.Store!);
        return open.Store!;
    }

    /// <summary>An embedder that can hold its first call (the probe) until released; records every text, thread-safely.</summary>
    private sealed class HoldingEmbedder : IEmbedder
    {
        private readonly object _lock = new();
        private readonly List<string> _calls = new();
        private int _first = 1;

        public EmbedderDescriptor Descriptor { get; } = new("local", "bge-small-en-v1.5", "rev1", 384);
        public ManualResetEventSlim? Hold { get; init; }
        public ManualResetEventSlim Entered { get; } = new(false);

        public List<string> Calls { get { lock (_lock) return _calls.ToList(); } }

        public float[] Embed(string text)
        {
            lock (_lock) _calls.Add(text);
            if (Interlocked.Exchange(ref _first, 0) == 1)
            {
                Entered.Set();
                Hold?.Wait(Wait);
            }
            return ScriptedEmbedder.Vector(text);
        }
    }

    private List<string> Seed(int n)
    {
        var s = _db.AddSession("claude-code", "conc-" + Guid.NewGuid().ToString("N"));
        var texts = new List<string>();
        for (var i = 0; i < n; i++)
        {
            var text = $"header\n\nconcurrent chunk {i:000}";
            _db.AddChunk(s, text, i);
            texts.Add(text);
        }
        return texts;
    }

    private string? Lease() => _db.Text("SELECT value FROM tx_state WHERE key = 'embed.lease'");

    /// <summary>A pid no process has (Linux and Windows cap pids far below it).</summary>
    private static int DeadPid()
    {
        for (var pid = 2_000_000_000; ; pid++)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                return pid;
            }
        }
    }

    [Fact]
    public async Task TwoWorkers_OnlyOneEmbeds_EachChunkOnce()
    {
        var texts = Seed(40);
        using var hold = new ManualResetEventSlim(false);
        var a = new HoldingEmbedder { Hold = hold };
        var b = new HoldingEmbedder();

        // Worker A takes the lease and is held inside its probe; worker B, on its own connection, comes next.
        var runA = Task.Run(() => new TranscriptEmbedWorker(_db.Store, a).Run(Ready, Fp, NoBudget, CancellationToken.None));
        Assert.True(a.Entered.Wait(Wait), "worker A never reached its probe");
        var leaseOfA = Lease();
        Assert.NotNull(leaseOfA);

        var resultB = new TranscriptEmbedWorker(SecondStore(), b).Run(Ready, Fp, NoBudget, CancellationToken.None);

        Assert.Equal(EmbedStopReason.Lease, resultB.StoppedBy);
        Assert.Equal(EmbedState.AnotherRunActive, resultB.State.State);
        Assert.Equal(0, resultB.Embedded);
        Assert.Empty(b.Calls);
        Assert.Equal(leaseOfA, Lease()); // B neither replaced nor deleted A's lease

        hold.Set();
        var resultA = await runA.WaitAsync(Wait);

        Assert.Equal(EmbedStopReason.Done, resultA.StoppedBy);
        Assert.Equal(40, resultA.Embedded);
        Assert.Equal(texts, a.Calls.Where(c => c != EmbedProbe.Text).ToList()); // each chunk once, in id order
        Assert.Equal(40, _db.Scalar("SELECT count(*) FROM tx_chunk_vec"));
        Assert.Equal(40, _db.Scalar("SELECT count(*) FROM tx_chunk WHERE embedded = 1"));
        Assert.Null(Lease()); // A released its own lease

        // With A gone, B runs and finds nothing left to embed.
        var again = new TranscriptEmbedWorker(SecondStore(), b).Run(Ready, Fp, NoBudget, CancellationToken.None);
        Assert.Equal(EmbedStopReason.Done, again.StoppedBy);
        Assert.Equal(0, again.Embedded);
        Assert.Empty(b.Calls);
    }

    [Fact]
    public void StaleLease_TakenOver()
    {
        Seed(5);
        var now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var other = new EmbedLeaseHolder("another-machine", 4242, "their-run");

        // A fresh lease of a live holder on another machine (nine minutes old) holds.
        Assert.True(_db.Store.TryAcquireEmbedLease(other, now.AddMinutes(-9), (_, _) => true));
        var held = new ScriptedEmbedder();
        var refused = new TranscriptEmbedWorker(SecondStore(), held, new ManualTimeProvider(now)).Run(Ready, Fp, NoBudget, CancellationToken.None);
        Assert.Equal(EmbedStopReason.Lease, refused.StoppedBy);
        Assert.Empty(held.Calls);
        Assert.Contains("their-run", Lease());

        // Two minutes later the same lease is eleven minutes old: stale, so the next run takes it over.
        var later = now.AddMinutes(2);
        var embedder = new ScriptedEmbedder();
        var run = new TranscriptEmbedWorker(SecondStore(), embedder, new ManualTimeProvider(later)).Run(Ready, Fp, NoBudget, CancellationToken.None);

        Assert.Equal(EmbedStopReason.Done, run.StoppedBy);
        Assert.Equal(5, run.Embedded);
        Assert.Equal(5, _db.Scalar("SELECT count(*) FROM tx_chunk WHERE embedded = 1"));
        Assert.Null(Lease()); // the taker released its own lease at the end
    }

    [Fact]
    public void DeadPidLease_TakenOver_OtherMachineNot()
    {
        Seed(5);
        var now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var dead = DeadPid();

        // Another machine's lease, one minute old, with a pid that does not exist here: not ours to judge, so it holds.
        Assert.True(_db.Store.TryAcquireEmbedLease(
            new EmbedLeaseHolder("other-" + Environment.MachineName, dead, "their-run"), now.AddMinutes(-1), (_, _) => true));
        var refusedEmbedder = new ScriptedEmbedder();
        var refused = new TranscriptEmbedWorker(SecondStore(), refusedEmbedder, new ManualTimeProvider(now))
            .Run(Ready, Fp, NoBudget, CancellationToken.None);
        Assert.Equal(EmbedStopReason.Lease, refused.StoppedBy);
        Assert.Empty(refusedEmbedder.Calls);
        _db.Exec("DELETE FROM tx_state WHERE key = 'embed.lease'");

        // This machine's lease, one minute old, whose process is gone (a crash, a second Ctrl+C): taken over at once.
        Assert.True(_db.Store.TryAcquireEmbedLease(
            new EmbedLeaseHolder(Environment.MachineName, dead, "crashed-run"), now.AddMinutes(-1), (_, _) => true));
        var embedder = new ScriptedEmbedder();
        var run = new TranscriptEmbedWorker(SecondStore(), embedder, new ManualTimeProvider(now)).Run(Ready, Fp, NoBudget, CancellationToken.None);

        Assert.Equal(EmbedStopReason.Done, run.StoppedBy);
        Assert.Equal(5, run.Embedded);
        Assert.Null(Lease());

        // And this machine's lease of a live process (this one) holds.
        Seed(2);
        Assert.True(_db.Store.TryAcquireEmbedLease(
            new EmbedLeaseHolder(Environment.MachineName, Environment.ProcessId, "live-run"), now.AddMinutes(-1), (_, _) => true));
        var liveEmbedder = new ScriptedEmbedder();
        var live = new TranscriptEmbedWorker(SecondStore(), liveEmbedder, new ManualTimeProvider(now))
            .Run(Ready, Fp, NoBudget, CancellationToken.None);
        Assert.Equal(EmbedStopReason.Lease, live.StoppedBy);
        Assert.Empty(liveEmbedder.Calls);
    }
}
