using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Core;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Tests.TestSupport;
using TotalRecall.Infrastructure.Transcripts;
using TotalRecall.TestSupport.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

/// <summary>
/// <see cref="ArchivePass"/>, the <c>session_start</c> trigger (search the archive, spec section 4): <c>Kick</c> returns at
/// once and starts at most one pass per interval; the pass re-checks the settings, opens the archive without creating
/// it, ingests per adapter under the ingest budget, evaluates the gate (rules 1 and 2 before the embedder is built) and
/// embeds on a background thread under the embed budget. A file-backed archive, fixture session files and fake
/// embedders; no model. In the process-environment collection because one test swaps <see cref="Console.Error"/>.
/// </summary>
[Collection("process-environment")]
public sealed class ArchivePassTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private static readonly EmbedBudget NoBudget = new(null, null);

    private readonly IngestHarness _h = new();
    private readonly List<ArchivePass> _passes = new();

    public void Dispose()
    {
        foreach (var p in _passes)
        {
            p.Dispose();
            try { p.WaitIdleAsync().Wait(Wait); } catch { /* best effort */ }
        }
        _h.Dispose();
    }

    private TranscriptTestDb Db => _h.Db;
    private ClaudeFixtureHome Home => _h.Home;

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static Core.Config.EmbeddingConfig Embedding(string? provider = null) => new(
        "bge-small-en-v1.5", 384,
        provider is null ? FSharpOption<string>.None : FSharpOption<string>.Some(provider),
        FSharpOption<string>.None, FSharpOption<string>.None, FSharpOption<string>.None,
        FSharpOption<string>.None, FSharpOption<string>.None, FSharpOption<string>.None, FSharpOption<string>.None);

    private static TranscriptsSettings Settings(bool enabled = true, bool allowRemote = false) =>
        new(enabled, string.Empty, null, 0, allowRemote);

    /// <summary>An embedder that records its calls, the thread it runs on and its disposal; optionally blocks.</summary>
    private sealed class TestEmbedder : IEmbedder, IEmbedderReadiness, IDisposable
    {
        private readonly object _lock = new();
        private readonly List<string> _calls = new();

        public TestEmbedder(string provider = "local", EmbedderReadiness? readiness = null)
        {
            Descriptor = new EmbedderDescriptor(provider, "bge-small-en-v1.5", "rev1", 384);
            Readiness = readiness ?? new EmbedderReadiness(true, null);
        }

        public EmbedderDescriptor Descriptor { get; }
        public EmbedderReadiness Readiness { get; }
        public ManualResetEventSlim? Gate { get; init; }
        public ManualResetEventSlim Entered { get; } = new(false);
        public bool Disposed { get; private set; }
        public bool? IsBackground { get; private set; }
        public ThreadPriority? Priority { get; private set; }

        public List<string> Calls { get { lock (_lock) return _calls.ToList(); } }

        public EmbedderReadiness CheckReady() => Readiness;

        public float[] Embed(string text)
        {
            lock (_lock) _calls.Add(text);
            IsBackground = Thread.CurrentThread.IsBackground;
            Priority = Thread.CurrentThread.Priority;
            Entered.Set();
            Gate?.Wait(Wait);
            return ScriptedEmbedder.Vector(text);
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>An inspection that hands out <paramref name="embedder"/> and records each call's thread count.</summary>
    private sealed class RecordingInspect
    {
        private readonly TestEmbedder? _embedder;
        private readonly Exception? _throw;

        public RecordingInspect(TestEmbedder? embedder, Exception? @throw = null)
        {
            _embedder = embedder;
            _throw = @throw;
        }

        public List<int?> Threads { get; } = new();

        public EmbedderInspectionResult Inspect(Core.Config.EmbeddingConfig cfg, int? threads)
        {
            Threads.Add(threads);
            if (_throw is not null) throw _throw;
            var e = _embedder!;
            var d = e.Descriptor;
            return new EmbedderInspectionResult(
                e, new ArchiveFingerprint(d.Provider, d.Model, d.Revision, d.Dimensions), e.CheckReady(), null);
        }
    }

    /// <summary>Wraps an adapter: <see cref="Detect"/> may block, throw or run an action, and the n-th <see cref="ReadHead"/> may run an action.</summary>
    private sealed class ControlAdapter : ITranscriptAdapter
    {
        private readonly ITranscriptAdapter _inner;
        private int _heads;

        public ControlAdapter(ITranscriptAdapter inner) => _inner = inner;

        public ManualResetEventSlim? DetectGate { get; init; }
        public ManualResetEventSlim DetectEntered { get; } = new(false);
        public Exception? DetectThrows { get; init; }
        public int OnHeadNumber { get; init; }
        /// <summary>Runs inside <see cref="Detect"/> (a test advances its clock here).</summary>
        public Action? OnDetect { get; init; }
        /// <summary>Runs inside the <see cref="OnHeadNumber"/>-th <see cref="ReadHead"/> (a test advances its clock here).</summary>
        public Action? OnHead { get; init; }
        public int DetectCalls;

        public string Host => _inner.Host;
        public string Root => _inner.Root;

        public bool Detect()
        {
            Interlocked.Increment(ref DetectCalls);
            DetectEntered.Set();
            DetectGate?.Wait(Wait);
            OnDetect?.Invoke();
            if (DetectThrows is not null) throw DetectThrows;
            return _inner.Detect();
        }

        public SessionEnumeration EnumerateSessions() => _inner.EnumerateSessions();

        public SessionHead ReadHead(SessionSource source)
        {
            if (++_heads == OnHeadNumber)
                OnHead?.Invoke();
            return _inner.ReadHead(source);
        }

        public bool IsLineStart(SessionSource source, long offset) => _inner.IsLineStart(source, offset);

        public IEnumerable<TranscriptRecord> ReadRecords(SessionSource source, long startOffset, ReadStats stats, CancellationToken ct) =>
            _inner.ReadRecords(source, startOffset, stats, ct);
    }

    private ClaudeCodeAdapter Claude() => new(Home.ProjectsDir);

    private TranscriptLocation Location => new(Db.Path, IsMainDatabase: false);

    private ArchivePassContext Context(
        Func<Core.Config.EmbeddingConfig, int?, EmbedderInspectionResult> inspect,
        IReadOnlyList<ITranscriptAdapter>? adapters = null,
        TranscriptsSettings? settings = null,
        TranscriptLocation? location = null,
        string storageMode = "sqlite",
        string? provider = null,
        Func<bool>? reindexRunning = null,
        bool useLocation = true) =>
        new(settings ?? Settings(),
            useLocation ? location ?? Location : null,
            storageMode,
            Embedding(provider),
            inspect,
            reindexRunning ?? (() => false),
            () => adapters ?? Array.Empty<ITranscriptAdapter>());

    private ArchivePass Pass(
        Func<ArchivePassContext?> context,
        Func<bool>? isEnabled = null,
        ArchivePassOptions? options = null,
        TimeProvider? time = null)
    {
        var p = new ArchivePass(
            context,
            isEnabled ?? (() => true),
            options ?? new ArchivePassOptions(EmbedBudget: NoBudget, MinInterval: TimeSpan.Zero),
            time);
        _passes.Add(p);
        return p;
    }

    private static async Task<ArchivePassResult> Run(ArchivePass p)
    {
        p.Kick();
        await p.WaitIdleAsync().WaitAsync(Wait);
        Assert.NotNull(p.Last);
        return p.Last!;
    }

    private List<long> SeedChunks(int n)
    {
        var s = Db.AddSession("pi", "seed-" + Guid.NewGuid().ToString("N"));
        return Enumerable.Range(0, n).Select(i => Db.AddChunk(s, $"seeded chunk {i:000}", i)).ToList();
    }

    private long Sessions() => Db.Scalar("SELECT count(*) FROM tx_session");
    private long Chunks() => Db.Scalar("SELECT count(*) FROM tx_chunk");
    private long EmbeddedChunks() => Db.Scalar("SELECT count(*) FROM tx_chunk WHERE embedded = 1");

    // ------------------------------------------------------------------
    // Kick
    // ------------------------------------------------------------------

    [Fact]
    public async Task Kick_ReturnsImmediately_EvenWhenAdapterAndEmbedderBlock()
    {
        // The adapter blocks.
        using var adapterGate = new ManualResetEventSlim(false);
        var adapter = new ControlAdapter(Claude()) { DetectGate = adapterGate };
        var e1 = new TestEmbedder();
        var p1 = Pass(() => Context(new RecordingInspect(e1).Inspect, new ITranscriptAdapter[] { adapter }));

        var sw = Stopwatch.StartNew();
        p1.Kick();
        sw.Stop();
        // generous for a loaded machine; a synchronous Kick would block for the whole 30 s wait
        Assert.True(sw.ElapsedMilliseconds < 2000, $"Kick took {sw.ElapsedMilliseconds} ms with the adapter blocked");
        Assert.True(adapter.DetectEntered.Wait(Wait));
        adapterGate.Set();
        await p1.WaitIdleAsync().WaitAsync(Wait);

        // The embedder blocks.
        SeedChunks(3);
        using var embedGate = new ManualResetEventSlim(false);
        var e2 = new TestEmbedder { Gate = embedGate };
        var p2 = Pass(() => Context(new RecordingInspect(e2).Inspect));

        sw.Restart();
        p2.Kick();
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 2000, $"Kick took {sw.ElapsedMilliseconds} ms with the embedder blocked");
        Assert.True(e2.Entered.Wait(Wait));
        embedGate.Set();
        await p2.WaitIdleAsync().WaitAsync(Wait);
        Assert.Equal(3, p2.Last!.Embed!.Embedded);
    }

    [Fact]
    public async Task Kick_Disabled_DoesNothing_ArchiveNeverOpened()
    {
        var contextCalls = 0;
        var enabledCalls = 0;
        var p = Pass(() => { contextCalls++; return Context((_, _) => throw new InvalidOperationException()); },
            isEnabled: () => { enabledCalls++; return false; });

        p.Kick();
        await p.WaitIdleAsync().WaitAsync(Wait);

        Assert.Equal(1, enabledCalls);
        Assert.Equal(0, contextCalls);
        Assert.Null(p.Last);
    }

    [Fact]
    public async Task Kick_WhilePassRunning_IsNoop()
    {
        using var gate = new ManualResetEventSlim(false);
        var adapter = new ControlAdapter(Claude()) { DetectGate = gate };
        var contextCalls = 0;
        var p = Pass(() =>
        {
            Interlocked.Increment(ref contextCalls);
            return Context(new RecordingInspect(new TestEmbedder()).Inspect, new ITranscriptAdapter[] { adapter });
        });

        p.Kick();
        Assert.True(adapter.DetectEntered.Wait(Wait));
        p.Kick();
        p.Kick();
        gate.Set();
        await p.WaitIdleAsync().WaitAsync(Wait);

        Assert.Equal(1, contextCalls);
        Assert.Equal(1, adapter.DetectCalls);
    }

    [Fact]
    public async Task Kick_WithinMinInterval_IsNoop_AndAfterItStartsAgain()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
        var contextCalls = 0;
        var p = Pass(() => { contextCalls++; return Context(new RecordingInspect(new TestEmbedder()).Inspect); },
            options: new ArchivePassOptions(EmbedBudget: NoBudget, MinInterval: TimeSpan.FromMinutes(10)),
            time: clock);

        await Run(p);
        Assert.Equal(1, contextCalls);

        clock.Advance(TimeSpan.FromMinutes(9));
        p.Kick();
        await p.WaitIdleAsync().WaitAsync(Wait);
        Assert.Equal(1, contextCalls);

        clock.Advance(TimeSpan.FromMinutes(1));
        await Run(p);
        Assert.Equal(2, contextCalls);
    }

    [Fact]
    public async Task Kick_AfterTheClockMovedBackwards_IsDue()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
        var contextCalls = 0;
        var p = Pass(() => { contextCalls++; return Context(new RecordingInspect(new TestEmbedder()).Inspect); },
            options: new ArchivePassOptions(EmbedBudget: NoBudget, MinInterval: TimeSpan.FromMinutes(10)),
            time: clock);

        await Run(p);
        Assert.Equal(1, contextCalls);

        // the wall clock was set back an hour (or the last start was recorded under a wrong time): a pass is due,
        // not blocked until the clock catches up again
        clock.Advance(TimeSpan.FromHours(-1));
        await Run(p);
        Assert.Equal(2, contextCalls);
    }

    [Fact]
    public void Defaults_AreTheSpecValues()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), ArchivePass.DefaultIngestBudget);
        Assert.Equal(new EmbedBudget(TimeSpan.FromSeconds(30), 256), ArchivePass.DefaultEmbedBudget);
        Assert.Equal(TimeSpan.FromMinutes(10), ArchivePass.DefaultMinInterval);
        Assert.Equal(2, ArchivePass.PassIntraOpThreads);
    }

    // ------------------------------------------------------------------
    // The pass: step 1
    // ------------------------------------------------------------------

    [Fact]
    public async Task Pass_ArchiveAbsent_StopsSilently()
    {
        Home.Place("decision", _h.Cwd, "absent-1");
        var adapter = new ControlAdapter(Claude());
        var inspect = new RecordingInspect(new TestEmbedder());
        var missing = Path.Combine(Db.Dir, "none", "archive.db");
        var p = Pass(() => Context(inspect.Inspect, new ITranscriptAdapter[] { adapter }, location: new TranscriptLocation(missing, false)));

        var result = await Run(p);

        Assert.Equal("absent", result.Outcome);
        Assert.Equal(0, result.SessionsIngested);
        Assert.Null(result.Embed);
        Assert.Equal(0, adapter.DetectCalls);
        Assert.Empty(inspect.Threads);
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public async Task Pass_SettingOff_StopsSilently()
    {
        var adapter = new ControlAdapter(Claude());
        var inspect = new RecordingInspect(new TestEmbedder());
        var p = Pass(() => Context(inspect.Inspect, new ITranscriptAdapter[] { adapter }, settings: Settings(enabled: false)));

        var result = await Run(p);

        Assert.Equal("off", result.Outcome);
        Assert.Equal(0, adapter.DetectCalls);
        Assert.Empty(inspect.Threads);
    }

    [Fact]
    public async Task Pass_Postgres_StopsSilently()
    {
        Home.Place("decision", _h.Cwd, "pg-1");
        SeedChunks(2);
        var adapter = new ControlAdapter(Claude());
        var inspect = new RecordingInspect(new TestEmbedder());
        var p = Pass(() => Context(inspect.Inspect, new ITranscriptAdapter[] { adapter }, storageMode: "postgres"));

        var result = await Run(p);

        Assert.Equal("postgres", result.Outcome);
        Assert.Null(result.Embed);
        Assert.Equal(0, adapter.DetectCalls);
        Assert.Empty(inspect.Threads);
        Assert.Equal(1, Sessions()); // the seeded one only
        Assert.Equal(0, EmbeddedChunks());
    }

    [Fact]
    public async Task Pass_NoLocation_StopsSilently()
    {
        var adapter = new ControlAdapter(Claude());
        var p = Pass(() => Context(new RecordingInspect(new TestEmbedder()).Inspect, new ITranscriptAdapter[] { adapter }, useLocation: false));

        var result = await Run(p);

        Assert.Equal("unavailable", result.Outcome);
        Assert.Equal(0, adapter.DetectCalls);
    }

    [Fact]
    public async Task Pass_ContextThrows_StopsAndIsLogged()
    {
        var p = Pass(() => throw new InvalidOperationException("config unreadable zqxctx"));

        var result = await Run(p);

        Assert.Equal("unavailable", result.Outcome);
        Assert.Null(result.Embed);
    }

    // ------------------------------------------------------------------
    // Ingest, then embed
    // ------------------------------------------------------------------

    [Fact]
    public async Task Pass_IngestsThenEmbeds()
    {
        Home.Place("decision", _h.Cwd, "both-1");
        Home.Place("compaction", _h.Cwd, "both-2");
        var e = new TestEmbedder();
        long chunksWhenEmbeddingStarted = -1;
        var inspect = new RecordingInspect(e);
        var p = Pass(() => Context(
            (c, t) => { chunksWhenEmbeddingStarted = Chunks(); return inspect.Inspect(c, t); },
            new ITranscriptAdapter[] { Claude() }));

        var result = await Run(p);

        Assert.Equal("done", result.Outcome);
        Assert.Equal(2, result.SessionsIngested);
        Assert.Equal(2, Sessions());
        var chunks = Chunks();
        Assert.True(chunks > 0);
        Assert.Equal(chunks, chunksWhenEmbeddingStarted); // ingest finished before the embed step began
        Assert.Equal(chunks, EmbeddedChunks());
        Assert.NotNull(result.Embed);
        Assert.Equal(EmbedStopReason.Done, result.Embed!.StoppedBy);
        Assert.Equal((int)chunks, result.Embed.Embedded);
        Assert.Equal(EmbedState.Ready, result.Embed.State.State);
    }

    [Fact]
    public async Task Pass_AsksTheFactoryFor2Threads_AndDisposesTheEmbedder_OnABackgroundBelowNormalThread()
    {
        SeedChunks(2);
        var e = new TestEmbedder();
        var inspect = new RecordingInspect(e);
        var p = Pass(() => Context(inspect.Inspect));

        var result = await Run(p);

        Assert.Equal(2, result.Embed!.Embedded);
        Assert.Equal(new int?[] { 2 }, inspect.Threads.ToArray());
        Assert.True(e.Disposed);
        Assert.True(e.IsBackground);
        Assert.Equal(ThreadPriority.BelowNormal, e.Priority);
    }

    [Fact]
    public async Task Pass_IngestBudgetCancelsRemainingSessions()
    {
        var newest = Home.Place("decision", _h.Cwd, "budget-newest");
        var middle = Home.Place("decision", _h.Cwd, "budget-middle");
        var oldest = Home.Place("decision", _h.Cwd, "budget-oldest");
        Home.SetMtime(newest, Home.BaseTime.AddDays(3));
        Home.SetMtime(middle, Home.BaseTime.AddDays(2));
        Home.SetMtime(oldest, Home.BaseTime.AddDays(1));
        // The second session's head read outlasts the budget on the pass's clock: the first is stored, the rest wait.
        // The budget is an hour, so only the fake clock can end it (no wall-clock margin is involved).
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
        var adapter = new ControlAdapter(Claude()) { OnHeadNumber = 2, OnHead = () => clock.Advance(TimeSpan.FromHours(1)) };
        var e = new TestEmbedder();
        var p = Pass(() => Context(new RecordingInspect(e).Inspect, new ITranscriptAdapter[] { adapter }),
            options: new ArchivePassOptions(IngestBudget: TimeSpan.FromHours(1), EmbedBudget: NoBudget, MinInterval: TimeSpan.Zero),
            time: clock);

        var result = await Run(p);

        Assert.Equal(1, result.SessionsIngested);
        Assert.Equal(1, Sessions());
        Assert.Equal("budget-newest", Db.Text("SELECT host_session_id FROM tx_session"));
        // The embed step still runs after an ingest that ran out of time.
        Assert.Equal(EmbedStopReason.Done, result.Embed!.StoppedBy);
        Assert.Equal(Chunks(), EmbeddedChunks());
    }

    [Fact]
    public async Task Pass_IngestBudgetIsForThePass_LaterAdaptersWait()
    {
        Home.Place("decision", _h.Cwd, "later-1");
        // The first adapter's detection uses up the pass's budget on the pass's clock; the next adapter is not started.
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
        var slow = new ControlAdapter(new ClaudeCodeAdapter(Path.Combine(Home.Root, "no-such-dir")))
        {
            OnDetect = () => clock.Advance(TimeSpan.FromHours(1)),
        };
        var later = new ControlAdapter(Claude());
        var p = Pass(() => Context(new RecordingInspect(new TestEmbedder()).Inspect, new ITranscriptAdapter[] { slow, later }),
            options: new ArchivePassOptions(IngestBudget: TimeSpan.FromHours(1), EmbedBudget: NoBudget, MinInterval: TimeSpan.Zero),
            time: clock);

        var result = await Run(p);

        Assert.Equal(0, result.SessionsIngested);
        Assert.Equal(0, later.DetectCalls);
        Assert.Equal(0, Sessions());
    }

    [Fact]
    public async Task Pass_EmbedBudgetStops_RestWaits()
    {
        SeedChunks(40);
        var e = new TestEmbedder();
        var p = Pass(() => Context(new RecordingInspect(e).Inspect),
            options: new ArchivePassOptions(EmbedBudget: new EmbedBudget(null, 16), MinInterval: TimeSpan.Zero));

        var result = await Run(p);

        Assert.Equal(EmbedStopReason.Budget, result.Embed!.StoppedBy);
        Assert.Equal(16, result.Embed.Embedded);
        Assert.Equal(24, result.Embed.Remaining);
        Assert.Equal(16, EmbeddedChunks());

        var second = await Run(p);
        Assert.Equal(16, second.Embed!.Embedded);
        Assert.Equal(32, EmbeddedChunks());
    }

    [Fact]
    public async Task Pass_AdapterThrows_NextAdapterStillRuns_Logged()
    {
        Home.Place("decision", _h.Cwd, "next-1");
        var broken = new ControlAdapter(Claude()) { DetectThrows = new IOException("adapter exploded zqxadapter") };
        var good = new ControlAdapter(Claude());
        var p = Pass(() => Context(new RecordingInspect(new TestEmbedder()).Inspect, new ITranscriptAdapter[] { broken, good }));

        var original = Console.Error;
        var captured = new StringWriter();
        ArchivePassResult result;
        try
        {
            Console.SetError(captured);
            result = await Run(p);
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Equal(1, broken.DetectCalls);
        Assert.Equal(1, good.DetectCalls);
        Assert.Equal(1, result.SessionsIngested);
        Assert.Equal(1, Sessions());
        var log = captured.ToString();
        Assert.Contains("[total-recall]", log);
        Assert.Contains("zqxadapter", log);
        Assert.Contains("claude-code", log);
    }

    [Fact]
    public async Task Pass_GateNotReady_IngestsButDoesNotEmbed()
    {
        Home.Place("decision", _h.Cwd, "notready-1");
        var e = new TestEmbedder(readiness: new EmbedderReadiness(false, "model.onnx missing"));
        var p = Pass(() => Context(new RecordingInspect(e).Inspect, new ITranscriptAdapter[] { Claude() }));

        var result = await Run(p);

        Assert.Equal(1, result.SessionsIngested);
        Assert.True(Chunks() > 0);
        Assert.Equal(0, EmbeddedChunks());
        Assert.Empty(e.Calls);
        Assert.Equal(EmbedState.ModelNotReady, result.Embed!.State.State);
        Assert.Equal(EmbedStopReason.Gate, result.Embed.StoppedBy);
        Assert.Equal((int)Chunks(), result.Embed.Remaining);
        Assert.True(e.Disposed);
    }

    [Fact]
    public async Task Pass_MemoryReindexRunning_Defers()
    {
        SeedChunks(3);
        var e = new TestEmbedder();
        var p = Pass(() => Context(new RecordingInspect(e).Inspect, reindexRunning: () => true));

        var result = await Run(p);

        Assert.Equal(EmbedState.WaitingForReindex, result.Embed!.State.State);
        Assert.Equal(EmbedStopReason.Gate, result.Embed.StoppedBy);
        Assert.Equal(3, result.Embed.Remaining);
        Assert.Empty(e.Calls);
        Assert.Equal(0, EmbeddedChunks());
        Assert.True(e.Disposed);
    }

    [Fact]
    public async Task Pass_RemoteWithoutOptIn_NeverCallsEmbedder()
    {
        SeedChunks(3);
        var e = new TestEmbedder(provider: "openai");
        var inspect = new RecordingInspect(e);
        var p = Pass(() => Context(inspect.Inspect, provider: "openai"));

        var result = await Run(p);

        Assert.Empty(inspect.Threads); // not even built: rules 1 and 2 come from config
        Assert.Empty(e.Calls);
        Assert.Equal(EmbedState.RemoteNotAllowed, result.Embed!.State.State);
        Assert.Equal(EmbedStopReason.Gate, result.Embed.StoppedBy);
        Assert.Equal(3, result.Embed.Remaining);
        Assert.Equal(0, EmbeddedChunks());
    }

    [Fact]
    public async Task Pass_RemoteWithOptIn_Embeds()
    {
        SeedChunks(3);
        var e = new TestEmbedder(provider: "openai");
        var p = Pass(() => Context(new RecordingInspect(e).Inspect, provider: "openai", settings: Settings(allowRemote: true)));

        var result = await Run(p);

        Assert.Equal(3, result.Embed!.Embedded);
        Assert.Equal(3, EmbeddedChunks());
    }

    [Fact]
    public async Task Pass_FactoryThrows_IngestsButDoesNotEmbed_StateNotReady()
    {
        Home.Place("decision", _h.Cwd, "factory-1");
        var p = Pass(() => Context(
            (c, t) => EmbedderInspection.Inspect(c, t, (_, _) => throw new InvalidOperationException("model files missing zqxfactory")),
            new ITranscriptAdapter[] { Claude() }));

        var result = await Run(p);

        Assert.Equal(1, result.SessionsIngested);
        Assert.Equal(0, EmbeddedChunks());
        Assert.Equal(EmbedState.ModelNotReady, result.Embed!.State.State);
        Assert.Contains("zqxfactory", result.Embed.State.Text);
    }

    [Fact]
    public async Task Pass_InspectDelegateThrows_StateNotReady()
    {
        SeedChunks(2);
        var p = Pass(() => Context(new RecordingInspect(null, new InvalidOperationException("inspect broke zqxinspect")).Inspect));

        var result = await Run(p);

        Assert.Equal("done", result.Outcome);
        Assert.Equal(EmbedState.ModelNotReady, result.Embed!.State.State);
        Assert.Equal(0, EmbeddedChunks());
    }

    [Fact]
    public async Task Last_RecordsResult()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 21, 9, 30, 0, TimeSpan.Zero));
        SeedChunks(2);
        var p = Pass(() => Context(new RecordingInspect(new TestEmbedder()).Inspect), time: clock);
        Assert.Null(p.Last);

        var result = await Run(p);

        Assert.Same(result, p.Last);
        Assert.Equal(clock.GetUtcNow(), result.At);
        Assert.Equal("done", result.Outcome);
        Assert.Equal(0, result.SessionsIngested);
        Assert.Equal(new EmbedRun(2, 0, 0, new EmbedGateResult(EmbedState.Ready, "ready"), EmbedStopReason.Done), result.Embed);
    }

    [Fact]
    public async Task Dispose_CancelsARunningPass()
    {
        Home.Place("decision", _h.Cwd, "dispose-1");
        using var gate = new ManualResetEventSlim(false);
        var adapter = new ControlAdapter(Claude()) { DetectGate = gate };
        var inspect = new RecordingInspect(new TestEmbedder());
        var p = Pass(() => Context(inspect.Inspect, new ITranscriptAdapter[] { adapter }));

        p.Kick();
        Assert.True(adapter.DetectEntered.Wait(Wait));
        p.Dispose();
        gate.Set();
        await p.WaitIdleAsync().WaitAsync(Wait);

        Assert.Equal("cancelled", p.Last!.Outcome);
        Assert.Equal(0, p.Last.SessionsIngested);
        Assert.Null(p.Last.Embed);
        Assert.Empty(inspect.Threads);
        Assert.Equal(0, Sessions());

        // A disposed pass never starts again.
        var before = p.Last;
        p.Kick();
        await p.WaitIdleAsync().WaitAsync(Wait);
        Assert.Same(before, p.Last);
    }

    [Fact]
    public async Task Dispose_DuringEmbedding_StopsBetweenBatches()
    {
        SeedChunks(40);
        using var gate = new ManualResetEventSlim(false);
        var e = new TestEmbedder { Gate = gate };
        var p = Pass(() => Context(new RecordingInspect(e).Inspect));

        p.Kick();
        Assert.True(e.Entered.Wait(Wait)); // the probe
        p.Dispose();
        gate.Set();
        await p.WaitIdleAsync().WaitAsync(Wait);

        Assert.Equal(EmbedStopReason.Cancelled, p.Last!.Embed!.StoppedBy);
        Assert.True(p.Last.Embed.Embedded < 40);
        Assert.True(e.Disposed);
    }

    [Fact]
    public void TranscriptAdapters_All_IsClaudeCodeOnly()
    {
        var all = TranscriptAdapters.All();
        var only = Assert.Single(all);
        Assert.IsType<ClaudeCodeAdapter>(only);
        Assert.Equal("claude-code", only.Host);
    }
}
