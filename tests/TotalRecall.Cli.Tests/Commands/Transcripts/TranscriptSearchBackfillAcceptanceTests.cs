using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Transcripts;
using TotalRecall.TestSupport.Transcripts;
using Xunit;
using Xunit.Abstractions;

namespace TotalRecall.Cli.Tests.Commands.Transcripts;

/// <summary>
/// The archive search acceptance queries (search the archive, spec section 8, queries 1 to 7 and 13) through the
/// triggers, not the worker: every claim runs after <c>total-recall transcripts backfill</c> (its ingest, then its own
/// embed step, through <see cref="BackfillCommand"/> and the Part 2 seams) over the 40-session search corpus, with the
/// real bge-small model, and then asks <see cref="TranscriptSearchService.Search"/>. The worker is never called
/// directly.
/// </summary>
[Collection("ConsoleCapture")]
public sealed class TranscriptSearchBackfillAcceptanceTests : IDisposable
{
    private const string Host = "claude-code";

    private readonly ITestOutputHelper _output;
    private readonly TranscriptsHarness _h = new();
    private readonly ClaudeFixtureHome _home = new();
    private IEmbedder? _searchEmbedder;

    public TranscriptSearchBackfillAcceptanceTests(ITestOutputHelper output)
    {
        _output = output;
        _h.Interactive = false; // backfill needs no terminal
    }

    public void Dispose()
    {
        (_searchEmbedder as IDisposable)?.Dispose();
        _h.Dispose();
        _home.Dispose();
    }

    // ---- the corpus through backfill ---------------------------------------------------------------------------------

    /// <summary>
    /// Turns the archive on in the harness's data directory, places the search corpus in the fresh fixture home and runs
    /// <c>backfill</c> with the real embedder; asserts that every chunk was embedded and prints the duration.
    /// </summary>
    private SearchCorpus BackfillCorpus()
    {
        _h.WriteConfig("[transcripts]\nenabled = true\n");
        _h.CreateArchive();
        _h.Inspector = cfg => BgeModel.Inspect(cfg);
        var corpus = SearchFixtureCorpus.Place(_home);

        var sw = Stopwatch.StartNew();
        var code = Backfill();
        sw.Stop();

        Assert.True(code == 0, "backfill failed: " + _h.Out + _h.Err);
        var chunks = _h.Scalar("SELECT count(*) FROM tx_chunk");
        var sessions = _h.Scalar("SELECT count(*) FROM tx_session");
        Assert.Equal(corpus.Sessions.Count, sessions);
        Assert.Equal(chunks, _h.Scalar("SELECT count(*) FROM tx_chunk WHERE embedded = 1"));
        Assert.Equal(chunks, _h.Scalar("SELECT count(*) FROM tx_chunk_vec"));
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"corpus: {sessions} sessions, {chunks} chunks; backfill (ingest and its embed step, real bge-small) took {sw.Elapsed.TotalSeconds:F1} s"));
        return corpus;
    }

    private int Backfill(params string[] args) =>
        _h.Run(h => h.Backfill(_ => new ClaudeCodeAdapter(_home.ProjectsDir)), args);

    private TranscriptSearchService Service()
    {
        _searchEmbedder ??= BgeModel.Embedder(_h.ReadEmbeddingConfig());
        var embedder = _searchEmbedder;
        return new TranscriptSearchService(() => new TranscriptSearchContext(
            new TranscriptLocation(_h.DbPath, IsMainDatabase: true),
            _h.ReadSettings(),
            _h.ReadStorageMode(),
            _h.ReadEmbeddingConfig(),
            embedder));
    }

    private TranscriptSearchResponse Search(
        SearchCorpus corpus, string query, string? scope, string? project = null, string? sessionFixture = null,
        string? since = null, bool includeAutomated = false, int topK = 10)
    {
        var response = Service().Search(
            new TranscriptSearchRequest(
                query,
                scope,
                project,
                sessionFixture is null ? null : SessionKey(corpus, sessionFixture),
                since is null ? null : TranscriptSearchService.ParseSince(since),
                includeAutomated,
                topK),
            CancellationToken.None);
        Assert.Null(response.Error);
        _output.WriteLine($"query \"{query}\" scope={response.Scope} meaning={(response.MeaningOn ? "on" : "off")}:");
        foreach (var hit in response.Results)
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {hit.Score:F3} {Name(corpus, hit.Session)} {hit.ChunkType} [{string.Join(",", hit.Via)}] {Cut(hit.Text)}"));
        return response;
    }

    private TranscriptSearchResponse Search(SearchCorpus corpus, SearchQuery q, int topK = 10) =>
        Search(corpus, q.Query, q.Scope, q.Project, q.SessionFixture, q.Since, q.IncludeAutomated, topK);

    private static SearchQuery Query(SearchCorpus corpus, string name) => corpus.Queries.Single(q => q.Name == name);

    private static string SessionKey(SearchCorpus corpus, string fixture) => Host + ":" + corpus.SessionIdOf(fixture);

    private static string Name(SearchCorpus corpus, string session)
    {
        foreach (var fixture in new[]
                 {
                     "deploy-staging", "flaky-test-retries", "invoice-compaction", "sqlite-busy-error", "retry-backoff",
                     "automated-changelog-sdk", "automated-changelog-opening", "settings-date-locale",
                     "beta-deploy-staging", "beta-dns-cutover",
                 })
            if (SessionKey(corpus, fixture) == session) return fixture;
        return session;
    }

    private static string Cut(string text)
    {
        var line = text.Replace('\n', ' ');
        return line.Length <= 80 ? line : line[..80] + "...";
    }

    private static void AssertMeaningOn(TranscriptSearchResponse r)
    {
        Assert.True(r.MeaningOn, "meaning is off: " + r.MeaningReason);
        Assert.Null(r.MeaningReason);
    }

    private static void AssertInTopThree(SearchCorpus corpus, TranscriptSearchResponse r, string fixture)
    {
        var key = SessionKey(corpus, fixture);
        Assert.True(
            r.Results.Take(3).Any(h => h.Session == key),
            $"{fixture} is not in the top three of \"{r.Query}\": " +
            string.Join(" | ", r.Results.Take(3).Select(h => Name(corpus, h.Session))));
    }

    // ---- query 1: topic / how-to ---------------------------------------------------------------------------------------

    [BgeModelFact]
    public void Topic_HowTo_StagingSite_InTopThree()
    {
        var corpus = BackfillCorpus();
        var q = Query(corpus, "how-to-staging");

        var r = Search(corpus, q);

        AssertMeaningOn(r);
        Assert.Equal(0, r.PendingEmbedding);
        AssertInTopThree(corpus, r, q.ExpectedFixture);
    }

    [BgeModelFact]
    public void Topic_HowTo_FlakyTest_InTopThree()
    {
        var corpus = BackfillCorpus();
        var q = Query(corpus, "how-to-flaky-test");

        var r = Search(corpus, q);

        AssertMeaningOn(r);
        AssertInTopThree(corpus, r, q.ExpectedFixture);
    }

    // ---- query 2: answered only by a compaction summary ----------------------------------------------------------------

    [BgeModelFact]
    public void CompactionSummaryOnly_InTopThree()
    {
        var corpus = BackfillCorpus();
        var q = Query(corpus, "compaction-summary-fact");

        var r = Search(corpus, q);

        AssertMeaningOn(r);
        var key = SessionKey(corpus, q.ExpectedFixture);
        var hit = r.Results.Take(3).FirstOrDefault(h => h.Session == key && h.ChunkType == "compaction-summary");
        Assert.True(hit is not null, "the compaction summary of " + q.ExpectedFixture + " is not in the top three");
        Assert.Contains("50 requests per minute", hit!.Text);
    }

    // ---- query 3: an exact error string --------------------------------------------------------------------------------

    [BgeModelFact]
    public void ExactErrorString_InTopThree()
    {
        var corpus = BackfillCorpus();
        var q = Query(corpus, "exact-error");

        var r = Search(corpus, q);

        AssertMeaningOn(r);
        var key = SessionKey(corpus, q.ExpectedFixture);
        var hit = r.Results.Take(3).FirstOrDefault(h => h.Session == key && h.Via.Contains("phrase"));
        Assert.True(hit is not null, "no phrase hit of " + q.ExpectedFixture + " in the top three");
        Assert.Contains("SQLITE_BUSY: database is locked while writing tx_chunk", hit!.Text);
    }

    // ---- query 4: a session-scope paraphrase ---------------------------------------------------------------------------

    [BgeModelFact]
    public void SessionScope_Paraphrase_FindsItsChunk_With39OtherSessions()
    {
        var corpus = BackfillCorpus();
        Assert.Equal(40, corpus.Sessions.Count); // the named session and 39 others
        var q = Query(corpus, "session-paraphrase");
        var key = SessionKey(corpus, q.SessionFixture!);

        var r = Search(corpus, q);

        AssertMeaningOn(r);
        Assert.Equal("session", r.Scope);
        Assert.NotEmpty(r.Results);
        Assert.All(r.Results, h => Assert.Equal(key, h.Session));
        Assert.Contains(r.Results.Take(3), h => h.Text.Contains("exponential backoff", StringComparison.Ordinal));
        // Both retry-backoff chunks share the query's word "attempts", so the words run alone would find them: the hit must
        // also come from the vector search, or a broken session filter on that search would go unseen.
        Assert.Contains(r.Results.Take(3), h =>
            h.Text.Contains("exponential backoff", StringComparison.Ordinal) && h.Via.Contains("meaning"));
    }

    // ---- query 5: automated sessions -----------------------------------------------------------------------------------

    [BgeModelFact]
    public void Automated_OnlyWithIncludeAutomated_ForProjectAndAll()
    {
        var corpus = BackfillCorpus();
        var hidden = Query(corpus, "automated-hidden-without-flag");
        var shown = Query(corpus, "automated-found-with-flag");
        Assert.Equal(hidden.Query, shown.Query);
        var automated = new[] { "automated-changelog-sdk", "automated-changelog-opening" }
            .Select(f => SessionKey(corpus, f)).ToArray();

        foreach (var (scope, project) in new (string, string?)[] { ("project", SearchFixtureCorpus.Alpha), ("all", null) })
        {
            var without = Search(corpus, hidden.Query, scope, project, includeAutomated: false, topK: 50);
            AssertMeaningOn(without);
            Assert.NotEmpty(without.Results);
            Assert.DoesNotContain(without.Results, h => automated.Contains(h.Session));
            Assert.All(without.Results, h => Assert.Equal("interactive", h.Kind));

            var with = Search(corpus, shown.Query, scope, project, includeAutomated: true);
            foreach (var session in automated)
                Assert.Contains(with.Results, h => h.Session == session);
            AssertInTopThree(corpus, with, shown.ExpectedFixture);
        }
    }

    [BgeModelFact]
    public void Automated_NamedSession_FoundWithoutFlag()
    {
        var corpus = BackfillCorpus();
        var q = Query(corpus, "automated-found-when-session-named");
        Assert.False(q.IncludeAutomated);

        var r = Search(corpus, q);

        AssertMeaningOn(r);
        Assert.Equal("session", r.Scope);
        AssertInTopThree(corpus, r, q.ExpectedFixture);
        Assert.All(r.Results, h => Assert.Equal("automated", h.Kind));
    }

    // ---- query 6: project scope and since ------------------------------------------------------------------------------

    [BgeModelFact]
    public void ProjectScope_ReturnsOnlyThatProject()
    {
        var corpus = BackfillCorpus();

        foreach (var name in new[] { "project-alpha-staging", "project-beta-staging" })
        {
            var q = Query(corpus, name);
            var r = Search(corpus, q);

            AssertMeaningOn(r);
            Assert.Equal("project", r.Scope);
            Assert.NotEmpty(r.Results);
            Assert.All(r.Results, h => Assert.Equal(q.Project, h.Project));
            AssertInTopThree(corpus, r, q.ExpectedFixture);
        }
    }

    [BgeModelFact]
    public void Since_ExcludesOlder()
    {
        var corpus = BackfillCorpus();
        var q = Query(corpus, "since-staging");
        var since = TranscriptSearchService.ParseSince(q.Since!);

        var unfiltered = Search(corpus, q.Query, q.Scope);
        Assert.Contains(unfiltered.Results, h => At(h) < since); // the filter has something to exclude

        var r = Search(corpus, q);

        AssertMeaningOn(r);
        Assert.NotEmpty(r.Results);
        Assert.All(r.Results, h => Assert.True(At(h) >= since, $"{h.Ts} is before {q.Since}"));
        AssertInTopThree(corpus, r, q.ExpectedFixture);
    }

    private static DateTimeOffset At(TranscriptSearchHit h)
    {
        Assert.True(
            DateTimeOffset.TryParse(h.Ts, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at),
            "a hit without a time: " + h.Ts);
        return at;
    }

    // ---- query 7: a chunk without a vector -----------------------------------------------------------------------------

    [BgeModelFact]
    public void ChunkWithoutVector_FoundByWords_PendingEmbeddingCountsIt()
    {
        var corpus = BackfillCorpus();

        // A second session, ingested by 'backfill --no-embed': its chunks stay embedded = 0.
        var cwd = corpus.ProjectCwd(SearchFixtureCorpus.Alpha);
        var dir = Path.Combine(_home.ProjectsDir, ClaudeFixtureHome.EncodeDir(cwd));
        var id = "b0000001-0000-4000-8000-000000000000";
        var late = new PlacedSession(id, Path.Combine(dir, id + ".jsonl"), cwd);
        File.WriteAllBytes(late.Path, Array.Empty<byte>());
        _home.AppendExchange(late,
            "How do we reconcile the kumquat ledger with the warehouse tally?",
            "Match each kumquat ledger line to the warehouse tally by crate number, then flag the crates whose counts differ.",
            secondsAfterBase: 5000);
        var before = _h.Scalar("SELECT count(*) FROM tx_chunk");

        Assert.Equal(0, Backfill("--no-embed"));

        var waiting = _h.Scalar(
            "SELECT count(*) FROM tx_chunk c JOIN tx_session s ON s.id = c.session_id " +
            $"WHERE s.host_session_id = '{id}' AND c.embedded = 0");
        Assert.True(waiting >= 1, "the late session was not ingested");
        Assert.Equal(before + waiting, _h.Scalar("SELECT count(*) FROM tx_chunk"));
        Assert.Equal(waiting, _h.Scalar("SELECT count(*) FROM tx_chunk WHERE embedded = 0"));

        var r = Search(corpus, "kumquat ledger warehouse tally", "all");

        AssertMeaningOn(r); // on for the others
        Assert.True(r.PendingEmbedding >= 1);
        Assert.Equal(waiting, r.PendingEmbedding);
        var hit = r.Results.Take(3).FirstOrDefault(h => h.Session == Host + ":" + id);
        Assert.True(hit is not null, "the late session is not in the top three");
        Assert.Contains("words", hit!.Via);
        Assert.DoesNotContain("meaning", hit.Via);
    }
}
