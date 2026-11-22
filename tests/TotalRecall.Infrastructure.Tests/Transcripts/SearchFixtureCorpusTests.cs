using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Transcripts;
using TotalRecall.TestSupport.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

/// <summary>
/// The query-set corpus of Part 3 (spec section 8): placement, the query file, what a plain backfill makes of the
/// hand-written sessions (no model needed), the model-available attribute's resolution rule and the recording fake.
/// </summary>
public sealed class SearchFixtureCorpusTests : IDisposable
{
    private readonly ClaudeFixtureHome _home = new();
    private readonly List<TranscriptTestDb> _dbs = new();

    public void Dispose()
    {
        foreach (var db in _dbs) db.Dispose();
        _home.Dispose();
    }

    private string UserHome => Path.Combine(_home.Root, "me");

    private (SearchCorpus Corpus, TranscriptTestDb Db) BackfilledCorpus()
    {
        Directory.CreateDirectory(UserHome);
        var corpus = SearchFixtureCorpus.Place(_home);
        var db = new TranscriptTestDb(userHome: UserHome);
        _dbs.Add(db);
        var summary = new TranscriptBackfill(db.Store, new ClaudeCodeAdapter(_home.ProjectsDir), resolver: null, userHome: UserHome)
            .Run(CancellationToken.None);
        Assert.Equal(40, summary.New);
        Assert.Equal(0, summary.Failed);
        return (corpus, db);
    }

    private static string Quote(string s) => s.Replace("'", "''", StringComparison.Ordinal);

    private static long SessionRowId(TranscriptTestDb db, string hostSessionId) =>
        db.Scalar($"SELECT id FROM tx_session WHERE host_session_id = '{Quote(hostSessionId)}'");

    [Fact]
    public void Place_Writes40Sessions_InTwoProjects()
    {
        var corpus = SearchFixtureCorpus.Place(_home);

        var files = Directory.GetFiles(_home.ProjectsDir, "*.jsonl", SearchOption.AllDirectories);
        Assert.Equal(40, files.Length);
        Assert.Equal(40, corpus.Sessions.Count);
        Assert.Equal(40, corpus.Sessions.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, Directory.GetDirectories(_home.ProjectsDir).Length);
        Assert.Equal(corpus.Sessions.Count, corpus.Sessions.Count(s => File.Exists(s.Path)));
    }

    [Fact]
    public void Fixtures_AreNaturalDialogue_NoZqxMarkers()
    {
        var dir = ClaudeFixtureHome.SearchFixturesDir;
        var files = Directory.GetFiles(dir, "*.jsonl");

        Assert.Equal(10, files.Length);
        foreach (var f in files)
            Assert.DoesNotContain("zqx", File.ReadAllText(f), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Queries_Parse_AndEveryReferencedFixtureAndProjectResolves()
    {
        var corpus = SearchFixtureCorpus.Place(_home);

        Assert.NotEmpty(corpus.Queries);
        var names = corpus.Queries.Select(q => q.Name).ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        foreach (var q in corpus.Queries)
        {
            Assert.False(string.IsNullOrWhiteSpace(q.Query), q.Name);
            Assert.Contains(q.Scope, new[] { "session", "project", "all" });
            Assert.False(string.IsNullOrWhiteSpace(q.Kind), q.Name);
            Assert.False(string.IsNullOrWhiteSpace(q.ExpectedFixture), q.Name);
            Assert.True(File.Exists(Path.Combine(ClaudeFixtureHome.SearchFixturesDir, q.ExpectedFixture + ".jsonl")), q.Name);
            Assert.NotEmpty(corpus.SessionIdOf(q.ExpectedFixture));
            if (q.Scope == "session")
            {
                Assert.False(string.IsNullOrEmpty(q.SessionFixture), q.Name);
                Assert.NotEmpty(corpus.SessionIdOf(q.SessionFixture!));
            }
            if (q.Scope == "project")
            {
                Assert.False(string.IsNullOrEmpty(q.Project), q.Name);
                Assert.NotEmpty(corpus.ProjectCwd(q.Project!));
            }
            if (q.Since is not null)
                Assert.True(DateTimeOffset.TryParse(q.Since, out _), q.Name);
        }

        // The kinds of query the spec lists.
        var kinds = corpus.Queries.Select(q => q.Kind).ToHashSet(StringComparer.Ordinal);
        foreach (var kind in new[] { "how-to", "compaction-summary", "error", "session-paraphrase", "automated-excluded",
                     "automated-included", "automated-session", "project", "since" })
            Assert.Contains(kind, kinds);
    }

    [Fact]
    public void SessionIdOf_AndProjectCwd_AreStable_AndUnknownNamesThrow()
    {
        var corpus = SearchFixtureCorpus.Place(_home);

        Assert.Equal(corpus.SessionIdOf("deploy-staging"), corpus.SessionIdOf("deploy-staging"));
        Assert.NotEqual(corpus.SessionIdOf("deploy-staging"), corpus.SessionIdOf("flaky-test-retries"));
        Assert.NotEqual(corpus.ProjectCwd("alpha"), corpus.ProjectCwd("beta"));
        Assert.Throws<ArgumentException>(() => corpus.SessionIdOf("no-such-fixture"));
        Assert.Throws<ArgumentException>(() => corpus.ProjectCwd("no-such-project"));
    }

    [Fact]
    public void Backfill_AutomatedSessionsAreAutomated_TheRestInteractive()
    {
        var (corpus, db) = BackfilledCorpus();

        var automated = new[] { "automated-changelog-sdk", "automated-changelog-opening" }
            .Select(corpus.SessionIdOf).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(2, db.Scalar("SELECT count(*) FROM tx_session WHERE kind = 'automated'"));
        Assert.Equal(38, db.Scalar("SELECT count(*) FROM tx_session WHERE kind = 'interactive'"));
        foreach (var id in automated)
            Assert.Equal("automated", db.Text($"SELECT kind FROM tx_session WHERE host_session_id = '{Quote(id)}'"));
    }

    [Fact]
    public void Backfill_TwoProjects_AndAboutAHundredChunks()
    {
        var (corpus, db) = BackfilledCorpus();

        Assert.Equal(2, db.Scalar("SELECT count(DISTINCT project_key) FROM tx_session"));
        Assert.Equal(
            "beta",
            db.Text($"SELECT project_key FROM tx_session WHERE host_session_id = '{Quote(corpus.SessionIdOf("beta-deploy-staging"))}'"));
        Assert.Equal(
            "alpha",
            db.Text($"SELECT project_key FROM tx_session WHERE host_session_id = '{Quote(corpus.SessionIdOf("deploy-staging"))}'"));
        var chunks = db.Scalar("SELECT count(*) FROM tx_chunk");
        Assert.InRange(chunks, 80, 130);
    }

    [Fact]
    public void Backfill_CompactionSession_HoldsTheFactOnlyInItsSummaryChunk()
    {
        var (corpus, db) = BackfilledCorpus();
        var sid = SessionRowId(db, corpus.SessionIdOf("invoice-compaction"));

        Assert.Equal(1, db.Scalar($"SELECT count(*) FROM tx_chunk WHERE session_id = {sid} AND chunk_type = 'compaction-summary'"));
        Assert.Equal(
            1,
            db.Scalar(
                $"SELECT count(*) FROM tx_chunk WHERE session_id = {sid} AND chunk_type = 'compaction-summary' " +
                "AND content LIKE '%the invoice export is rate limited to 50 requests per minute by the vendor%'"));
        Assert.Equal(
            0,
            db.Scalar($"SELECT count(*) FROM tx_chunk WHERE session_id = {sid} AND chunk_type <> 'compaction-summary' AND lower(content) LIKE '%rate limit%'"));
        Assert.Equal(
            0,
            db.Scalar($"SELECT count(*) FROM tx_chunk WHERE chunk_type <> 'compaction-summary' AND lower(content) LIKE '%rate limit%'"));
        Assert.True(db.Scalar($"SELECT count(*) FROM tx_chunk WHERE session_id = {sid} AND chunk_type = 'dialogue'") >= 2);
    }

    [Fact]
    public void Backfill_ErrorSession_HasOneErrorChunkHoldingTheExactText()
    {
        const string error = "SQLITE_BUSY: database is locked while writing tx_chunk";
        var (corpus, db) = BackfilledCorpus();
        var sid = SessionRowId(db, corpus.SessionIdOf("sqlite-busy-error"));

        Assert.Equal(1, db.Scalar($"SELECT count(*) FROM tx_chunk WHERE session_id = {sid} AND has_error = 1"));
        Assert.Contains(error, db.Text($"SELECT content FROM tx_chunk WHERE session_id = {sid} AND has_error = 1"));
        Assert.Equal(1, db.Scalar($"SELECT count(*) FROM tx_chunk WHERE content LIKE '%{error}%'"));
    }

    [Fact]
    public void Backfill_DeployStaging_HasAChunkAfterTheSinceCutoffAndOthersBefore()
    {
        var (corpus, db) = BackfilledCorpus();
        var sid = SessionRowId(db, corpus.SessionIdOf("deploy-staging"));
        var since = corpus.Queries.Single(q => q.Kind == "since").Since!;
        var cutoff = DateTimeOffset.Parse(since, null, System.Globalization.DateTimeStyles.AssumeUniversal)
            .UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(1, db.Scalar($"SELECT count(*) FROM tx_chunk WHERE session_id = {sid} AND ts >= '{cutoff}'"));
        Assert.True(db.Scalar($"SELECT count(*) FROM tx_chunk WHERE session_id = {sid} AND ts < '{cutoff}'") >= 2);
        // Nothing else in the corpus is as late: the since query can only find that chunk.
        Assert.Equal(0, db.Scalar($"SELECT count(*) FROM tx_chunk WHERE session_id <> {sid} AND ts >= '{cutoff}'"));
    }

    [Fact]
    public void Backfill_AutomatedChangelogSessions_AreInAlpha_AndFindableByTheirWords()
    {
        var (corpus, db) = BackfilledCorpus();

        foreach (var name in new[] { "automated-changelog-sdk", "automated-changelog-opening" })
        {
            var id = corpus.SessionIdOf(name);
            Assert.Equal("alpha", db.Text($"SELECT project_key FROM tx_session WHERE host_session_id = '{Quote(id)}'"));
            Assert.Equal(
                1,
                db.Scalar(
                    $"SELECT count(*) FROM tx_chunk c JOIN tx_session s ON s.id = c.session_id WHERE s.host_session_id = '{Quote(id)}' " +
                    "AND lower(c.content) LIKE '%nightly changelog summariser%'"));
        }
    }

    // ---- BgeModelFact -----------------------------------------------------------------------------------------

    private static string TempTree(bool withModel, out string start)
    {
        var root = Path.Combine(Path.GetTempPath(), "tr-bge-" + Guid.NewGuid().ToString("N"));
        start = Path.Combine(root, "a", "b", "bin");
        Directory.CreateDirectory(start);
        if (withModel)
        {
            var dir = Path.Combine(root, "models", "bge-small-en-v1.5");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "model.onnx"), "x");
        }
        return root;
    }

    private static Func<string, string?> Env(string? ci) => name => name == "CI" ? ci : null;

    [Fact]
    public void BgeModelFact_ModelMissing_CiUnset_SkipsWithTheReason()
    {
        var root = TempTree(withModel: false, out var start);
        try
        {
            var r = BgeModelFactAttribute.Resolve(Env(null), start);

            Assert.Equal(BgeModelStatus.Skip, r.Status);
            Assert.Equal("bge model not found (run scripts/fetch-bge-small.sh)", r.Reason);
            Assert.Null(r.ModelPath);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void BgeModelFact_ModelMissing_CiEmpty_StillSkips(string? ci)
    {
        var root = TempTree(withModel: false, out var start);
        try { Assert.Equal(BgeModelStatus.Skip, BgeModelFactAttribute.Resolve(Env(ci), start).Status); }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void BgeModelFact_ModelMissing_CiSet_Fails()
    {
        var root = TempTree(withModel: false, out var start);
        try
        {
            var r = BgeModelFactAttribute.Resolve(Env("1"), start);

            Assert.Equal(BgeModelStatus.Fail, r.Status);
            Assert.Equal("bge model not found (run scripts/fetch-bge-small.sh)", r.Reason);
            Assert.Null(r.ModelPath);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1")]
    public void BgeModelFact_ModelPresent_IsAvailable_FoundWalkingUp(string? ci)
    {
        var root = TempTree(withModel: true, out var start);
        try
        {
            var r = BgeModelFactAttribute.Resolve(Env(ci), start);

            Assert.Equal(BgeModelStatus.Available, r.Status);
            Assert.Equal(Path.Combine(root, "models", "bge-small-en-v1.5", "model.onnx"), r.ModelPath);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void BgeModelFact_Attribute_SkipsOnlyWhenTheModelIsMissingAndCiIsUnset()
    {
        var r = BgeModelFactAttribute.Resolve(Environment.GetEnvironmentVariable, AppContext.BaseDirectory);
        var attribute = new BgeModelFactAttribute();

        if (r.Status == BgeModelStatus.Skip)
            Assert.Equal(r.Reason, attribute.Skip);
        else
            Assert.Null(attribute.Skip);
    }

    // ---- RecordingEmbedder -------------------------------------------------------------------------------------

    [Fact]
    public void RecordingEmbedder_RecordsEveryEmbedAndEmbedQueryText_WithDescriptorAndDeterministicVectors()
    {
        var e = new RecordingEmbedder("openai", 384);

        var v1 = e.Embed("alpha text");
        var v2 = e.EmbedQuery("a query");
        var v3 = e.Embed("alpha text");

        Assert.Equal(new[] { "alpha text", "a query", "alpha text" }, e.Calls);
        Assert.Equal("openai", e.Descriptor.Provider);
        Assert.Equal(384, e.Descriptor.Dimensions);
        Assert.Equal(384, v1.Length);
        Assert.Equal(v1, v3);
        Assert.NotEqual(v1, v2);
        Assert.InRange(Math.Sqrt(v1.Sum(x => (double)x * x)), 0.999, 1.001);
    }

    [Fact]
    public void RecordingEmbedder_ReturnedDimensions_CanDifferFromTheDescriptor()
    {
        var e = new RecordingEmbedder("openai", 384, 768);

        Assert.Equal(384, e.Descriptor.Dimensions);
        Assert.Equal(768, e.Embed("x").Length);
        Assert.Equal(768, e.EmbedQuery("y").Length);
    }
}
