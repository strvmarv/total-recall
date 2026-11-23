using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using TotalRecall.Infrastructure.Transcripts;
using TotalRecall.TestSupport.Transcripts;
using Xunit;
using Xunit.Abstractions;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

/// <summary>
/// The speed of the archive reads (read spec section 6, test 10): a synthetic archive of 2,000 sessions and 100,000
/// chunks, built with one <see cref="ITranscriptStore.ApplyIngest"/> per session through <see cref="ReadArchiveBuilder"/>;
/// the median of five <see cref="TranscriptReadService.Get"/> windows (by id and by range) and of five
/// <see cref="TranscriptReadService.Sessions"/> calls with filters each answers in under one second. The measured
/// values are printed.
/// </summary>
public sealed class TranscriptReadSpeedTests : IDisposable
{
    private const int SessionCount = 2_000;
    private const int ExchangesPerSession = 50;
    private const int Projects = 20;
    private static readonly DateTimeOffset T0 = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(1);

    private readonly ITestOutputHelper _output;
    private readonly TranscriptTestDb _db = new();

    public TranscriptReadSpeedTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => _db.Dispose();

    private static string Id(int i) => "s" + i.ToString("D5", CultureInfo.InvariantCulture);

    private static string Project(int i) => "owner/repo" + (i % Projects).ToString("D2", CultureInfo.InvariantCulture);

    /// <summary>Session <c>i</c> ends <c>i</c> hours after <see cref="T0"/>; every tenth is automated.</summary>
    private static DateTimeOffset Ended(int i) => T0.AddHours(i);

    private void BuildArchive()
    {
        var archive = new ReadArchiveBuilder(_db.Store);
        for (var i = 0; i < SessionCount; i++)
        {
            var started = Ended(i).AddMinutes(-30);
            var s = archive.Session("claude-code", Id(i), project: Project(i), kind: i % 10 == 0 ? "automated" : "interactive",
                title: "Session " + i.ToString(CultureInfo.InvariantCulture), startedAt: started, endedAt: Ended(i));
            for (var n = 0; n < ExchangesPerSession; n++)
                s.Exchange(n, "User: question " + n.ToString(CultureInfo.InvariantCulture) + " of session " + i.ToString(CultureInfo.InvariantCulture)
                    + "\n\nAssistant: a short answer about the build.", ts: started.AddSeconds(n * 30));
            Assert.Equal(IngestOutcomeKind.Applied, s.Commit().Kind);
        }
    }

    private long ChunkId(int session, int exchange)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT c.id FROM tx_chunk c JOIN tx_session s ON s.id = c.session_id " +
            "WHERE s.host_session_id = $h AND c.exchange_start = $e AND c.part = 0";
        cmd.Parameters.AddWithValue("$h", Id(session));
        cmd.Parameters.AddWithValue("$e", exchange);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private long Count(string table)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM " + table;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Runs each call once, timed; returns the median and prints every value.</summary>
    private TimeSpan Median(string what, IReadOnlyList<Action> calls)
    {
        var times = new List<TimeSpan>();
        foreach (var call in calls)
        {
            var sw = Stopwatch.StartNew();
            call();
            sw.Stop();
            times.Add(sw.Elapsed);
        }
        var sorted = times.OrderBy(t => t).ToArray();
        var median = sorted[sorted.Length / 2];
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{what}: median {median.TotalMilliseconds:F1} ms ({string.Join(", ", times.Select(t => t.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)))} ms)"));
        return median;
    }

    [Fact]
    public void Reads_On100000Chunks_AnswerUnderOneSecond()
    {
        var build = Stopwatch.StartNew();
        BuildArchive();
        build.Stop();
        Assert.Equal(SessionCount, Count("tx_session"));
        Assert.Equal(SessionCount * ExchangesPerSession, Count("tx_chunk"));
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"archive: {SessionCount} sessions, {SessionCount * ExchangesPerSession} chunks, built in {build.Elapsed.TotalSeconds:F1} s"));

        var service = new TranscriptReadService(
            () => new TranscriptReadContext(new TranscriptLocation(_db.Path, IsMainDatabase: false)));
        var picks = new[] { 7, 513, 1009, 1500, 1998 };
        var ids = picks.ToDictionary(i => i, i => ChunkId(i, 25));

        var byId = picks.Select(i => (Action)(() =>
        {
            var r = service.Get(new TranscriptGetRequest(ids[i], null, null, null, 3, 2000), default);
            Assert.Null(r.Error);
            Assert.Equal(Enumerable.Range(22, 7).ToArray(), r.Exchanges.Select(e => e.N).ToArray());
        })).ToArray();

        var byRange = picks.Select(i => (Action)(() =>
        {
            var r = service.Get(new TranscriptGetRequest(null, "claude-code:" + Id(i), 10, 19, 2, 20000), default);
            Assert.Null(r.Error);
            Assert.Equal(Enumerable.Range(8, 14).ToArray(), r.Exchanges.Select(e => e.N).ToArray());
            Assert.False(r.Truncated);
        })).ToArray();

        var lists = new Action[]
        {
            () => Assert.Equal(100, service.Sessions(new TranscriptSessionsRequest(Project(3), null, null, 100, false), default).Sessions.Count),
            () => Assert.Equal(100, service.Sessions(new TranscriptSessionsRequest(null, null, null, 100, true), default).Sessions.Count),
            () => Assert.Equal(100, service.Sessions(new TranscriptSessionsRequest(null, Ended(500), Ended(1500), 100, false), default).Sessions.Count),
            () => Assert.Equal(10, service.Sessions(new TranscriptSessionsRequest(Project(5), Ended(1000), Ended(1200), 100, true), default).Sessions.Count),
            () =>
            {
                var r = service.Sessions(new TranscriptSessionsRequest(null, null, Ended(100), 100, false), default);
                Assert.Equal(90, r.Total);
                Assert.Equal(10, r.AutomatedHidden);
            },
        };

        Assert.True(Median("transcript_get by id (context 3)", byId) < Bound);
        Assert.True(Median("transcript_get by range (10 exchanges, context 2)", byRange) < Bound);
        Assert.True(Median("transcript_sessions with filters (limit 100)", lists) < Bound);
    }
}
