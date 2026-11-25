using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.FSharp.Core;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Transcripts;
using TotalRecall.TestSupport.Transcripts;
using Xunit;
using Xunit.Abstractions;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

/// <summary>
/// The cost of the sync-on-read (spec 2026-11-24, section 6, sync test 10): a sync that finds nothing new, on an archive of
/// 2,000 sessions built through <see cref="ReadArchiveBuilder"/> plus one real current session file named by a marker,
/// has a median under 100 ms over 7 runs (text only, as <c>transcript_get</c> and <c>transcript_sessions</c> run it,
/// and with an embed plan, as <c>transcript_search</c> runs it); the guess over 5,000 session files, with no marker,
/// takes under one second. The measured values are printed. Real files in temporary directories; the real Claude
/// directory and data directory are never read (explicit adapter, data directory and ancestors).
/// </summary>
public sealed class TranscriptSyncSpeedTests : IDisposable
{
    private const string Host = "claude-code";
    private const int HostPid = 4242;
    private const int Runs = 7;
    private static readonly TimeSpan NothingNewBound = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan GuessBound = TimeSpan.FromSeconds(1);

    private readonly ITestOutputHelper _output;
    private readonly ClaudeFixtureHome _home = new();
    private readonly string _data;

    public TranscriptSyncSpeedTests(ITestOutputHelper output)
    {
        _output = output;
        _data = Path.Combine(_home.Root, "data");
        Directory.CreateDirectory(Path.Combine(_data, "live"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _home.Dispose();
    }

    private static TranscriptsSettings Settings() => new(true, string.Empty, null, 0, false);

    private static Core.Config.EmbeddingConfig LocalEmbedding() => new(
        "bge-small-en-v1.5",
        384,
        FSharpOption<string>.None,
        FSharpOption<string>.None,
        FSharpOption<string>.None,
        FSharpOption<string>.None,
        FSharpOption<string>.None,
        FSharpOption<string>.None,
        FSharpOption<string>.None,
        FSharpOption<string>.None);

    private void Marker(PlacedSession s)
    {
        var marker = new JsonObject
        {
            ["v"] = 1,
            ["host"] = Host,
            ["host_pid"] = HostPid,
            ["session_id"] = s.Id,
            ["transcript_path"] = s.Path,
            ["cwd"] = s.Cwd,
            ["at"] = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        };
        File.WriteAllText(Path.Combine(_data, "live", Host + "-" + HostPid.ToString(CultureInfo.InvariantCulture) + ".json"),
            marker.ToJsonString());
    }

    /// <summary>Runs <paramref name="call"/> <paramref name="count"/> times, timed; returns the median and prints every value.</summary>
    private TimeSpan Median(string what, int count, Action call)
    {
        var times = new List<TimeSpan>();
        for (var i = 0; i < count; i++)
        {
            var sw = Stopwatch.StartNew();
            call();
            sw.Stop();
            times.Add(sw.Elapsed);
        }
        var median = times.OrderBy(t => t).ElementAt(times.Count / 2);
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{what}: median {median.TotalMilliseconds:F1} ms ({string.Join(", ", times.Select(t => t.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)))} ms)"));
        return median;
    }

    [Fact]
    public void Sync_NothingNew_On2000Sessions_MedianUnder100ms()
    {
        using var db = new TranscriptTestDb();
        var build = Stopwatch.StartNew();
        var archive = new ReadArchiveBuilder(db.Store);
        var t0 = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 2_000; i++)
        {
            var started = t0.AddHours(i);
            var s = archive.Session(Host, "s" + i.ToString("D5", CultureInfo.InvariantCulture),
                project: "owner/repo" + (i % 20).ToString("D2", CultureInfo.InvariantCulture),
                title: "Session " + i.ToString(CultureInfo.InvariantCulture), startedAt: started, endedAt: started.AddMinutes(30));
            for (var n = 0; n < 2; n++)
                s.Exchange(n, $"User: question {n} of session {i}\n\nAssistant: a short answer about the build.", ts: started.AddSeconds(n * 30));
            Assert.Equal(IngestOutcomeKind.Applied, s.Commit().Kind);
        }
        build.Stop();

        var current = ReadFixtureHome.Place(_home, Path.Combine(_home.Root, "work", "speed"), "speed-current",
            ReadFixtureHome.Dialogue(20, n => $"Current question {n}", n => $"Current answer {n} about the speed of the sync."));
        Marker(current);
        var service = new TranscriptSyncService(
            () => new SyncContext(new TranscriptLocation(db.Path, IsMainDatabase: false), Settings()),
            new TranscriptSyncOptions(
                Ancestors: () => new[] { HostPid },
                DataDir: _data,
                ProcessStart: _ => null,
                Adapters: () => new ITranscriptAdapter[] { new ClaudeCodeAdapter(_home.ProjectsDir) }));

        // The first sync ingests the current session; every later one finds nothing new.
        var first = service.SyncCurrent(null, CancellationToken.None);
        Assert.True(first.Complete, first.Note);
        Assert.Equal(CurrentKind.Marker, first.Current.Kind);
        Assert.Equal(2_001, db.Store.Status().Sessions);
        var chunks = db.Store.CountEmbedding();
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"archive: 2001 sessions, {chunks.Waiting} waiting chunks, built in {build.Elapsed.TotalSeconds:F1} s"));

        void NothingNew(SyncEmbedPlan? plan)
        {
            var r = service.SyncCurrent(plan, CancellationToken.None);
            Assert.True(r.Complete, r.Note);
            Assert.Equal(CurrentKind.Marker, r.Current.Kind);
            Assert.Equal("speed", r.ProjectKey);
        }

        var textOnly = Median("sync with nothing new, text only (transcript_get, transcript_sessions)", Runs, () => NothingNew(null));

        // As transcript_search runs it: with an embed plan. The current session is embedded first (its 20 chunks, 8 per
        // sync, the last batch filled from the backlog), so the timed syncs find nothing of it waiting while almost all
        // of the 4,000 backlog chunks still wait.
        var embedder = new RecordingEmbedder("local", 384);
        var plan = new SyncEmbedPlan(embedder, Settings(), "sqlite", LocalEmbedding(), () => false);
        for (var i = 0; i < 5 && db.Store.HasWaitingEmbedWork(Host, current.Id); i++)
            service.SyncCurrent(plan, CancellationToken.None);
        Assert.False(db.Store.HasWaitingEmbedWork(Host, current.Id));
        var calls = embedder.Calls.Count;
        var withPlan = Median("sync with nothing new, with an embed plan (transcript_search)", Runs, () => NothingNew(plan));
        Assert.Equal(calls, embedder.Calls.Count); // nothing was embedded by the timed syncs
        var waiting = db.Store.CountEmbedding().Waiting;
        _output.WriteLine($"waiting chunks during the timed syncs with a plan: {waiting}");
        Assert.True(waiting > 3_900, $"only {waiting} chunks wait");

        Assert.True(textOnly < NothingNewBound, $"a sync with nothing new took a median {textOnly.TotalMilliseconds:F1} ms");
        Assert.True(withPlan < NothingNewBound, $"a sync with nothing new and a plan took a median {withPlan.TotalMilliseconds:F1} ms");
    }

    [Fact]
    public void Guess_Over5000SessionFiles_Under1Second()
    {
        // 5,000 small session files in 50 project directories, all written two hours ago except three recent ones.
        var now = DateTimeOffset.UtcNow;
        var line = "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"hello\"},\"timestamp\":\"2026-01-01T00:00:00.000Z\"}\n";
        var paths = new List<string>(5_000);
        for (var d = 0; d < 50; d++)
        {
            var dir = Path.Combine(_home.ProjectsDir, "-work-project" + d.ToString("D2", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dir);
            for (var f = 0; f < 100; f++)
            {
                var path = Path.Combine(dir, "g" + (d * 100 + f).ToString("D5", CultureInfo.InvariantCulture) + ".jsonl");
                File.WriteAllText(path, line);
                File.SetLastWriteTimeUtc(path, now.AddHours(-2).UtcDateTime);
                paths.Add(path);
            }
        }
        File.SetLastWriteTimeUtc(paths[1234], now.AddMinutes(-10).UtcDateTime);
        File.SetLastWriteTimeUtc(paths[4321], now.AddMinutes(-3).UtcDateTime);
        File.SetLastWriteTimeUtc(paths[3777], now.AddMinutes(-1).UtcDateTime);

        // No marker (no ancestors) and no archive: the time is the guess itself.
        var service = new TranscriptSyncService(
            () => new SyncContext(new TranscriptLocation(Path.Combine(_home.Root, "absent", "archive.db"), IsMainDatabase: false), Settings()),
            new TranscriptSyncOptions(
                Ancestors: () => Array.Empty<int>(),
                DataDir: _data,
                ProcessStart: _ => null,
                Adapters: () => new ITranscriptAdapter[] { new ClaudeCodeAdapter(_home.ProjectsDir) }));

        var median = Median("the guess over 5,000 session files", 5, () =>
        {
            var r = service.SyncCurrent(null, CancellationToken.None);
            Assert.Equal(CurrentKind.Guessed, r.Current.Kind);
            Assert.Equal(Host + ":g03777", r.Current.Ref);
        });
        Assert.True(median < GuessBound, $"the guess took a median {median.TotalMilliseconds:F1} ms");
    }
}
