using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TotalRecall.Infrastructure.Transcripts;
using TotalRecall.TestSupport.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

/// <summary>
/// <see cref="TranscriptReadService.Get"/> (read spec section 2): resolving a chunk id or a session range, the window and
/// its context, the token budget (requested exchanges first, context alternating before and after, a first exchange
/// alone over the budget cut with the marker), the reading-on pointers, the exact errors of unknown targets and the
/// argument checks. A real file-backed archive seeded through <see cref="ReadArchiveBuilder"/>; the service opens it per
/// call. In the process-environment collection because the error-path tests swap <see cref="Console.Error"/>.
/// </summary>
[Collection("process-environment")]
public sealed class TranscriptReadServiceGetTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private const string Suffix = " (nothing is archived yet; run total-recall transcripts backfill)";

    private readonly TranscriptTestDb _db = new();
    private readonly ReadArchiveBuilder _archive;

    public TranscriptReadServiceGetTests() => _archive = new ReadArchiveBuilder(_db.Store);

    public void Dispose() => _db.Dispose();

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private TranscriptReadService Service(string? path = null) =>
        new(() => new TranscriptReadContext(new TranscriptLocation(path ?? _db.Path, IsMainDatabase: false)));

    private static TranscriptGetRequest ById(long id, int context = 1, int maxTokens = 2000) =>
        new(id, null, null, null, context, maxTokens);

    private static TranscriptGetRequest Range(string session, int from, int? to = null, int context = 1, int maxTokens = 2000) =>
        new(null, session, from, to, context, maxTokens);

    /// <summary>A body of exactly <paramref name="length"/> characters that names its exchange.</summary>
    private static string Body(int n, int length)
    {
        var head = $"exchange {n} ";
        return length <= head.Length ? head[..length] : head + new string('a', length - head.Length);
    }

    /// <summary>Seeds exchanges 0..count-1 of <paramref name="id"/>, each a body of <paramref name="length"/> characters.</summary>
    private SessionBuilder Seed(string id, int count, int length = 40, string kind = "interactive")
    {
        var s = _archive.Session("claude-code", id, project: "acme/widgets", kind: kind, title: "The title");
        for (var n = 0; n < count; n++)
            s.Exchange(n, Body(n, length), ts: T0.AddMinutes(n));
        return s;
    }

    private long ChunkId(string hostSessionId, int exchange, int part = 0) =>
        _db.Scalar(
            "SELECT c.id FROM tx_chunk c JOIN tx_session s ON s.id = c.session_id " +
            $"WHERE s.host_session_id = '{hostSessionId}' AND c.exchange_start = {exchange} AND c.part = {part}");

    private static int[] Ns(TranscriptGetResponse r) => r.Exchanges.Select(e => e.N).ToArray();

    private static int[] RequestedNs(TranscriptGetResponse r) => r.Exchanges.Where(e => e.Requested).Select(e => e.N).ToArray();

    private static int Tokens(string text) => (text.Length + 3) / 4;

    private static void AssertBudgetArithmetic(TranscriptGetResponse r, int maxTokens)
    {
        Assert.Equal(r.Exchanges.Sum(e => Tokens(e.Text)), r.ApproxTokens);
        Assert.True(r.ApproxTokens <= maxTokens, $"approxTokens {r.ApproxTokens} is over max_tokens {maxTokens}");
    }

    /// <summary>
    /// Reads <paramref name="first"/>, then issues exactly the call each answer names (from its pointers, context 0) until
    /// no pointer remains. Returns every page.
    /// </summary>
    private List<TranscriptGetResponse> ReadOn(TranscriptReadService svc, TranscriptGetRequest first)
    {
        var pages = new List<TranscriptGetResponse>();
        var request = first;
        while (true)
        {
            var page = svc.Get(request, default);
            Assert.Null(page.Error);
            AssertBudgetArithmetic(page, request.MaxTokens);
            pages.Add(page);
            Assert.True(pages.Count <= 200, "reading on did not terminate");
            Assert.Equal(page.NextFromExchange is null, page.NextToExchange is null);
            if (page.NextFromExchange is not { } from)
                return pages;
            Assert.Contains($"session: {page.Session}", page.Note);
            Assert.Contains($"from_exchange: {from}", page.Note);
            Assert.Contains($"to_exchange: {page.NextToExchange}", page.Note);
            Assert.Contains("context: 0", page.Note);
            request = new TranscriptGetRequest(null, page.Session, from, page.NextToExchange, 0, request.MaxTokens);
        }
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

    // ---- following a hit --------------------------------------------------------------------------------------------

    [Fact]
    public void Get_ById_ReturnsHitWithOneContextEachSide_FlagsRequested_Ascending()
    {
        var s = Seed("s1", 5);
        s.Exchange(5, "the failing exchange", ts: T0.AddHours(1), hasError: true);
        s.Commit();

        var r = Service().Get(ById(ChunkId("s1", 2)), default);

        Assert.Null(r.Error);
        Assert.Equal("claude-code:s1", r.Session);
        Assert.Equal("acme/widgets", r.Project);
        Assert.Equal("interactive", r.Kind);
        Assert.Equal("The title", r.Title);
        Assert.Equal("2", r.Requested);
        Assert.Equal(1, r.Context);
        Assert.Equal(new[] { 1, 2, 3 }, Ns(r));
        Assert.Equal(new[] { false, true, false }, r.Exchanges.Select(e => e.Requested).ToArray());
        Assert.Equal(Body(2, 40), r.Exchanges[1].Text);
        Assert.Equal(ReadArchiveBuilder.Iso(T0.AddMinutes(2)), r.Exchanges[1].Ts);
        Assert.Equal("dialogue", r.Exchanges[1].ChunkType);
        Assert.False(r.Exchanges[1].HasError);
        Assert.False(r.Truncated);
        Assert.Null(r.NextFromExchange);
        Assert.Null(r.NextToExchange);
        Assert.Null(r.Note);
        AssertBudgetArithmetic(r, 2000);

        var failing = Service().Get(ById(ChunkId("s1", 5)), default);
        Assert.Equal(new[] { 4, 5 }, Ns(failing));
        Assert.True(failing.Exchanges[1].HasError);
        Assert.Equal("the failing exchange", failing.Exchanges[1].Text);
    }

    [Fact]
    public void Get_Context0_OnlyTheExchange()
    {
        Seed("s1", 5).Commit();

        var r = Service().Get(ById(ChunkId("s1", 2), context: 0), default);

        Assert.Equal(new[] { 2 }, Ns(r));
        Assert.True(r.Exchanges[0].Requested);
        Assert.Equal(0, r.Context);
        Assert.False(r.Truncated);
    }

    [Fact]
    public void Get_Context10_NearEnds_ReturnsWhatExists_NotTruncated()
    {
        Seed("s1", 5).Commit();

        var nearStart = Service().Get(ById(ChunkId("s1", 1), context: 10, maxTokens: 20000), default);
        var nearEnd = Service().Get(ById(ChunkId("s1", 4), context: 10, maxTokens: 20000), default);

        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, Ns(nearStart));
        Assert.Equal(new[] { 1 }, RequestedNs(nearStart));
        Assert.False(nearStart.Truncated);
        Assert.Null(nearStart.Note);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, Ns(nearEnd));
        Assert.Equal(new[] { 4 }, RequestedNs(nearEnd));
        Assert.False(nearEnd.Truncated);
    }

    // ---- ranges -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Get_SessionRange_ReturnsStoredInOrder()
    {
        Seed("s1", 6).Commit();

        var r = Service().Get(Range("claude-code:s1", 1, 3, context: 0), default);

        Assert.Null(r.Error);
        Assert.Equal("1-3", r.Requested);
        Assert.Equal(new[] { 1, 2, 3 }, Ns(r));
        Assert.All(r.Exchanges, e => Assert.True(e.Requested));
        Assert.Equal(new[] { Body(1, 40), Body(2, 40), Body(3, 40) }, r.Exchanges.Select(e => e.Text).ToArray());

        // to_exchange defaults to from_exchange
        var one = Service().Get(Range("claude-code:s1", 4), default);
        Assert.Equal("4", one.Requested);
        Assert.Equal(new[] { 3, 4, 5 }, Ns(one));
        Assert.Equal(new[] { 4 }, RequestedNs(one));
    }

    [Fact]
    public void Get_Gaps_SkippedWithoutUsingContext()
    {
        _archive.Session("claude-code", "s1")
            .Exchange(0, "zero").Exchange(1, "one").Exchange(3, "three").Exchange(4, "four").Exchange(6, "six")
            .Commit();

        var r = Service().Get(Range("claude-code:s1", 1, 3, context: 1), default);

        Assert.Equal(new[] { 0, 1, 3, 4 }, Ns(r));
        Assert.Equal(new[] { 1, 3 }, RequestedNs(r));
        Assert.False(r.Truncated);

        // context counts stored exchanges: the gap at 5 costs nothing, so context 1 after 4 is 6
        var after = Service().Get(Range("claude-code:s1", 4, context: 1), default);
        Assert.Equal(new[] { 3, 4, 6 }, Ns(after));
    }

    [Fact]
    public void Get_RangePastEnd_ReturnsWhatExists()
    {
        Seed("s1", 5).Commit();

        var r = Service().Get(Range("claude-code:s1", 3, 10), default);

        Assert.Equal("3-10", r.Requested);
        Assert.Equal(new[] { 2, 3, 4 }, Ns(r));
        Assert.Equal(new[] { 3, 4 }, RequestedNs(r));
        Assert.False(r.Truncated);
        Assert.Null(r.NextFromExchange);
    }

    [Fact]
    public void Get_RangeEntirelyPastEnd_NoteNotError()
    {
        Seed("s1", 5).Commit();

        var r = Service().Get(Range("claude-code:s1", 10, 12), default);

        Assert.Null(r.Error);
        Assert.Equal("claude-code:s1", r.Session);
        Assert.Equal("10-12", r.Requested);
        Assert.Empty(r.Exchanges);
        Assert.Equal(0, r.ApproxTokens);
        Assert.False(r.Truncated);
        Assert.Null(r.NextFromExchange);
        Assert.NotNull(r.Note);
        Assert.Contains("10-12", r.Note);
        Assert.Contains("5 exchanges", r.Note);
    }

    // ---- the text of an exchange ------------------------------------------------------------------------------------

    [Fact]
    public void Get_MultiPartExchange_OneText_NoHeaderNoPrefix()
    {
        _archive.Session("claude-code", "s1")
            .Exchange(0, "before")
            .Parts(1,
                "User: how do I fix it\n\nAssistant: part one",
                "User (continued): how do I fix it\n\nAssistant: part two",
                "User (continued): how do I fix it\n\nAssistant: part three")
            .Exchange(2, "after")
            .Commit();

        var r = Service().Get(Range("claude-code:s1", 1, context: 0), default);

        var e = Assert.Single(r.Exchanges);
        Assert.Equal(1, e.N);
        Assert.Equal("User: how do I fix it\n\nAssistant: part one\n\nAssistant: part two\n\nAssistant: part three", e.Text);
        Assert.DoesNotContain("claude-code session", e.Text);
        Assert.DoesNotContain("User (continued)", e.Text);
        Assert.Equal(Tokens(e.Text), r.ApproxTokens);

        // a hit on a later part reads the whole exchange
        var byPart = Service().Get(ById(ChunkId("s1", 1, part: 2), context: 0), default);
        Assert.Equal(e.Text, Assert.Single(byPart.Exchanges).Text);
    }

    [Fact]
    public void Get_CompactionSummary_KeptAsStored()
    {
        const string body = "User: earlier work\n\nUser (continued): earlier work\n\nthe summary";
        _archive.Session("claude-code", "s1")
            .Exchange(0, "first")
            .Exchange(1, body, chunkType: "compaction-summary")
            .Commit();

        var r = Service().Get(Range("claude-code:s1", 1, context: 0), default);

        var e = Assert.Single(r.Exchanges);
        Assert.Equal("compaction-summary", e.ChunkType);
        Assert.Equal(body, e.Text);
    }

    // ---- the budget -------------------------------------------------------------------------------------------------

    [Fact]
    public void Get_WindowOverBudget_DropsFarContextFirst_AlternatingBeforeAfter_Truncated_NoNext()
    {
        Seed("s1", 11, length: 400).Commit(); // 100 tokens each

        // 5, then 4 (before first), 6, 3 = 400 tokens; 7 does not fit in 450
        var r = Service().Get(ById(ChunkId("s1", 5), context: 3, maxTokens: 450), default);

        Assert.Null(r.Error);
        Assert.Equal(new[] { 3, 4, 5, 6 }, Ns(r));
        Assert.Equal(new[] { 5 }, RequestedNs(r));
        Assert.True(r.Truncated);
        Assert.Null(r.NextFromExchange);
        Assert.Null(r.NextToExchange);
        Assert.Equal(400, r.ApproxTokens);
        Assert.NotNull(r.Note);
        Assert.Contains("context", r.Note);
        Assert.Contains("max_tokens", r.Note);
        AssertBudgetArithmetic(r, 450);

        // with room for everything the same window is whole
        var whole = Service().Get(ById(ChunkId("s1", 5), context: 3, maxTokens: 700), default);
        Assert.Equal(new[] { 2, 3, 4, 5, 6, 7, 8 }, Ns(whole));
        Assert.False(whole.Truncated);
    }

    [Fact]
    public void Get_RequestedRangeOverBudget_EndsAtLastWhole_NextPointers_NoContextAdded()
    {
        Seed("s1", 11, length: 400).Commit(); // 100 tokens each

        var r = Service().Get(Range("claude-code:s1", 2, 8, context: 2, maxTokens: 350), default);

        Assert.Null(r.Error);
        Assert.Equal(new[] { 2, 3, 4 }, Ns(r));
        Assert.All(r.Exchanges, e => Assert.True(e.Requested));
        Assert.True(r.Truncated);
        Assert.Equal(5, r.NextFromExchange);
        Assert.Equal(8, r.NextToExchange);
        Assert.Equal(300, r.ApproxTokens);
        Assert.Contains("session: claude-code:s1", r.Note);
        Assert.Contains("from_exchange: 5", r.Note);
        Assert.Contains("to_exchange: 8", r.Note);
        Assert.Contains("context: 0", r.Note);

        // reading on returns the rest, none twice
        var rest = Service().Get(Range("claude-code:s1", 5, 8, context: 0, maxTokens: 20000), default);
        Assert.Equal(new[] { 5, 6, 7, 8 }, Ns(rest));
        Assert.False(rest.Truncated);
    }

    [Fact]
    public void Get_Paging_ReadOnWithContext0_UnionEqualsRange_NoneTwice()
    {
        var s = _archive.Session("claude-code", "s1");
        for (var n = 0; n < 40; n++)
            s.Exchange(n, Body(n, 800 + (n % 5) * 50)); // 200 to 250 tokens: pages of one or two exchanges
        s.Commit();

        var pages = ReadOn(Service(), Range("claude-code:s1", 0, 39, context: 1, maxTokens: 400));

        var read = pages.SelectMany(RequestedNs).ToArray();
        Assert.Equal(Enumerable.Range(0, 40).ToArray(), read);
        Assert.Equal(read.Length, read.Distinct().Count());
        Assert.All(pages, p => Assert.All(p.Exchanges, e => Assert.True(e.Requested)));
        Assert.True(pages.Count > 1);
        Assert.All(pages.Take(pages.Count - 1), p => Assert.True(p.Truncated));
        Assert.False(pages[^1].Truncated);
    }

    [Fact]
    public void Get_FirstExchangeOverBudget_CutWithMarker_ApproxWithinBudget()
    {
        var big = Body(1, 5000);
        _archive.Session("claude-code", "s1")
            .Exchange(0, "small before")
            .Exchange(1, big)
            .Exchange(2, "small after")
            .Commit();

        var r = Service().Get(ById(ChunkId("s1", 1), context: 1, maxTokens: 200), default);

        Assert.Null(r.Error);
        var e = Assert.Single(r.Exchanges);
        Assert.Equal(1, e.N);
        Assert.True(e.Requested);
        Assert.EndsWith(TranscriptReadService.CutMarker, e.Text);
        Assert.True(e.Text.Length <= 4 * 200);
        Assert.StartsWith(big[..700], e.Text);
        Assert.True(r.ApproxTokens <= 200);
        AssertBudgetArithmetic(r, 200);
        Assert.True(r.Truncated);
        Assert.Null(r.NextFromExchange);
        Assert.Null(r.NextToExchange);
        Assert.NotNull(r.Note);
        Assert.Contains("exchange 1", r.Note);
        Assert.Contains("max_tokens", r.Note);
    }

    [Fact]
    public void Get_OversizedExchangeInTheMiddle_Paging_ReachesEveryLaterExchange()
    {
        var s = _archive.Session("claude-code", "s1");
        for (var n = 0; n < 26; n++)
        {
            if (n == 15)
                s.Parts(15, Enumerable.Range(0, 5).Select(p => Body(15, 1800)).ToArray());
            else
                s.Exchange(n, Body(n, 400)); // 100 tokens each
        }
        s.Commit();
        var svc = Service();

        var pages = ReadOn(svc, Range("claude-code:s1", 10, 20, context: 1, maxTokens: 1000));

        Assert.Equal(3, pages.Count);
        Assert.Equal(new[] { 10, 11, 12, 13, 14 }, Ns(pages[0]));
        Assert.Equal(15, pages[0].NextFromExchange);
        Assert.Equal(20, pages[0].NextToExchange);

        var cut = Assert.Single(pages[1].Exchanges);
        Assert.Equal(15, cut.N);
        Assert.EndsWith(TranscriptReadService.CutMarker, cut.Text);
        Assert.True(pages[1].Truncated);
        Assert.Equal(16, pages[1].NextFromExchange);
        Assert.Equal(20, pages[1].NextToExchange);
        Assert.Contains("exchange 15", pages[1].Note);

        Assert.Equal(new[] { 16, 17, 18, 19, 20 }, Ns(pages[2]));
        Assert.False(pages[2].Truncated);
        Assert.Null(pages[2].NextFromExchange);
        Assert.Equal(Enumerable.Range(10, 11).ToArray(), pages.SelectMany(RequestedNs).ToArray());
    }

    [Fact]
    public void Get_EveryExchangeOverBudget_EachPageReturnsOne_Terminates()
    {
        var s = _archive.Session("claude-code", "s1");
        for (var n = 0; n < 5; n++)
            s.Exchange(n, Body(n, 2000)); // 500 tokens each
        s.Commit();

        var pages = ReadOn(Service(), Range("claude-code:s1", 0, 4, context: 1, maxTokens: 200));

        Assert.Equal(5, pages.Count);
        for (var i = 0; i < 5; i++)
        {
            var e = Assert.Single(pages[i].Exchanges);
            Assert.Equal(i, e.N);
            Assert.EndsWith(TranscriptReadService.CutMarker, e.Text);
            Assert.True(pages[i].Truncated);
        }
        Assert.Null(pages[^1].NextFromExchange);
    }

    // ---- targets ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Get_AutomatedSession_ByRef_AndById_Readable()
    {
        Seed("auto1", 3, kind: "automated").Commit();

        var byRef = Service().Get(Range("claude-code:auto1", 1), default);
        var byId = Service().Get(ById(ChunkId("auto1", 1)), default);

        Assert.Null(byRef.Error);
        Assert.Equal("automated", byRef.Kind);
        Assert.Equal(new[] { 0, 1, 2 }, Ns(byRef));
        Assert.Null(byId.Error);
        Assert.Equal("automated", byId.Kind);
        Assert.Equal(new[] { 0, 1, 2 }, Ns(byId));
    }

    [Fact]
    public void Get_UnknownChunk_ExactError_And_Suffix_WhenArchiveEmpty()
    {
        Assert.Equal(
            "no archived chunk has id 5 (it may have been forgotten); use transcript_sessions to list sessions",
            TranscriptReadService.UnknownChunk(5, anyChunk: true));
        Assert.Equal(
            "no archived chunk has id 5 (it may have been forgotten); use transcript_sessions to list sessions" + Suffix,
            TranscriptReadService.UnknownChunk(5, anyChunk: false));
        Assert.Equal(Suffix, TranscriptReadService.NothingArchivedSuffix);

        var empty = Service().Get(ById(5), default);
        Assert.Equal(TranscriptReadService.UnknownChunk(5, anyChunk: false), empty.Error);

        Seed("s1", 2).Commit();
        var unknown = Service().Get(ById(99999), default);
        Assert.Equal(
            "no archived chunk has id 99999 (it may have been forgotten); use transcript_sessions to list sessions",
            unknown.Error);
        Assert.Empty(unknown.Exchanges);
        Assert.Equal("", unknown.Session);
        Assert.Null(unknown.Note);
        Assert.Null(unknown.NextFromExchange);
        Assert.False(unknown.Truncated);
    }

    [Fact]
    public void Get_UnknownSession_ExactError()
    {
        Assert.Equal(
            "no archived session claude-code:nope; use transcript_sessions to list sessions",
            TranscriptReadService.UnknownSession("claude-code:nope", anyChunk: true));
        Assert.Equal(
            "no archived session claude-code:nope; use transcript_sessions to list sessions" + Suffix,
            TranscriptReadService.UnknownSession("claude-code:nope", anyChunk: false));

        Seed("s1", 2).Commit();
        var r = Service().Get(Range("claude-code:nope", 0), default);

        Assert.Equal("no archived session claude-code:nope; use transcript_sessions to list sessions", r.Error);
        Assert.Empty(r.Exchanges);
        Assert.Equal("", r.Session);
        Assert.Equal("", r.Requested);
    }

    [Fact]
    public void Get_ForgottenOnlySession_UnknownWithSuffix()
    {
        Seed("s1", 3).Commit();
        var chunk = ChunkId("s1", 1);
        Assert.Null(Service().Get(ById(chunk), default).Error);

        _db.Store.ForgetSession("claude-code", "s1");

        Assert.Equal(TranscriptReadService.UnknownSession("claude-code:s1", anyChunk: false),
            Service().Get(Range("claude-code:s1", 0), default).Error);
        Assert.Equal(TranscriptReadService.UnknownChunk(chunk, anyChunk: false),
            Service().Get(ById(chunk), default).Error);
    }

    [Fact]
    public void Get_ForgottenSession_OthersArchived_UnknownWithoutSuffix()
    {
        Seed("s1", 3).Commit();
        Seed("s2", 3).Commit();
        var chunk = ChunkId("s1", 1);

        _db.Store.ForgetSession("claude-code", "s1");

        Assert.Equal(TranscriptReadService.UnknownSession("claude-code:s1", anyChunk: true),
            Service().Get(Range("claude-code:s1", 0), default).Error);
        Assert.Equal(TranscriptReadService.UnknownChunk(chunk, anyChunk: true),
            Service().Get(ById(chunk), default).Error);
    }

    [Fact]
    public void Get_AbsentArchiveFile_UnknownWithSuffix_CreatesNothing()
    {
        var path = Path.Combine(_db.Dir, "missing", "archive.db");
        var svc = Service(path);

        Assert.Equal(TranscriptReadService.UnknownSession("claude-code:s1", anyChunk: false),
            svc.Get(Range("claude-code:s1", 0), default).Error);
        Assert.Equal(TranscriptReadService.UnknownChunk(7, anyChunk: false), svc.Get(ById(7), default).Error);

        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + "-wal"));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Fact]
    public void Get_ArchiveThatCannotBeOpened_ErrorResult_NotException()
    {
        var svc = new TranscriptReadService(
            () => new TranscriptReadContext(new TranscriptLocation(_db.Path, IsMainDatabase: false)),
            null,
            _ => new TranscriptOpenResult(TranscriptOpenStatus.Failed, null, "disk I/O error"));

        TranscriptGetResponse? r = null;
        var stderr = CaptureError(() => r = svc.Get(Range("claude-code:s1", 0), default));

        Assert.NotNull(r);
        Assert.StartsWith("the transcript archive could not be opened: ", r!.Error);
        Assert.Contains("disk I/O error", r.Error);
        Assert.Empty(r.Exchanges);
        Assert.Contains("[total-recall] transcript_get", stderr);
        Assert.Contains("disk I/O error", stderr);
    }

    [Fact]
    public void Get_ArchiveRefused_ErrorResult()
    {
        // A separate archive file named as the main database: Open refuses it.
        var svc = new TranscriptReadService(
            () => new TranscriptReadContext(new TranscriptLocation(_db.Path, IsMainDatabase: true)));

        TranscriptGetResponse? r = null;
        CaptureError(() => r = svc.Get(Range("claude-code:s1", 0), default));

        Assert.StartsWith("the transcript archive could not be opened: ", r!.Error);
    }

    [Fact]
    public void Get_StoreFault_ErrorResult_Logged_NotException()
    {
        Seed("s1", 3).Commit();
        var svc = new TranscriptReadService(
            () => new TranscriptReadContext(new TranscriptLocation(_db.Path, IsMainDatabase: false)),
            null,
            _ => new TranscriptOpenResult(TranscriptOpenStatus.Opened, new ThrowingReadsStore(), null));

        TranscriptGetResponse? r = null;
        var stderr = CaptureError(() => r = svc.Get(Range("claude-code:s1", 0), default));

        Assert.StartsWith("the transcript archive could not be read: ", r!.Error);
        Assert.Contains("a fault inside the store", r.Error);
        Assert.Contains("[total-recall] transcript_get", stderr);
    }

    [Fact]
    public void Get_ContextNullOrThrowing_ErrorResult()
    {
        var none = new TranscriptReadService(() => null).Get(Range("claude-code:s1", 0), default);
        Assert.False(string.IsNullOrWhiteSpace(none.Error));

        TranscriptGetResponse? thrown = null;
        CaptureError(() => thrown = new TranscriptReadService(() => throw new InvalidOperationException("transcripts.db_path is invalid"))
            .Get(Range("claude-code:s1", 0), default));
        Assert.Contains("transcripts.db_path is invalid", thrown!.Error);
    }

    // ---- arguments --------------------------------------------------------------------------------------------------

    public static IEnumerable<object[]> BadArguments() => new[]
    {
        new object[] { new TranscriptGetRequest(5, "claude-code:s1", 0, null, 1, 2000), "not both" },
        new object[] { new TranscriptGetRequest(null, null, null, null, 1, 2000), "give either id (a chunk id" },
        new object[] { new TranscriptGetRequest(5, null, 0, null, 1, 2000), "from_exchange" },
        new object[] { new TranscriptGetRequest(5, null, null, 3, 1, 2000), "to_exchange" },
        new object[] { new TranscriptGetRequest(null, "claude-code:s1", null, null, 1, 2000), "from_exchange" },
        new object[] { new TranscriptGetRequest(null, "claude-code:s1", null, 4, 1, 2000), "from_exchange" },
        new object[] { new TranscriptGetRequest(null, "no-colon", 0, null, 1, 2000), "session" },
        new object[] { new TranscriptGetRequest(null, ":id", 0, null, 1, 2000), "session" },
        new object[] { new TranscriptGetRequest(null, "host:", 0, null, 1, 2000), "session" },
        new object[] { new TranscriptGetRequest(0, null, null, null, 1, 2000), "id must be a chunk id of 1 or more" },
        new object[] { new TranscriptGetRequest(-3, null, null, null, 1, 2000), "id must be a chunk id of 1 or more" },
        new object[] { new TranscriptGetRequest(null, "claude-code:s1", -1, null, 1, 2000), "from_exchange" },
        new object[] { new TranscriptGetRequest(null, "claude-code:s1", 5, 4, 1, 2000), "to_exchange" },
        new object[] { new TranscriptGetRequest(null, "claude-code:s1", 0, 200, 1, 2000), "exchanges wide" },
        new object[] { new TranscriptGetRequest(null, "claude-code:s1", 0, int.MaxValue, 1, 2000), "exchanges wide" }, // the width in int would overflow
        new object[] { new TranscriptGetRequest(5, null, null, null, 11, 2000), "context" },
        new object[] { new TranscriptGetRequest(5, null, null, null, -1, 2000), "context" },
        new object[] { new TranscriptGetRequest(5, null, null, null, 1, 199), "max_tokens" },
        new object[] { new TranscriptGetRequest(5, null, null, null, 1, 20001), "max_tokens" },
        new object[] { new TranscriptGetRequest(5, null, null, null, 1, 100), "max_tokens" },
    };

    [Theory]
    [MemberData(nameof(BadArguments))]
    public void Get_Arguments_Refused_NamingTheArgument_BeforeTheArchiveIsOpened(TranscriptGetRequest request, string named)
    {
        var contextCalls = 0;
        var opens = 0;
        var svc = new TranscriptReadService(
            () => { contextCalls++; return new TranscriptReadContext(new TranscriptLocation(_db.Path, IsMainDatabase: false)); },
            null,
            location => { opens++; return TranscriptStore.Open(location, create: false); });

        var ex = Assert.Throws<ArgumentException>(() => svc.Get(request, default));

        Assert.Contains(named, ex.Message);
        Assert.Equal(0, contextCalls);
        Assert.Equal(0, opens);
    }

    [Fact]
    public void Get_Arguments_Width200Accepted_201Refused()
    {
        Seed("s1", 3).Commit();

        var ok = Service().Get(Range("claude-code:s1", 0, 199), default);
        Assert.Null(ok.Error);
        Assert.Equal("0-199", ok.Requested);
        Assert.Equal(new[] { 0, 1, 2 }, Ns(ok));

        Assert.Throws<ArgumentException>(() => Service().Get(Range("claude-code:s1", 0, 200), default));
    }

    [Fact]
    public void Get_Arguments_LimitsAccepted()
    {
        Seed("s1", 3).Commit();
        var id = ChunkId("s1", 1);

        Assert.Null(Service().Get(ById(id, context: 0, maxTokens: 200), default).Error);
        Assert.Null(Service().Get(ById(id, context: 10, maxTokens: 20000), default).Error);
        Assert.Null(Service().Get(Range("claude-code:s1", 0, 0), default).Error);
    }

    [Fact]
    public void Get_Arguments_SessionSplitsAtTheFirstColon()
    {
        _archive.Session("claude-code", "a:b").Exchange(0, "colon id").Commit();

        var r = Service().Get(Range("claude-code:a:b", 0), default);

        Assert.Null(r.Error);
        Assert.Equal("claude-code:a:b", r.Session);
        Assert.Equal("colon id", Assert.Single(r.Exchanges).Text);
    }

    // ---- fakes ------------------------------------------------------------------------------------------------------

    /// <summary>A store whose read transaction throws; every other member is unused here.</summary>
    private sealed class ThrowingReadsStore : ITranscriptStore
    {
        public ITranscriptReads BeginReads() => throw new ArgumentOutOfRangeException("ordinal", "a fault inside the store");
        public void Dispose() { }

        public TranscriptStatus Status() => throw new NotSupportedException();
        public ForgetCount CountSession(string host, string hostSessionId) => throw new NotSupportedException();
        public ForgetCount CountProject(string projectKey) => throw new NotSupportedException();
        public ForgetCount CountAll() => throw new NotSupportedException();
        public ForgetResult ForgetSession(string host, string hostSessionId) => throw new NotSupportedException();
        public ForgetResult ForgetProject(string projectKey) => throw new NotSupportedException();
        public ForgetResult ForgetAll(DateTimeOffset now) => throw new NotSupportedException();
        public bool UnforgetSession(string host, string hostSessionId) => throw new NotSupportedException();
        public bool UnforgetProject(string projectKey) => throw new NotSupportedException();
        public bool ClearCutoff() => throw new NotSupportedException();
        public bool IsExcluded(string host, string hostSessionId, string? projectKey, string? cwd) => throw new NotSupportedException();
        public SessionIngestState? GetIngestState(string host, string hostSessionId) => throw new NotSupportedException();
        public IngestCutoff? IngestNotBefore() => throw new NotSupportedException();
        public IngestOutcome ApplyIngest(SessionIngest ingest) => throw new NotSupportedException();
        public bool IsBeforeCutoff(DateTimeOffset timestamp) => throw new NotSupportedException();
        public IReadOnlyList<EmbedChunk> NextEmbedWork(
            int limit, IReadOnlyCollection<long> exclude, (string Host, string HostSessionId)? preferSession = null) =>
            throw new NotSupportedException();
        public bool HasWaitingEmbedWork(string host, string hostSessionId) => throw new NotSupportedException();
        public EmbedBatchOutcome StoreEmbeddings(
            IReadOnlyList<ChunkEmbedding> batch, ArchiveFingerprint current, EmbedLeaseHolder holder, DateTimeOffset now) =>
            throw new NotSupportedException();
        public bool RecordEmbedFailure(long chunkId, string content) => throw new NotSupportedException();
        public int ResetEmbedAttempts() => throw new NotSupportedException();
        public bool TryAcquireEmbedLease(EmbedLeaseHolder holder, DateTimeOffset now, Func<string, int, bool> holderIsAlive) =>
            throw new NotSupportedException();
        public void ReleaseEmbedLease(EmbedLeaseHolder holder) => throw new NotSupportedException();
        public ArchiveFingerprint? ReadEmbedFingerprint() => throw new NotSupportedException();
        public FingerprintState CheckFingerprint(ArchiveFingerprint current) => throw new NotSupportedException();
        public EmbeddingCounts CountEmbedding() => throw new NotSupportedException();
        public void WriteEmbedRun(EmbedRunRecord run) => throw new NotSupportedException();
        public EmbedRunRecord? ReadEmbedRun() => throw new NotSupportedException();
        public ITranscriptSearchReads BeginSearchReads() => throw new NotSupportedException();
    }
}
