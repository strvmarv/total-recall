using System.Threading;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Transcripts;
using TotalRecall.TestSupport.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

/// <summary>
/// What happens when something commits while a pass is reading (spec section 7 step 6): a forget wins, a changed cutoff
/// makes the pass stale and it is redone once, and a concurrent pass makes it conflict. Each hook asserts how often it
/// ran, so a race that never happened fails the test.
/// </summary>
public sealed class TranscriptIngestRaceTests : System.IDisposable
{
    private const string Host = IngestHarness.Host;

    private readonly IngestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private TranscriptTestDb Db => _h.Db;
    private ClaudeFixtureHome Home => _h.Home;

    [Fact]
    public void ForgetSessionDuringPass_IsExcluded_NothingWritten()
    {
        var s = Home.Place("decision", _h.Cwd, "race-fs");
        var adapter = _h.Adapter(_ => Db.Store.ForgetSession(Host, s.Id));

        var result = _h.Ingest(Db, s, adapter);

        Assert.Equal(SessionIngestStatus.Excluded, result.Status);
        Assert.Equal(1, adapter.HookFired);
        Assert.Equal(1, adapter.ReadHeadCalls);      // a forget wins: no redo
        Assert.Equal(0, _h.Sessions(Db));
        Assert.Equal(0, _h.Chunks(Db));
        Assert.Equal(1, Db.Scalar("SELECT count(*) FROM tx_forgotten WHERE kind = 'session'"));
    }

    [Fact]
    public void ForgetProjectDuringPass_IsExcluded()
    {
        var s = Home.Place("decision", _h.Cwd, "race-fp");
        Assert.Equal(SessionIngestStatus.New, _h.Ingest(Db, s).Status);
        Home.AppendExchange(s, "A later question.", "A later answer.", 300);
        var adapter = _h.Adapter(_ => Db.Store.ForgetProject("proj"));

        var result = _h.Ingest(Db, s, adapter);

        Assert.Equal(SessionIngestStatus.Excluded, result.Status);
        Assert.Equal(1, adapter.HookFired);
        Assert.Equal(1, adapter.ReadHeadCalls);
        Assert.Equal(0, _h.Sessions(Db));
        Assert.Equal(0, _h.Chunks(Db));
    }

    [Fact]
    public void ForgetAllDuringPass_IsStale_RedoStoresOnlyAfterCutoff()
    {
        var s = Home.Place("straddle", _h.Cwd, "race-fa");
        Home.SetMtime(s, Home.BaseTime.AddDays(1));
        var adapter = _h.Adapter(_ => Db.Store.ForgetAll(Home.BaseTime.AddSeconds(50)));

        var result = _h.Ingest(Db, s, adapter);

        Assert.Equal(SessionIngestStatus.New, result.Status);
        Assert.Equal(1, adapter.HookFired);
        Assert.Equal(2, adapter.ReadRecordsCalls);   // the stale pass and its redo
        var contents = IngestHarness.Contents(Db);
        Assert.Contains(contents, c => c.Contains("zqxstraddle4"));
        Assert.Contains(contents, c => c.Contains("zqxstraddle5"));
        Assert.Contains(contents, c => c.Contains("zqxstraddle6"));
        foreach (var gone in new[] { "zqxstraddle1", "zqxstraddle2", "zqxstraddle3" })
            Assert.DoesNotContain(contents, c => c.Contains(gone));
        Assert.Equal(TranscriptTime.Iso(Home.BaseTime.AddSeconds(50)),
            Db.Text("SELECT built_under_cutoff FROM tx_session"));
    }

    [Fact]
    public void CutoffChangingEveryRead_IsRetry()
    {
        var s = Home.Place("straddle", _h.Cwd, "race-retry");
        Home.SetMtime(s, Home.BaseTime.AddDays(1));
        var n = 0;
        var adapter = _h.Adapter(_ => Db.Store.ForgetAll(Home.BaseTime.AddSeconds(++n)), everyTime: true);

        var result = _h.Ingest(Db, s, adapter);

        Assert.Equal(SessionIngestStatus.Retry, result.Status);
        Assert.Equal(2, adapter.HookFired);
        Assert.Equal(0, _h.Sessions(Db));
        Assert.Equal(0, _h.Chunks(Db));
        Assert.Equal(TranscriptTime.Iso(Home.BaseTime.AddSeconds(2)), Db.Store.IngestNotBefore()!.Raw);
    }

    [Fact]
    public void ConcurrentPass_IsConflict_EndsLikeOnePass()
    {
        var s = Home.Place("decision", _h.Cwd, "race-cc");
        SessionIngestResult? inner = null;
        var adapter = _h.Adapter(src =>
        {
            var opened = TranscriptStore.Open(new TranscriptLocation(Db.Path, IsMainDatabase: false), create: false);
            Assert.Equal(TranscriptOpenStatus.Opened, opened.Status);
            using var other = opened.Store!;
            inner = _h.Ingester(other, new ClaudeCodeAdapter(Home.ProjectsDir), new ProjectResolver())
                .IngestSession(src, CancellationToken.None);
        });

        var outer = _h.Ingest(Db, s, adapter);

        Assert.Equal(1, adapter.HookFired);
        Assert.Equal(SessionIngestStatus.New, inner!.Status);
        Assert.Equal(SessionIngestStatus.Unchanged, outer.Status);
        Assert.Equal(1, adapter.ReadRecordsCalls);   // the redo found the row the other pass wrote and stopped
        IngestHarness.AssertSame(_h.Once(s), ArchiveSnapshot.Read(Db.Path));
    }
}
