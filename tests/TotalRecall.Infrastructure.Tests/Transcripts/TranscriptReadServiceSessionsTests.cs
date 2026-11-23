using System;
using System.IO;
using System.Linq;
using TotalRecall.Infrastructure.Transcripts;
using TotalRecall.TestSupport.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

/// <summary>
/// <see cref="TranscriptReadService.Sessions"/> (read spec section 3): newest last activity first, the filters, the
/// limit with <c>Total</c> and <c>More</c>, the hidden automated count, the notes and paging with <c>Before</c>. A real
/// file-backed archive seeded through <see cref="ReadArchiveBuilder"/>. In the process-environment collection because
/// the error-path test swaps <see cref="Console.Error"/>.
/// </summary>
[Collection("process-environment")]
public sealed class TranscriptReadServiceSessionsTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private const string NothingArchived = "nothing is archived yet; run total-recall transcripts backfill";

    private readonly TranscriptTestDb _db = new();
    private readonly ReadArchiveBuilder _archive;

    public TranscriptReadServiceSessionsTests() => _archive = new ReadArchiveBuilder(_db.Store);

    public void Dispose() => _db.Dispose();

    private TranscriptReadService Service(string? path = null) =>
        new(() => new TranscriptReadContext(new TranscriptLocation(path ?? _db.Path, IsMainDatabase: false)));

    private static TranscriptSessionsRequest Req(
        string? project = null, DateTimeOffset? since = null, DateTimeOffset? before = null, int limit = 20, bool automated = false) =>
        new(project, since, before, limit, automated);

    private void Add(string id, DateTimeOffset? ended, string project = "acme/widgets", string kind = "interactive",
        string? title = "A title", int exchanges = 2, DateTimeOffset? started = null)
    {
        var s = _archive.Session("claude-code", id, project: project, kind: kind, title: title, startedAt: started, endedAt: ended);
        for (var n = 0; n < exchanges; n++)
            s.Exchange(n, $"body {id} {n}");
        s.Commit();
    }

    private static string CaptureError(Action action)
    {
        var original = Console.Error;
        using var captured = new StringWriter();
        try
        {
            Console.SetError(captured);
            action();
        }
        finally
        {
            Console.SetError(original);
        }
        return captured.ToString();
    }

    private static string[] Ids(TranscriptSessionsResponse r) => r.Sessions.Select(s => s.Session).ToArray();

    [Fact]
    public void Sessions_ArchiveThatCannotBeOpened_ErrorResult_NotException()
    {
        var svc = new TranscriptReadService(
            () => new TranscriptReadContext(new TranscriptLocation(_db.Path, IsMainDatabase: false)),
            null,
            _ => new TranscriptOpenResult(TranscriptOpenStatus.Failed, null, "disk I/O error"));

        TranscriptSessionsResponse? r = null;
        var stderr = CaptureError(() => r = svc.Sessions(Req(), default));

        Assert.NotNull(r);
        Assert.NotNull(r!.Error);
        Assert.StartsWith("the transcript archive could not be opened: ", r.Error);
        Assert.Contains("disk I/O error", r.Error);
        Assert.Equal(0, r.Total);
        Assert.Empty(r.Sessions);
        Assert.Contains("[total-recall] transcript_sessions", stderr);
        Assert.Contains("disk I/O error", stderr);
    }

    [Fact]
    public void Sessions_NewestFirst_WithTitlesAndFields()
    {
        Add("old", T0.AddDays(1), title: "Old one", started: T0, exchanges: 1);
        Add("new", T0.AddDays(3), title: null, started: T0.AddDays(2), exchanges: 3);
        Add("mid", T0.AddDays(2), title: "Mid one", exchanges: 2);

        var r = Service().Sessions(Req(), default);

        Assert.Null(r.Error);
        Assert.Equal(new[] { "claude-code:new", "claude-code:mid", "claude-code:old" }, Ids(r));
        Assert.Equal(3, r.Total);
        Assert.Equal(0, r.More);
        Assert.Equal(0, r.AutomatedHidden);
        Assert.Null(r.Note);

        var first = r.Sessions[0];
        Assert.Equal("acme/widgets", first.Project);
        Assert.Equal("interactive", first.Kind);
        Assert.Null(first.Title);
        Assert.Equal(ReadArchiveBuilder.Iso(T0.AddDays(2)), first.StartedAt);
        Assert.Equal(ReadArchiveBuilder.Iso(T0.AddDays(3)), first.EndedAt);
        Assert.Equal(3, first.Exchanges);
        Assert.Equal(3, first.Chunks);
        Assert.Equal("Old one", r.Sessions[2].Title);
        Assert.Equal(1, r.Sessions[2].Exchanges);
    }

    [Fact]
    public void Sessions_ProjectFilter()
    {
        Add("a", T0, project: "acme/widgets");
        Add("b", T0.AddDays(1), project: "other/thing");

        var r = Service().Sessions(Req(project: "other/thing"), default);

        Assert.Equal(new[] { "claude-code:b" }, Ids(r));
        Assert.Equal(1, r.Total);
    }

    [Fact]
    public void Sessions_SinceAndBefore_ActiveWindow()
    {
        Add("d1", T0.AddDays(1));
        Add("d2", T0.AddDays(2));
        Add("d3", T0.AddDays(3));

        // since is at or after, before is strictly earlier
        Assert.Equal(new[] { "claude-code:d3", "claude-code:d2" }, Ids(Service().Sessions(Req(since: T0.AddDays(2)), default)));
        Assert.Equal(new[] { "claude-code:d1" }, Ids(Service().Sessions(Req(before: T0.AddDays(2)), default)));
        var window = Service().Sessions(Req(since: T0.AddDays(2), before: T0.AddDays(3)), default);
        Assert.Equal(new[] { "claude-code:d2" }, Ids(window));
        Assert.Equal(1, window.Total);
    }

    [Fact]
    public void Sessions_Limit_TotalAndMore_NarrowingNote()
    {
        for (var i = 0; i < 5; i++)
            Add("s" + i, T0.AddDays(i));

        var r = Service().Sessions(Req(limit: 2), default);

        Assert.Equal(new[] { "claude-code:s4", "claude-code:s3" }, Ids(r));
        Assert.Equal(5, r.Total);
        Assert.Equal(3, r.More);
        Assert.NotNull(r.Note);
        Assert.Contains("newest 2", r.Note);
        Assert.Contains("project", r.Note);
        Assert.Contains("since", r.Note);
        Assert.Contains("before", r.Note);

        var all = Service().Sessions(Req(limit: 5), default);
        Assert.Equal(0, all.More);
        Assert.Null(all.Note);
    }

    [Fact]
    public void Sessions_AutomatedHiddenCount_ShownWhenIncluded()
    {
        Add("human", T0.AddDays(1));
        Add("bot1", T0.AddDays(2), kind: "automated");
        Add("bot2", T0.AddDays(3), kind: "automated");

        var hidden = Service().Sessions(Req(), default);
        Assert.Equal(new[] { "claude-code:human" }, Ids(hidden));
        Assert.Equal(1, hidden.Total);
        Assert.Equal(2, hidden.AutomatedHidden);

        var shown = Service().Sessions(Req(automated: true), default);
        Assert.Equal(new[] { "claude-code:bot2", "claude-code:bot1", "claude-code:human" }, Ids(shown));
        Assert.Equal(3, shown.Total);
        Assert.Equal(0, shown.AutomatedHidden);
    }

    [Fact]
    public void Sessions_ResetRowNotListed()
    {
        Add("real", T0);
        _archive.Session("claude-code", "reset", endedAt: T0.AddDays(5)).Reset().Commit();

        var r = Service().Sessions(Req(), default);

        Assert.Equal(new[] { "claude-code:real" }, Ids(r));
        Assert.Equal(1, r.Total);
    }

    [Fact]
    public void Sessions_EmptyArchive_TotalZero_NothingArchivedNote()
    {
        var r = Service().Sessions(Req(), default);

        Assert.Null(r.Error);
        Assert.Equal(0, r.Total);
        Assert.Equal(0, r.More);
        Assert.Empty(r.Sessions);
        Assert.Equal(NothingArchived, r.Note);
    }

    [Fact]
    public void Sessions_AbsentFile_SameAndCreatesNothing()
    {
        var path = Path.Combine(_db.Dir, "missing", "archive.db");

        var r = Service(path).Sessions(Req(), default);

        Assert.Null(r.Error);
        Assert.Equal(0, r.Total);
        Assert.Empty(r.Sessions);
        Assert.Equal(NothingArchived, r.Note);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + "-wal"));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Fact]
    public void Sessions_ProjectMatchingNothing_InNonEmptyArchive_NoNote()
    {
        Add("a", T0);

        var r = Service().Sessions(Req(project: "no/such"), default);

        Assert.Null(r.Error);
        Assert.Equal(0, r.Total);
        Assert.Empty(r.Sessions);
        Assert.Null(r.Note);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Sessions_LimitOutOfRange_Throws(int limit)
    {
        var ex = Assert.Throws<ArgumentException>(() => Service().Sessions(Req(limit: limit), default));
        Assert.Contains("limit", ex.Message);
    }

    [Fact]
    public void Sessions_PagingWithBefore_ReachesOlderSessions()
    {
        for (var i = 0; i < 150; i++)
            Add("p" + i.ToString("D3"), T0.AddMinutes(i), exchanges: 1);

        var first = Service().Sessions(Req(limit: 100), default);
        Assert.Equal(100, first.Sessions.Count);
        Assert.Equal(150, first.Total);
        Assert.Equal(50, first.More);

        var oldest = TranscriptSearchService.ParseSince(first.Sessions[^1].EndedAt!, "before");
        var second = Service().Sessions(Req(before: oldest, limit: 100), default);

        Assert.Equal(50, second.Sessions.Count);
        Assert.Equal(50, second.Total);
        Assert.Equal(0, second.More);
        Assert.Empty(Ids(first).Intersect(Ids(second)));
        Assert.Equal(150, Ids(first).Concat(Ids(second)).Distinct().Count());
    }
}
