using System;
using System.Globalization;
using System.Linq;
using TotalRecall.Infrastructure.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

/// <summary>The store's status, forget, unforget and exclusion primitives (spec section 6).</summary>
public sealed class TranscriptStoreTests : IDisposable
{
    private readonly TranscriptTestDb _db = new();

    public void Dispose() => _db.Dispose();

    private ITranscriptStore Store => _db.Store;

    private static DateTimeOffset Parse(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    // ---- Status ----

    [Fact]
    public void Status_EmptyArchive()
    {
        var s = Store.Status();

        Assert.Equal(2, s.SchemaVersion);
        Assert.Equal(_db.Path, s.Location);
        Assert.Equal(0, s.Sessions);
        Assert.Equal(0, s.Chunks);
        Assert.Empty(s.ForgottenSessions);
        Assert.Empty(s.ForgottenProjects);
        Assert.Null(s.IngestNotBefore);
    }

    [Fact]
    public void Status_CountsSessionsChunksAndKinds()
    {
        var a = _db.AddSession("pi", "s1");
        var b = _db.AddSession("pi", "s2", kind: "automated");
        _db.AddSession("claude-code", "s3");
        _db.AddChunk(a, "one");
        _db.AddChunk(a, "two", exchangeStart: 1);
        _db.AddChunk(b, "three");

        var s = Store.Status();

        Assert.Equal(3, s.Sessions);
        Assert.Equal(3, s.Chunks);
        Assert.Equal(2, s.SessionsByKind["interactive"]);
        Assert.Equal(1, s.SessionsByKind["automated"]);
    }

    [Fact]
    public void Status_ListsForgottenRecordsAndCutoff()
    {
        _db.AddSession("pi", "s1", projectKey: "owner/repo", cwd: "/w/repo", projectRoot: "/w/repo");
        Store.ForgetSession("pi", "gone");
        Store.ForgetProject("owner/repo");
        var now = new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);
        Store.ForgetAll(now);

        var s = Store.Status();

        var fs = Assert.Single(s.ForgottenSessions);
        Assert.Equal("pi", fs.Host);
        Assert.Equal("gone", fs.HostSessionId);
        Assert.False(string.IsNullOrEmpty(fs.At));
        var fp = Assert.Single(s.ForgottenProjects);
        Assert.Equal("owner/repo", fp.ProjectKey);
        Assert.Equal(new[] { "/w/repo" }, fp.Roots);
        Assert.NotNull(s.IngestNotBefore);
        Assert.True(Parse(s.IngestNotBefore!) >= now);
    }

    // ---- Count* agree with Forget* ----

    [Fact]
    public void CountSession_MatchesForgetSession()
    {
        var a = _db.AddSession("pi", "s1");
        _db.AddChunk(a, "one");
        _db.AddChunk(a, "two", exchangeStart: 1);
        _db.AddChunk(_db.AddSession("pi", "s2"), "other");

        var count = Store.CountSession("pi", "s1");
        var result = Store.ForgetSession("pi", "s1");

        Assert.Equal(new ForgetCount(1, 2), count);
        Assert.Equal(count.Sessions, result.Sessions);
        Assert.Equal(count.Chunks, result.Chunks);
    }

    [Fact]
    public void CountProject_MatchesForgetProject()
    {
        _db.AddChunk(_db.AddSession("pi", "s1", projectKey: "k"), "one");
        _db.AddChunk(_db.AddSession("pi", "s2", projectKey: "k"), "two");
        _db.AddChunk(_db.AddSession("pi", "s3", projectKey: "other"), "three");

        var count = Store.CountProject("k");
        var result = Store.ForgetProject("k");

        Assert.Equal(new ForgetCount(2, 2), count);
        Assert.Equal(2, result.Sessions);
        Assert.Equal(2, result.Chunks);
    }

    [Fact]
    public void CountAll_MatchesForgetAll()
    {
        _db.AddChunk(_db.AddSession("pi", "s1"), "one");
        _db.AddChunk(_db.AddSession("pi", "s2"), "two");
        _db.AddSession("pi", "s3");

        var count = Store.CountAll();
        var result = Store.ForgetAll(DateTimeOffset.UtcNow);

        Assert.Equal(new ForgetCount(3, 2), count);
        Assert.Equal(3, result.Sessions);
        Assert.Equal(2, result.Chunks);
    }

    // ---- ForgetSession ----

    [Fact]
    public void ForgetSession_RemovesRowsAndIndex_WritesRecord_IsIdempotent()
    {
        var a = _db.AddSession("pi", "s1");
        _db.AddChunk(a, "alpha bravo");
        var keep = _db.AddSession("pi", "s2");
        _db.AddChunk(keep, "charlie");

        var result = Store.ForgetSession("pi", "s1");

        Assert.Equal(1, result.Sessions);
        Assert.Equal(1, result.Chunks);
        Assert.True(result.WalTruncated);
        Assert.Null(result.Warning);
        Assert.Equal(0, _db.Scalar("SELECT count(*) FROM tx_session WHERE host_session_id = 's1'"));
        Assert.Equal(0, _db.Scalar($"SELECT count(*) FROM tx_chunk WHERE session_id = {a}"));
        Assert.Equal(0, _db.Scalar("SELECT count(*) FROM tx_chunk_fts WHERE tx_chunk_fts MATCH 'alpha'"));
        Assert.Equal(1, _db.Scalar("SELECT count(*) FROM tx_chunk_fts WHERE tx_chunk_fts MATCH 'charlie'"));
        Assert.Equal(1, _db.Scalar(
            "SELECT count(*) FROM tx_forgotten WHERE kind = 'session' AND host = 'pi' AND host_session_id = 's1' " +
            "AND project_key IS NULL AND roots IS NULL"));

        var again = Store.ForgetSession("pi", "s1");

        Assert.Equal(0, again.Sessions);
        Assert.Equal(0, again.Chunks);
        Assert.Equal(1, _db.Scalar("SELECT count(*) FROM tx_forgotten"));
    }

    [Fact]
    public void ForgetSession_MissingSession_StillWritesRecord()
    {
        var result = Store.ForgetSession("pi", "never");

        Assert.Equal(0, result.Sessions);
        Assert.Equal(0, result.Chunks);
        Assert.True(Store.IsExcluded("pi", "never", null, null));
    }

    // ---- ForgetProject ----

    [Fact]
    public void ForgetProject_RemovesSessions_RecordsDistinctProjectRoots()
    {
        _db.AddChunk(_db.AddSession("pi", "s1", projectKey: "k", cwd: "/w/a/sub", projectRoot: "/w/a"), "one");
        _db.AddChunk(_db.AddSession("pi", "s2", projectKey: "k", cwd: "/w/a", projectRoot: "/w/a"), "two");
        _db.AddSession("claude-code", "s3", projectKey: "k", cwd: "/w/b/x", projectRoot: "/w/b");
        _db.AddSession("pi", "s4", projectKey: "k", cwd: "/w/nonroot", projectRoot: null);
        _db.AddSession("pi", "s5", projectKey: "other", cwd: "/w/c", projectRoot: "/w/c");

        var result = Store.ForgetProject("k");

        Assert.Equal(4, result.Sessions);
        Assert.Equal(2, result.Chunks);
        Assert.Equal(0, _db.Scalar("SELECT count(*) FROM tx_session WHERE project_key = 'k'"));
        Assert.Equal(1, _db.Scalar("SELECT count(*) FROM tx_session"));
        Assert.Equal(1, _db.Scalar(
            "SELECT count(*) FROM tx_forgotten WHERE kind = 'project' AND project_key = 'k' AND host IS NULL AND host_session_id IS NULL"));
        var fp = Assert.Single(Store.Status().ForgottenProjects);
        Assert.Equal(new[] { "/w/a", "/w/b" }, fp.Roots.OrderBy(r => r, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void ForgetProject_Repeat_MergesRoots()
    {
        _db.AddSession("pi", "s1", projectKey: "k", cwd: "/w/a", projectRoot: "/w/a");
        Store.ForgetProject("k");
        _db.AddSession("pi", "s2", projectKey: "k", cwd: "/w/b", projectRoot: "/w/b");
        _db.AddSession("pi", "s3", projectKey: "k", cwd: "/w/a", projectRoot: "/w/a");

        var result = Store.ForgetProject("k");

        Assert.Equal(2, result.Sessions);
        var fp = Assert.Single(Store.Status().ForgottenProjects);
        Assert.Equal(new[] { "/w/a", "/w/b" }, fp.Roots.OrderBy(r => r, StringComparer.Ordinal).ToArray());
        Assert.Equal(1, _db.Scalar("SELECT count(*) FROM tx_forgotten"));
    }

    [Fact]
    public void ForgetProject_NeverArchivedKey_WritesRecordWithNoRoots()
    {
        var result = Store.ForgetProject("ghost/repo");

        Assert.Equal(0, result.Sessions);
        var fp = Assert.Single(Store.Status().ForgottenProjects);
        Assert.Equal("ghost/repo", fp.ProjectKey);
        Assert.Empty(fp.Roots);
        Assert.True(Store.IsExcluded("pi", "x", "ghost/repo", null));
    }

    // ---- ForgetAll ----

    [Fact]
    public void ForgetAll_EmptiesTables_SetsCutoff()
    {
        _db.AddChunk(_db.AddSession("pi", "s1"), "one");
        var now = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

        Store.ForgetAll(now);

        Assert.Equal(0, _db.Scalar("SELECT count(*) FROM tx_session"));
        Assert.Equal(0, _db.Scalar("SELECT count(*) FROM tx_chunk"));
        Assert.Equal(0, _db.Scalar("SELECT count(*) FROM tx_chunk_fts WHERE tx_chunk_fts MATCH 'one'"));
        Assert.Equal(now, Parse(Store.Status().IngestNotBefore!));
    }

    [Fact]
    public void ForgetAll_CutoffIsMaxOfExistingAndNow()
    {
        var later = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
        var earlier = later.AddDays(-1);

        Store.ForgetAll(later);
        Store.ForgetAll(earlier);
        Assert.Equal(later, Parse(Store.Status().IngestNotBefore!));

        var latest = later.AddHours(1);
        Store.ForgetAll(latest);
        Assert.Equal(latest, Parse(Store.Status().IngestNotBefore!));
    }

    [Fact]
    public void ForgetAll_KeepsExclusionRecords()
    {
        Store.ForgetSession("pi", "s1");
        Store.ForgetAll(DateTimeOffset.UtcNow);
        Assert.True(Store.IsExcluded("pi", "s1", null, null));
    }

    // ---- Unforget / ClearCutoff ----

    [Fact]
    public void UnforgetSession_RemovesRecord_ReportsWhetherRemoved()
    {
        Store.ForgetSession("pi", "s1");

        Assert.True(Store.UnforgetSession("pi", "s1"));
        Assert.False(Store.IsExcluded("pi", "s1", null, null));
        Assert.False(Store.UnforgetSession("pi", "s1"));
    }

    [Fact]
    public void UnforgetProject_RemovesRecord_ReportsWhetherRemoved()
    {
        Store.ForgetProject("k");

        Assert.True(Store.UnforgetProject("k"));
        Assert.False(Store.IsExcluded("pi", "s", "k", null));
        Assert.False(Store.UnforgetProject("k"));
    }

    [Fact]
    public void Unforget_DoesNotCrossKinds()
    {
        Store.ForgetSession("pi", "s1");
        Store.ForgetProject("k");

        Assert.False(Store.UnforgetProject("pi"));
        Assert.False(Store.UnforgetSession("k", "k"));
        Assert.Equal(2, _db.Scalar("SELECT count(*) FROM tx_forgotten"));
    }

    [Fact]
    public void ClearCutoff_RemovesCutoff_ReportsWhetherRemoved()
    {
        Assert.False(Store.ClearCutoff());
        Store.ForgetAll(DateTimeOffset.UtcNow);

        Assert.True(Store.ClearCutoff());
        Assert.Null(Store.Status().IngestNotBefore);
        Assert.False(Store.IsBeforeCutoff(DateTimeOffset.UtcNow.AddYears(-10)));
        Assert.False(Store.ClearCutoff());
    }

    // ---- IsExcluded ----

    [Fact]
    public void IsExcluded_ForgottenSession()
    {
        Store.ForgetSession("pi", "s1");

        Assert.True(Store.IsExcluded("pi", "s1", "any", "/anywhere"));
        Assert.False(Store.IsExcluded("pi", "s2", "any", "/anywhere"));
        Assert.False(Store.IsExcluded("claude-code", "s1", "any", "/anywhere"));
    }

    [Fact]
    public void IsExcluded_ProjectKeyMatch()
    {
        Store.ForgetProject("owner/repo");

        Assert.True(Store.IsExcluded("pi", "new", "owner/repo", null));
        Assert.False(Store.IsExcluded("pi", "new", "owner/repo2", null));
        Assert.False(Store.IsExcluded("pi", "new", null, null));
    }

    [Fact]
    public void IsExcluded_CwdUnderRoot_OnDirectoryBoundary()
    {
        _db.AddSession("pi", "s1", projectKey: "k", cwd: "/a/b", projectRoot: "/a/b");
        Store.ForgetProject("k");

        Assert.True(Store.IsExcluded("pi", "new", "other", "/a/b"));
        Assert.True(Store.IsExcluded("pi", "new", "other", "/a/b/c"));
        Assert.True(Store.IsExcluded("pi", "new", null, "/a/b/c/d"));
        Assert.False(Store.IsExcluded("pi", "new", "other", "/a/bc"));
        Assert.False(Store.IsExcluded("pi", "new", "other", "/a"));
        Assert.False(Store.IsExcluded("pi", "new", "other", "/x/a/b"));
    }

    [Fact]
    public void IsExcluded_CwdComparison_IsCaseSensitiveExceptOnWindows()
    {
        _db.AddSession("pi", "s1", projectKey: "k", cwd: "/a/b", projectRoot: "/a/b");
        Store.ForgetProject("k");

        Assert.Equal(OperatingSystem.IsWindows(), Store.IsExcluded("pi", "new", "other", "/A/B/c"));
    }

    [Fact]
    public void IsExcluded_NothingRecorded_IsFalse()
    {
        Assert.False(Store.IsExcluded("pi", "s1", "owner/repo", "/a/b"));
    }

    // ---- IsBeforeCutoff ----

    [Fact]
    public void IsBeforeCutoff_ComparesWithCutoff()
    {
        var cutoff = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
        Assert.False(Store.IsBeforeCutoff(cutoff.AddYears(-1)));

        Store.ForgetAll(cutoff);

        Assert.True(Store.IsBeforeCutoff(cutoff.AddSeconds(-1)));
        Assert.False(Store.IsBeforeCutoff(cutoff));
        Assert.False(Store.IsBeforeCutoff(cutoff.AddSeconds(1)));
        // the same instant in another offset
        Assert.True(Store.IsBeforeCutoff(new DateTimeOffset(2026, 11, 18, 13, 59, 0, TimeSpan.FromHours(2))));
        Assert.False(Store.IsBeforeCutoff(new DateTimeOffset(2026, 11, 18, 14, 0, 1, TimeSpan.FromHours(2))));
    }

    // ---- Identifiers are data ----

    [Fact]
    public void InjectionLikeProjectKey_IsData()
    {
        const string key = "x' OR '1'='1";
        _db.AddChunk(_db.AddSession("pi", "s1", projectKey: key), "one");
        _db.AddChunk(_db.AddSession("pi", "s2", projectKey: "innocent"), "two");

        Assert.Equal(new ForgetCount(1, 1), Store.CountProject(key));
        var result = Store.ForgetProject(key);

        Assert.Equal(1, result.Sessions);
        Assert.Equal(1, _db.Scalar("SELECT count(*) FROM tx_session WHERE project_key = 'innocent'"));
        Assert.True(Store.IsExcluded("pi", "n", key, null));
        Assert.False(Store.IsExcluded("pi", "n", "innocent", null));
        Assert.Equal(key, Assert.Single(Store.Status().ForgottenProjects).ProjectKey);
        Assert.True(Store.UnforgetProject(key));
    }

    [Fact]
    public void WildcardAndColonHostIds_AreData()
    {
        const string host = "h%o_s:t";
        const string id = "%_:id";
        _db.AddChunk(_db.AddSession(host, id), "one");
        _db.AddChunk(_db.AddSession("hXoYs:t", "aXid"), "two");

        Assert.Equal(new ForgetCount(1, 1), Store.CountSession(host, id));
        var result = Store.ForgetSession(host, id);

        Assert.Equal(1, result.Sessions);
        Assert.Equal(1, _db.Scalar("SELECT count(*) FROM tx_session"));
        Assert.True(Store.IsExcluded(host, id, null, null));
        Assert.False(Store.IsExcluded("hXoYs:t", "aXid", null, null));
        var fs = Assert.Single(Store.Status().ForgottenSessions);
        Assert.Equal(host, fs.Host);
        Assert.Equal(id, fs.HostSessionId);
        Assert.True(Store.UnforgetSession(host, id));
    }

    [Fact]
    public void WildcardCwdRoot_IsData()
    {
        _db.AddSession("pi", "s1", projectKey: "k", cwd: "/a/%", projectRoot: "/a/%");
        Store.ForgetProject("k");

        Assert.True(Store.IsExcluded("pi", "n", null, "/a/%/x"));
        Assert.False(Store.IsExcluded("pi", "n", null, "/a/b"));
    }

    // ---- Ingest reads ----

    [Fact]
    public void GetIngestState_NoRow_IsNull()
    {
        Assert.Null(Store.GetIngestState("claude-code", "nope"));
    }

    [Fact]
    public void GetIngestState_ReadsEveryField()
    {
        using (var conn = _db.Connect())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "INSERT INTO tx_session (host, host_session_id, project_key, project_key_source, project_root, cwd, " +
                "started_at, ended_at, kind, title, source_path, source_size, source_mtime, source_fingerprint, " +
                "ingested_offset, exchange_count, settled_user_turns, built_under_cutoff) VALUES " +
                "('claude-code', 'abc', 'owner/repo', 'path', '/w/repo', '/w/repo/sub', " +
                "'2026-01-01T00:00:00.0000000Z', '2026-01-02T00:00:00.0000000Z', 'automated', 'A title', '/logs/abc.jsonl', 1234, 5678, " +
                "'fp-1', 99, 4, 3, '2026-01-01T12:00:00.0000000Z')";
            cmd.ExecuteNonQuery();
        }

        var state = Store.GetIngestState("claude-code", "abc");

        Assert.Equal(
            new SessionIngestState(1234, 5678, "fp-1", 99, 4, 3, "automated", "owner/repo", "path", "/w/repo",
                "/w/repo/sub", "A title", "2026-01-01T00:00:00.0000000Z", "2026-01-02T00:00:00.0000000Z",
                "2026-01-01T12:00:00.0000000Z", "/logs/abc.jsonl"),
            state);
    }

    [Fact]
    public void IngestNotBefore_NoCutoff_IsNull()
    {
        Assert.Null(Store.IngestNotBefore());
    }

    [Fact]
    public void IngestNotBefore_AfterForgetAll_RawEqualsStatusText_AndAtParses()
    {
        var now = new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);
        Store.ForgetAll(now);

        var cutoff = Store.IngestNotBefore();

        Assert.NotNull(cutoff);
        Assert.Equal(Store.Status().IngestNotBefore, cutoff!.Raw);
        Assert.Equal(now, cutoff.At);
    }

    [Fact]
    public void Status_ByProjectAndKind_OrderedByChunksDescThenKeyThenKind()
    {
        var cInt = _db.AddSession("pi", "c1", projectKey: "c/c");
        _db.AddChunk(cInt, "x1"); _db.AddChunk(cInt, "x2", exchangeStart: 1); _db.AddChunk(cInt, "x3", exchangeStart: 2);
        var bInt = _db.AddSession("pi", "b1", projectKey: "b/b");
        _db.AddChunk(bInt, "y1"); _db.AddChunk(bInt, "y2", exchangeStart: 1);
        _db.AddChunk(_db.AddSession("pi", "a1", projectKey: "a/a"), "z1");
        _db.AddChunk(_db.AddSession("pi", "a2", projectKey: "a/a", kind: "automated"), "z2");
        _db.AddChunk(_db.AddSession("pi", "b2", projectKey: "b/b", kind: "automated"), "z3");
        _db.AddSession("pi", "c2", projectKey: "c/c", kind: "automated");

        var rows = Store.Status().ByProjectAndKind;

        Assert.Equal(
            new[]
            {
                new ProjectKindCount("c/c", "interactive", 1, 3),
                new ProjectKindCount("b/b", "interactive", 1, 2),
                new ProjectKindCount("a/a", "automated", 1, 1),
                new ProjectKindCount("a/a", "interactive", 1, 1),
                new ProjectKindCount("b/b", "automated", 1, 1),
                new ProjectKindCount("c/c", "automated", 1, 0),
            },
            rows);
    }
}
