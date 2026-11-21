using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using TotalRecall.Infrastructure.Transcripts;
using TotalRecall.TestSupport.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

/// <summary>
/// <see cref="TranscriptBackfill"/>: the loop around the ingester (order, duplicate ids, the counts, failure, cancellation).
/// In the process-environment collection because one test changes the home directory variables and the failing-source
/// test swaps <see cref="Console.Error"/>.
/// </summary>
[Collection("process-environment")]
public sealed class TranscriptBackfillTests : IDisposable
{
    private const string Host = IngestHarness.Host;

    private readonly IngestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private TranscriptTestDb Db => _h.Db;
    private ClaudeFixtureHome Home => _h.Home;

    private TranscriptBackfill Backfill(ITranscriptAdapter? adapter = null) =>
        new(Db.Store, adapter ?? _h.Adapter(), resolver: null, userHome: _h.UserHome);

    private static void AssertAllZero(BackfillSummary s)
    {
        Assert.Equal(0, s.Seen + s.New + s.Updated + s.Unchanged + s.Excluded + s.Empty + s.Skipped + s.Changed + s.Retry + s.Failed);
        Assert.Equal(0, s.ChunksNew + s.ChunksChanged + s.ChunksUnchanged + s.ChunksRemoved + s.SkippedLines);
        Assert.Empty(s.ChangedSessions);
        Assert.False(s.Cancelled);
    }

    [Fact]
    public void NoProjectsDir_ZeroSummary()
    {
        var adapter = new HookAdapter(new ClaudeCodeAdapter(Path.Combine(Home.Root, "no-such-dir")));

        var summary = Backfill(adapter).Run(CancellationToken.None);

        AssertAllZero(summary);
        Assert.Equal(1, adapter.DetectCalls);
        Assert.Equal(0, adapter.EnumerateCalls);
        Assert.Equal(0, _h.Sessions(Db));
    }

    [Fact]
    public void NewestFirst_ProgressPerSession()
    {
        var oldest = Home.Place("decision", _h.Cwd, "order-oldest");
        var newest = Home.Place("decision", _h.Cwd, "order-newest");
        var middle = Home.Place("decision", _h.Cwd, "order-middle");
        Home.SetMtime(oldest, Home.BaseTime.AddDays(1));
        Home.SetMtime(newest, Home.BaseTime.AddDays(3));
        Home.SetMtime(middle, Home.BaseTime.AddDays(2));
        var order = new List<string>();
        var adapter = _h.Adapter(s => order.Add(s.HostSessionId), everyTime: true);
        var seen = new List<BackfillProgress>();

        var summary = Backfill(adapter).Run(CancellationToken.None, seen.Add);

        Assert.Equal(new[] { "order-newest", "order-middle", "order-oldest" }, order);
        Assert.Equal(new[] { new BackfillProgress(1, 3), new BackfillProgress(2, 3), new BackfillProgress(3, 3) }, seen);
        Assert.Equal(3, summary.Seen);
        Assert.Equal(3, summary.New);
        Assert.Equal(_h.Chunks(Db), summary.ChunksNew);
        Assert.False(summary.Cancelled);
        Assert.Equal(3, _h.Sessions(Db));
    }

    [Fact]
    public void DuplicateId_StoredSourcePathWins_ElseNewest_LoserSkipped()
    {
        var older = Home.Place("decision", Path.Combine(Home.Root, "work", "one"), "dup-1");
        var newer = Home.Place("decision", Path.Combine(Home.Root, "work", "two"), "dup-1");
        Home.SetMtime(older, Home.BaseTime.AddDays(1));
        Home.SetMtime(newer, Home.BaseTime.AddDays(2));

        // Nothing stored yet: the newest file wins.
        var first = Backfill().Run(CancellationToken.None);

        Assert.Equal(2, first.Seen);
        Assert.Equal(1, first.New);
        Assert.Equal(1, first.Skipped);
        Assert.Equal(newer.Path, Db.Text("SELECT source_path FROM tx_session WHERE host_session_id = 'dup-1'"));

        // The other file is now the newest, but the stored path still wins.
        Home.SetMtime(older, Home.BaseTime.AddDays(5));
        var second = Backfill().Run(CancellationToken.None);

        Assert.Equal(2, second.Seen);
        Assert.Equal(1, second.Unchanged);
        Assert.Equal(1, second.Skipped);
        Assert.Equal(0, second.Updated + second.Changed + second.New);
        Assert.Equal(newer.Path, Db.Text("SELECT source_path FROM tx_session WHERE host_session_id = 'dup-1'"));
        Assert.Equal(1, _h.Sessions(Db));
    }

    [Fact]
    public void BadNames_CountedSkippedAndSeen()
    {
        Home.Place("decision", _h.Cwd, "good-1");
        var dir = Path.Combine(Home.ProjectsDir, ClaudeFixtureHome.EncodeDir(_h.Cwd));
        File.WriteAllText(Path.Combine(dir, "not a valid id.jsonl"), "{}\n");
        File.WriteAllText(Path.Combine(dir, "also#bad.jsonl"), "{}\n");

        var summary = Backfill().Run(CancellationToken.None);

        Assert.Equal(3, summary.Seen);
        Assert.Equal(2, summary.Skipped);
        Assert.Equal(1, summary.New);
        Assert.Equal(1, _h.Sessions(Db));
    }

    [Fact]
    public void OneFailingSource_CountedFailed_OthersStored()
    {
        Home.Place("decision", _h.Cwd, "ok-1");
        Home.Place("decision", _h.Cwd, "boom-1");
        Home.Place("decision", _h.Cwd, "ok-2");
        var adapter = _h.Adapter(s =>
        {
            if (s.HostSessionId == "boom-1") throw new InvalidOperationException("planted failure");
        }, everyTime: true);

        BackfillSummary summary;
        string log;
        var original = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        var progress = new List<BackfillProgress>();
        try { summary = Backfill(adapter).Run(CancellationToken.None, progress.Add); }
        finally { Console.SetError(original); }
        log = captured.ToString();

        Assert.Equal(3, summary.Seen);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(2, summary.New);
        Assert.False(summary.Cancelled);
        Assert.Equal(2, _h.Sessions(Db));
        Assert.Equal(0, Db.Scalar("SELECT count(*) FROM tx_session WHERE host_session_id = 'boom-1'"));
        Assert.Contains("[total-recall] transcripts backfill: ", log);
        Assert.Contains("boom-1.jsonl", log);
        Assert.Contains("planted failure", log);
        // The failed session is reported like the others.
        Assert.Equal(new[] { new BackfillProgress(1, 3), new BackfillProgress(2, 3), new BackfillProgress(3, 3) }, progress);
    }

    [Fact]
    public void Cancelled_StopsAfterCurrentSession()
    {
        var a = Home.Place("decision", _h.Cwd, "cancel-a");
        var b = Home.Place("decision", _h.Cwd, "cancel-b");
        Home.SetMtime(a, Home.BaseTime.AddDays(2));
        Home.SetMtime(b, Home.BaseTime.AddDays(1));
        using var cts = new CancellationTokenSource();
        var calls = 0;

        var summary = Backfill().Run(cts.Token, _ =>
        {
            calls++;
            cts.Cancel();
        });

        Assert.True(summary.Cancelled);
        Assert.Equal(1, calls);
        Assert.Equal(1, summary.New);
        Assert.Equal(1, _h.Sessions(Db));
        Assert.Equal(1, Db.Scalar("SELECT count(*) FROM tx_session WHERE host_session_id = 'cancel-a'"));
    }

    [Fact]
    public void Cancelled_DuringReadRecords_IsCancelledNotFailed()
    {
        var a = Home.Place("decision", _h.Cwd, "cancel-read-a");
        var b = Home.Place("decision", _h.Cwd, "cancel-read-b");
        Home.SetMtime(a, Home.BaseTime.AddDays(2));
        Home.SetMtime(b, Home.BaseTime.AddDays(1));
        using var cts = new CancellationTokenSource();
        var adapter = _h.Adapter(_ => cts.Cancel());   // the first read starts, then the token is cancelled
        var progress = new List<BackfillProgress>();

        var summary = Backfill(adapter).Run(cts.Token, progress.Add);

        Assert.True(summary.Cancelled);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(0, summary.New);
        Assert.Equal(1, adapter.ReadRecordsCalls);
        Assert.Empty(progress);
        Assert.Equal(0, _h.Sessions(Db));
    }

    [Fact]
    public void ChangedSessions_ListHostAndId()
    {
        var replaced = Home.Place("decision", _h.Cwd, "changed-1");
        var kept = Home.Place("compaction", _h.Cwd, "kept-1");
        var first = Backfill().Run(CancellationToken.None);
        Assert.Equal(2, first.New);
        Assert.Empty(first.ChangedSessions);
        var before = ArchiveSnapshot.Read(Db.Path, includeIds: true);

        Home.Replace(replaced, "error");
        var second = Backfill().Run(CancellationToken.None);

        Assert.Equal(1, second.Changed);
        Assert.Equal(1, second.Unchanged);
        Assert.Equal(new[] { Host + ":changed-1" }, second.ChangedSessions);
        Assert.Equal(0, second.ChunksNew + second.ChunksChanged + second.ChunksRemoved);
        IngestHarness.AssertSame(before, ArchiveSnapshot.Read(Db.Path, includeIds: true));
        Assert.True(File.Exists(kept.Path));
    }

    [NotOnWindowsFact("Windows resolves the user profile folder from the account, not from HOME or USERPROFILE")]
    public void NullUserHome_ResolvesUserProfile_HomeRepoRootIsNull()
    {
        // The home directory is itself a repository (a dotfiles setup), and a worktree cwd under it, a missing
        // directory, is cut back to it: the repository root is the home directory, and only the home rule (which needs
        // the user profile folder, resolved by the default constructor) turns that root into null.
        var fakeHome = IngestHarness.MakeRepo(Path.Combine(Home.Root, "someone"), null);
        var saved = (Home: Environment.GetEnvironmentVariable("HOME"), Profile: Environment.GetEnvironmentVariable("USERPROFILE"));
        Environment.SetEnvironmentVariable("HOME", fakeHome);
        Environment.SetEnvironmentVariable("USERPROFILE", fakeHome);
        try
        {
            Assert.Equal(fakeHome, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            var cwd = Path.Combine(fakeHome, ".claude", "worktrees", Guid.NewGuid().ToString("N"));
            var s = Home.Place("decision", cwd, "home-1");

            var summary = new TranscriptBackfill(Db.Store, new ClaudeCodeAdapter(Home.ProjectsDir)).Run(CancellationToken.None);

            Assert.Equal(1, summary.New);
            Assert.Equal(1, _h.Sessions(Db));
            Assert.Equal("someone", Db.Text("SELECT project_key FROM tx_session WHERE host_session_id = 'home-1'"));
            Assert.Equal(cwd, Db.Text("SELECT cwd FROM tx_session WHERE host_session_id = 'home-1'"));
            Assert.Null(Db.Text("SELECT project_root FROM tx_session WHERE host_session_id = 'home-1'"));
            Assert.True(File.Exists(s.Path));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", saved.Home);
            Environment.SetEnvironmentVariable("USERPROFILE", saved.Profile);
        }
    }
}

/// <summary>A fact that is skipped, with <paramref name="reason"/>, on Windows.</summary>
internal sealed class NotOnWindowsFactAttribute : FactAttribute
{
    public NotOnWindowsFactAttribute(string reason)
    {
        if (OperatingSystem.IsWindows()) Skip = reason;
    }
}
