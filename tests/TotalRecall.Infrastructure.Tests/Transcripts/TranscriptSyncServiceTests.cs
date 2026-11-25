using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Tests.TestSupport;
using TotalRecall.Infrastructure.Transcripts;
using TotalRecall.TestSupport.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

/// <summary>
/// <see cref="TranscriptSyncService"/> (spec section 2, the guess, and section 3, steps 1 to 4): find the current session
/// (a marker written by hand under the test's own data directory, an injected ancestor chain) or the bounded guess, open
/// the archive without creating it, and ingest the session's new tail under a budget on the service's clock. A real
/// archive in a temporary directory, fixture session files, explicit adapters (the real Claude directory is never read).
/// In the process-environment collection because one test swaps the console.
/// </summary>
[Collection("process-environment")]
public sealed class TranscriptSyncServiceTests : IDisposable
{
    private const string Host = "claude-code";
    private const int HostPid = 4242;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private readonly IngestHarness _h = new();
    private readonly string _data;
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));

    public TranscriptSyncServiceTests()
    {
        _data = Path.Combine(_h.Home.Root, "data");
        Directory.CreateDirectory(Path.Combine(_data, "live"));
    }

    public void Dispose() => _h.Dispose();

    private TranscriptTestDb Db => _h.Db;
    private ClaudeFixtureHome Home => _h.Home;

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static TranscriptsSettings Settings(bool enabled = true) => new(enabled, string.Empty, null, 0, false);

    private TranscriptLocation Location => new(Db.Path, IsMainDatabase: false);

    private HookAdapter Adapter(Action<SessionSource>? hook = null) => _h.Adapter(hook);

    /// <summary>The service over <paramref name="adapter"/>, the test's data directory, ancestors and clock.</summary>
    private TranscriptSyncService Service(
        ITranscriptAdapter adapter,
        TranscriptSyncOptions? options = null,
        Func<SyncContext?>? context = null,
        TranscriptLocation? location = null)
    {
        var o = options ?? new TranscriptSyncOptions();
        o = o with
        {
            Ancestors = o.Ancestors ?? (() => new[] { HostPid }),
            DataDir = o.DataDir ?? _data,
            ProcessStart = o.ProcessStart ?? (_ => null),
            Adapters = o.Adapters ?? (() => new[] { adapter }),
            Time = o.Time ?? _clock,
        };
        var loc = location ?? Location;
        return new TranscriptSyncService(context ?? (() => new SyncContext(loc, Settings())), o);
    }

    /// <summary>Writes <c>&lt;data&gt;/live/claude-code-&lt;pid&gt;.json</c> naming <paramref name="s"/>.</summary>
    private void Marker(PlacedSession s, string? cwd = null, int pid = HostPid)
    {
        var marker = new JsonObject
        {
            ["v"] = 1,
            ["host"] = Host,
            ["host_pid"] = pid,
            ["session_id"] = s.Id,
            ["transcript_path"] = s.Path,
            ["cwd"] = cwd ?? s.Cwd,
            ["at"] = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        };
        File.WriteAllText(Path.Combine(_data, "live", Host + "-" + pid.ToString(CultureInfo.InvariantCulture) + ".json"),
            marker.ToJsonString());
    }

    private static SyncResult Sync(TranscriptSyncService service) => service.SyncCurrent(null, CancellationToken.None);

    private long Sessions() => _h.Sessions(Db);
    private long Chunks() => _h.Chunks(Db);
    private string AllText() => string.Join("\n", IngestHarness.Contents(Db));

    /// <summary>Appends the compaction fixture's boundary and its summary to <paramref name="s"/>, at T+60 and T+61.</summary>
    private void AppendCompaction(PlacedSession s)
    {
        var lines = Encoding.UTF8.GetString(Home.Render("compaction", s.Cwd, s.Id)).Split('\n');
        string Stamp(int seconds) => Home.BaseTime.AddSeconds(seconds).UtcDateTime
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var boundary = lines.Single(l => l.Contains("compact_boundary", StringComparison.Ordinal));
        var summary = lines.Single(l => l.Contains("zqxsummary", StringComparison.Ordinal));
        Home.AppendLine(s, boundary.Replace(Stamp(20), Stamp(60), StringComparison.Ordinal));
        Home.AppendLine(s, summary.Replace(Stamp(21), Stamp(61), StringComparison.Ordinal));
    }

    /// <summary>Counts the opens and passes through to the real one (create: false, a short busy timeout).</summary>
    private sealed class CountingOpen
    {
        public int Calls;
        public Action? Before { get; init; }

        public TranscriptOpenResult Open(TranscriptLocation location)
        {
            Interlocked.Increment(ref Calls);
            Before?.Invoke();
            return TranscriptStore.Open(location, create: false, busyTimeoutMs: 500);
        }
    }

    /// <summary>Wraps an adapter: runs an action inside every <see cref="ReadHead"/> before the read.</summary>
    private sealed class HeadHookAdapter(ITranscriptAdapter inner, Action<SessionSource> onHead) : ITranscriptAdapter
    {
        public string Host => inner.Host;
        public string Root => inner.Root;
        public bool Detect() => inner.Detect();
        public SessionEnumeration EnumerateSessions() => inner.EnumerateSessions();

        public SessionHead ReadHead(SessionSource source)
        {
            onHead(source);
            return inner.ReadHead(source);
        }

        public bool IsLineStart(SessionSource source, long offset) => inner.IsLineStart(source, offset);

        public IEnumerable<TranscriptRecord> ReadRecords(SessionSource source, long startOffset, ReadStats stats, CancellationToken ct) =>
            inner.ReadRecords(source, startOffset, stats, ct);
    }

    // ------------------------------------------------------------------
    // The tail
    // ------------------------------------------------------------------

    [Fact]
    public void Sync_AppendedTextAfterTheLastPass_IsStored()
    {
        var s = Home.Place("decision", _h.Cwd, "append-1");
        Assert.Equal(SessionIngestStatus.New, _h.Ingest(Db, s).Status);   // the last pass
        Home.AppendExchange(s, "zqxbefore1 what about the index?", "zqxbefore2 the index is fine.", 50);
        AppendCompaction(s);
        Home.AppendExchange(s, "zqxafter1 now add the column.", "zqxafter2 the column is added.", 100);
        Marker(s);

        var result = Sync(Service(Adapter()));

        Assert.Equal(CurrentKind.Marker, result.Current.Kind);
        Assert.Equal(Host + ":append-1", result.Current.Ref);
        Assert.True(result.Complete);
        Assert.Null(result.Note);
        var text = AllText();
        Assert.Contains("zqxbefore1", text);
        Assert.Contains("zqxbefore2", text);
        Assert.Contains("zqxsummary", text);
        Assert.Contains("zqxafter1", text);
        Assert.Contains("zqxafter2", text);
    }

    [Fact]
    public void Sync_GrownAcrossTwoSyncs_EqualsOneIngest()
    {
        var s = Home.Place("decision", _h.Cwd, "grown-1");
        Marker(s);
        var service = Service(Adapter());

        var first = Sync(service);
        Assert.True(first.Complete);
        Home.AppendExchange(s, "zqxgrow1 and the tests?", "zqxgrow2 they pass.", 50);
        AppendCompaction(s);
        Home.AppendExchange(s, "zqxgrow3 ship it.", "zqxgrow4 shipped.", 100);
        var second = Sync(service);
        Assert.True(second.Complete);
        Assert.Null(second.Note);

        IngestHarness.AssertSame(_h.Once(s), ArchiveSnapshot.Read(Db.Path), "two syncs against one ingest");
    }

    [Fact]
    public void Sync_NothingNew_IsUnchanged()
    {
        var s = Home.Place("decision", _h.Cwd, "same-1");
        Marker(s);
        var adapter = Adapter();
        var service = Service(adapter);
        Assert.True(Sync(service).Complete);
        var before = ArchiveSnapshot.Read(Db.Path, includeIds: true);
        var heads = adapter.ReadHeadCalls;
        var reads = adapter.ReadRecordsCalls;

        var result = Sync(service);

        Assert.True(result.Complete);
        Assert.Null(result.Note);
        Assert.Equal("proj", result.ProjectKey);
        Assert.Equal(heads, adapter.ReadHeadCalls);
        Assert.Equal(reads, adapter.ReadRecordsCalls);
        IngestHarness.AssertSame(before, ArchiveSnapshot.Read(Db.Path, includeIds: true), "a sync with nothing new");
    }

    // ------------------------------------------------------------------
    // Exclusions
    // ------------------------------------------------------------------

    [Fact]
    public void Sync_ForgottenById_FileNotOpened()
    {
        var s = Home.Place("decision", _h.Cwd, "forgot-1");
        Db.Store.ForgetSession(Host, s.Id);
        Marker(s);
        var adapter = Adapter();

        var result = Sync(Service(adapter));

        Assert.Equal(CurrentKind.Marker, result.Current.Kind);
        Assert.True(result.Complete);
        Assert.Null(result.ProjectKey);
        Assert.Equal(0, adapter.ReadHeadCalls);
        Assert.Equal(0, adapter.IsLineStartCalls);
        Assert.Equal(0, adapter.ReadRecordsCalls);
        Assert.Equal(0, Sessions());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sync_ForgottenByProjectKey_AndByRoot_StoresNothing(bool byRoot)
    {
        var repo = IngestHarness.MakeRepo(Path.Combine(Home.Root, "repos", "proj"), "https://github.com/Owner/Repo.git");
        var sub = Path.Combine(repo, "sub");
        Directory.CreateDirectory(sub);
        var s = Home.Place("decision", sub, "fproj-1");
        Assert.Equal(SessionIngestStatus.New, _h.Ingest(Db, s).Status);
        Db.Store.ForgetProject("owner/repo");

        var target = s;
        if (byRoot)
        {
            // Re-keyed: only the stored root keeps a new session under the repository out.
            IngestHarness.SetRemote(repo, "https://github.com/Other/Thing.git");
            target = Home.Place("decision", sub, "fproj-2");
        }
        Marker(target);
        var adapter = Adapter();

        var result = Sync(Service(adapter));

        Assert.True(result.Complete);
        Assert.Null(result.ProjectKey);
        Assert.Equal(0, adapter.ReadRecordsCalls);
        Assert.Equal(0, Sessions());
        Assert.Equal(0, Chunks());
    }

    [Fact]
    public void Sync_GlobalCutoff_DropsOlderRecords()
    {
        var s = Home.Place("decision", _h.Cwd, "cutoff-1");
        Db.Store.ForgetAll(Home.BaseTime.AddSeconds(100));
        Home.AppendExchange(s, "zqxlate1 after the cutoff", "zqxlate2 kept", 200);
        Marker(s);

        var result = Sync(Service(Adapter()));

        Assert.True(result.Complete);
        var text = AllText();
        Assert.Contains("zqxlate1", text);
        Assert.Contains("zqxlate2", text);
        Assert.DoesNotContain("schema migration", text);
        Assert.DoesNotContain("zqxtoolresult", text);
    }

    // ------------------------------------------------------------------
    // The guess
    // ------------------------------------------------------------------

    [Fact]
    public void Sync_Guess_NewestWithin30Minutes_ThenNone()
    {
        var now = _clock.GetUtcNow();
        var a = Home.Place("decision", _h.Cwd, "guess-a");
        var b = Home.Place("decision", _h.Cwd, "guess-b");
        Home.SetMtime(a, now.AddMinutes(-10));
        Home.SetMtime(b, now.AddMinutes(-31));
        var adapter = Adapter();

        var result = Sync(Service(adapter));

        Assert.Equal(CurrentKind.Guessed, result.Current.Kind);
        Assert.Equal(Host + ":guess-a", result.Current.Ref);
        Assert.True(result.Complete);
        Assert.Null(result.ProjectKey);
        Assert.Equal("guess-a", Db.Text("SELECT host_session_id FROM tx_session"));

        // Two files in the window at the same time: the ordinal path breaks the tie.
        var d = Home.Place("decision", _h.Cwd, "guess-d");
        var c = Home.Place("decision", _h.Cwd, "guess-c");
        Home.SetMtime(c, now.AddMinutes(-5));
        Home.SetMtime(d, now.AddMinutes(-5));
        Assert.Equal(Host + ":guess-c", Sync(Service(Adapter())).Current.Ref);

        // A newer file wins over the path.
        var e = Home.Place("decision", _h.Cwd, "guess-e");
        Home.SetMtime(e, now.AddMinutes(-4));
        var newest = Sync(Service(Adapter()));
        Assert.Equal(Host + ":guess-e", newest.Current.Ref);
        Assert.Null(newest.ProjectKey);

        // Forty minutes later every file is older than the window.
        _clock.Advance(TimeSpan.FromMinutes(40));
        var none = Sync(Service(Adapter()));
        Assert.Equal(CurrentKind.None, none.Current.Kind);
        Assert.Null(none.Current.Ref);
        Assert.True(none.Complete);
        Assert.Null(none.ProjectKey);
    }

    [Fact]
    public void Sync_NoMarkerNoRecentFile_IsNone()
    {
        var old = Home.Place("decision", _h.Cwd, "old-1");
        Home.SetMtime(old, _clock.GetUtcNow().AddMinutes(-31));
        var adapter = Adapter();

        var result = Sync(Service(adapter));

        Assert.Equal(CurrentKind.None, result.Current.Kind);
        Assert.True(result.Complete);
        Assert.Null(result.Note);
        Assert.Null(result.ProjectKey);
        Assert.Equal(0, adapter.ReadHeadCalls);
        Assert.Equal(0, adapter.ReadRecordsCalls);
        Assert.Equal(0, Sessions());
    }

    // ------------------------------------------------------------------
    // Budgets and failures
    // ------------------------------------------------------------------

    [Fact]
    public void Sync_IngestBudgetSpent_IsIncompleteWithNote()
    {
        var s = Home.Place("decision", _h.Cwd, "slow-1");
        Marker(s);
        // The service's clock passes the 2-second ingest budget once the read has started.
        var adapter = Adapter(_ => _clock.Advance(TimeSpan.FromSeconds(3)));

        var result = Sync(Service(adapter));

        Assert.Equal(CurrentKind.Marker, result.Current.Kind);
        Assert.False(result.Complete);
        Assert.NotNull(result.Note);
        Assert.Contains("ran out of time", result.Note);
        Assert.Equal(1, adapter.ReadRecordsCalls);
        Assert.Equal(0, Sessions());
        Assert.Equal(0, Chunks());
    }

    [Fact]
    public void Sync_ChangedSource_SetsNote()
    {
        var s = Home.Place("decision", _h.Cwd, "changed-1");
        Marker(s);
        var service = Service(Adapter());
        Assert.True(Sync(service).Complete);
        var before = ArchiveSnapshot.Read(Db.Path, includeIds: true);

        Home.Replace(s, "compaction");
        var result = Sync(service);

        Assert.False(result.Complete);
        Assert.NotNull(result.Note);
        Assert.Contains("replaced", result.Note);
        Assert.Equal("proj", result.ProjectKey);
        Assert.Equal(before.Chunks, ArchiveSnapshot.Read(Db.Path, includeIds: true).Chunks);
    }

    [Theory]
    [InlineData("head")]
    [InlineData("records")]
    public void Sync_FileDeletedBetweenStatAndRead_DoesNotThrow(string when)
    {
        var s = Home.Place("decision", _h.Cwd, "gone-" + when);
        Marker(s);
        ITranscriptAdapter adapter = when == "head"
            ? new HeadHookAdapter(_h.Adapter(), src => File.Delete(src.Path))
            : _h.Adapter(src => File.Delete(src.Path));

        var result = Sync(Service(adapter));

        Assert.Equal(CurrentKind.Marker, result.Current.Kind);
        Assert.False(result.Complete);
        Assert.NotNull(result.Note);
        Assert.Null(result.ProjectKey);
        Assert.Equal(0, Sessions());
    }

    [Fact]
    public void Sync_ArchiveAbsent_ReturnsCurrentAndComplete()
    {
        var s = Home.Place("decision", _h.Cwd, "absent-1");
        Marker(s);
        var missing = Path.Combine(Home.Root, "no-archive", "archive.db");
        Directory.CreateDirectory(Path.GetDirectoryName(missing)!);

        var result = Sync(Service(Adapter(), location: new TranscriptLocation(missing, IsMainDatabase: false)));

        Assert.Equal(CurrentKind.Marker, result.Current.Kind);
        Assert.Equal(Host + ":absent-1", result.Current.Ref);
        Assert.True(result.Complete);
        Assert.Null(result.Note);
        Assert.Equal("proj", result.ProjectKey);   // no row: derived from the head of the file
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public void Sync_StoreThrowsOnOpen_IsLoggedNotThrown()
    {
        var s = Home.Place("decision", _h.Cwd, "throws-1");
        Marker(s);
        var service = Service(Adapter(), new TranscriptSyncOptions(
            Open: _ => throw new InvalidOperationException("zqxopenboom")));

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var origOut = Console.Out;
        var origErr = Console.Error;
        SyncResult result;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            result = Sync(service);
        }
        finally
        {
            Console.SetOut(origOut);
            Console.SetError(origErr);
        }

        Assert.Equal(CurrentKind.Marker, result.Current.Kind);
        Assert.False(result.Complete);
        Assert.NotNull(result.Note);
        Assert.Contains("[total-recall]", stderr.ToString());
        Assert.Contains("zqxopenboom", stderr.ToString());
        Assert.Equal(string.Empty, stdout.ToString());
    }

    [Fact]
    public void Sync_BusyArchive_EndsInAboutTheTimeout()
    {
        var s = Home.Place("decision", _h.Cwd, "busy-1");
        Marker(s);
        var service = Service(Adapter());

        using var holder = Db.Connect();
        using (var begin = holder.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE";
            begin.ExecuteNonQuery();
        }

        SyncResult result;
        var sw = Stopwatch.StartNew();
        try
        {
            result = Sync(service);
        }
        finally
        {
            sw.Stop();
            using var rollback = holder.CreateCommand();
            rollback.CommandText = "ROLLBACK";
            rollback.ExecuteNonQuery();
        }

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"the sync took {sw.ElapsedMilliseconds} ms against a held lock");
        Assert.Equal(CurrentKind.Marker, result.Current.Kind);
        Assert.False(result.Complete);
        Assert.NotNull(result.Note);
        Assert.Contains("could not be written", result.Note);
        Assert.Equal(0, Sessions());
    }

    [Fact]
    public async Task Sync_TwoConcurrentCalls_Serialize_SecondSeesUnchanged()
    {
        // 1. The second call waits for the first, then finds nothing new.
        var s = Home.Place("decision", _h.Cwd, "twice-1");
        Marker(s);
        using var entered = new ManualResetEventSlim(false);
        using var gate = new ManualResetEventSlim(false);
        var adapter = Adapter(_ => { entered.Set(); gate.Wait(Wait); });
        var service = Service(adapter, new TranscriptSyncOptions(IngestBudget: Wait));

        var first = Task.Run(() => Sync(service));
        Assert.True(entered.Wait(Wait));
        var second = Task.Run(() => Sync(service));
        Assert.NotSame(second, await Task.WhenAny(second, Task.Delay(300)));   // still waiting for the first
        gate.Set();
        var r1 = await first.WaitAsync(Wait);
        var r2 = await second.WaitAsync(Wait);

        Assert.True(r1.Complete);
        Assert.True(r2.Complete);
        Assert.Null(r2.Note);
        Assert.Equal(CurrentKind.Marker, r2.Current.Kind);
        Assert.Equal(1, adapter.ReadRecordsCalls);
        Assert.Equal(1, Sessions());

        // 2. A wait that outlasts the budget returns with a note.
        var s2 = Home.Place("decision", _h.Cwd, "twice-2");
        Marker(s2);
        using var entered2 = new ManualResetEventSlim(false);
        using var gate2 = new ManualResetEventSlim(false);
        var service2 = Service(Adapter(_ => { entered2.Set(); gate2.Wait(Wait); }),
            new TranscriptSyncOptions(IngestBudget: TimeSpan.FromMilliseconds(200)));
        var blocked = Task.Run(() => Sync(service2));
        Assert.True(entered2.Wait(Wait));
        var late = await Task.Run(() => Sync(service2)).WaitAsync(Wait);
        Assert.False(late.Complete);
        Assert.NotNull(late.Note);
        Assert.Contains("ran out of time", late.Note);
        Assert.Equal(CurrentKind.Marker, late.Current.Kind);
        gate2.Set();
        await blocked.WaitAsync(Wait);

        // 3. The wait honours the caller's token.
        var s3 = Home.Place("decision", _h.Cwd, "twice-3");
        Marker(s3);
        using var entered3 = new ManualResetEventSlim(false);
        using var gate3 = new ManualResetEventSlim(false);
        var service3 = Service(Adapter(_ => { entered3.Set(); gate3.Wait(Wait); }), new TranscriptSyncOptions(IngestBudget: Wait));
        var holding = Task.Run(() => Sync(service3));
        Assert.True(entered3.Wait(Wait));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() => service3.SyncCurrent(null, cts.Token)).WaitAsync(Wait));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), "the wait did not end with the token");
        gate3.Set();
        await holding.WaitAsync(Wait);
    }

    [Fact]
    public void Sync_NoStepAfterTheTotalDeadline()
    {
        // The deadline passes while the marker is checked: the archive is never opened.
        var s = Home.Place("decision", _h.Cwd, "late-1");
        Marker(s);
        var adapter = Adapter();
        var open = new CountingOpen();
        var result = Sync(Service(adapter, new TranscriptSyncOptions(
            Open: open.Open,
            ProcessStart: _ => { _clock.Advance(TimeSpan.FromSeconds(7)); return null; })));

        Assert.Equal(CurrentKind.Marker, result.Current.Kind);
        Assert.False(result.Complete);
        Assert.NotNull(result.Note);
        Assert.Contains("ran out of time", result.Note);
        Assert.Equal(0, open.Calls);
        Assert.Equal(0, adapter.ReadRecordsCalls);

        // The deadline passes while the archive is opened: nothing is ingested.
        var adapter2 = Adapter();
        var slowOpen = new CountingOpen { Before = () => _clock.Advance(TimeSpan.FromSeconds(7)) };
        var result2 = Sync(Service(adapter2, new TranscriptSyncOptions(Open: slowOpen.Open)));

        Assert.False(result2.Complete);
        Assert.Contains("ran out of time", result2.Note);
        Assert.Equal(1, slowOpen.Calls);
        Assert.Equal(0, adapter2.ReadRecordsCalls);
        Assert.Equal(0, Sessions());
    }

    [Fact]
    public void Sync_SettingsDisabled_StoresNothing()
    {
        var s = Home.Place("decision", _h.Cwd, "off-1");
        Marker(s);
        var adapter = Adapter();
        var open = new CountingOpen();

        var off = Sync(Service(adapter, new TranscriptSyncOptions(Open: open.Open),
            context: () => new SyncContext(Location, Settings(enabled: false))));
        var none = Sync(Service(adapter, new TranscriptSyncOptions(Open: open.Open), context: () => null));

        foreach (var result in new[] { off, none })
        {
            Assert.Equal(CurrentKind.Marker, result.Current.Kind);
            Assert.True(result.Complete);
            Assert.Null(result.Note);
        }
        Assert.Equal(0, open.Calls);
        Assert.Equal(0, adapter.ReadHeadCalls);
        Assert.Equal(0, adapter.ReadRecordsCalls);
        Assert.Equal(0, Sessions());
    }

    // ------------------------------------------------------------------
    // The project key
    // ------------------------------------------------------------------

    [Fact]
    public void Sync_ProjectKey_IsTheStoredKey_NotTheMarkerCwd()
    {
        // The marker's cwd is another directory (a `cd`): the key is the one stored from the file's first cwd.
        var elsewhere = Path.Combine(Home.Root, "elsewhere", "other");
        var s = Home.Place("decision", _h.Cwd, "key-1");
        Marker(s, cwd: elsewhere);

        var stored = Sync(Service(Adapter()));

        Assert.Equal(elsewhere, stored.Current.Cwd);
        Assert.Equal("proj", Db.Text("SELECT project_key FROM tx_session WHERE host_session_id = 'key-1'"));
        Assert.Equal("proj", stored.ProjectKey);

        // The stored key, not the head's: a repository whose directory is gone derives a path key from the head now.
        var repo = IngestHarness.MakeRepo(Path.Combine(Home.Root, "repos", "app"), "https://github.com/Owner/App.git");
        var r = Home.Place("decision", repo, "key-remote");
        Marker(r, cwd: elsewhere);
        Assert.Equal("owner/app", Sync(Service(Adapter())).ProjectKey);
        Directory.Delete(repo, recursive: true);
        Assert.Equal("app", ProjectIdentityRule.Derive(repo, "x", new TotalRecall.Infrastructure.Memory.ProjectResolver(), _h.UserHome).Key);
        var afterDelete = Sync(Service(Adapter()));
        Assert.True(afterDelete.Complete);
        Assert.Equal("owner/app", afterDelete.ProjectKey);

        // No row (no archive): derived from the head of the file.
        var absent = Path.Combine(Home.Root, "no-archive", "archive.db");
        Directory.CreateDirectory(Path.GetDirectoryName(absent)!);
        Marker(s, cwd: elsewhere);
        var derived = Sync(Service(Adapter(), location: new TranscriptLocation(absent, IsMainDatabase: false)));
        Assert.Equal("proj", derived.ProjectKey);

        // The head cannot be read (no complete first line): no key.
        var partial = Home.Place("decision", _h.Cwd, "key-partial");
        Home.Write(partial, Encoding.UTF8.GetBytes("{\"type\":\"user\""));
        Marker(partial, cwd: elsewhere);
        var unread = Sync(Service(Adapter()));
        Assert.Equal(CurrentKind.Marker, unread.Current.Kind);
        Assert.False(unread.Complete);
        Assert.Null(unread.ProjectKey);
    }
}
