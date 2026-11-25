using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Tests.TestSupport;
using TotalRecall.Infrastructure.Transcripts;
using TotalRecall.TestSupport.Transcripts;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

/// <summary>
/// The search service of the archive (search the archive, spec section 5): effective scope, filters on both candidate
/// sources, the meaning decision through the gate, the fall-back to words, <c>pendingEmbedding</c>, the notes, the
/// text cap and relative scores. A real archive seeded by raw SQL; vectors are unit vectors at a known angle to the
/// query vector the fake embedder returns, so the meaning order is known. No model.
/// </summary>
public sealed class TranscriptSearchServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly EmbedderDescriptor LocalDescriptor = new("local", "bge-small-en-v1.5", "rev1", 384);

    private readonly TranscriptTestDb _db = new();

    public void Dispose() => _db.Dispose();

    // ---- fakes and seeding ----------------------------------------------------------------------------------------

    /// <summary>Records every call; answers <see cref="Unit"/>(0) unless told otherwise; can report a readiness.</summary>
    private sealed class QueryEmbedder : IEmbedder, IEmbedderReadiness
    {
        public QueryEmbedder(EmbedderDescriptor? descriptor = null) => Descriptor = descriptor ?? LocalDescriptor;

        public EmbedderDescriptor Descriptor { get; }
        public List<string> Calls { get; } = new();
        public Func<string, float[]> Answer { get; set; } = _ => Unit(0);
        public EmbedderReadiness Readiness { get; set; } = new(true, null);

        public float[] Embed(string text)
        {
            Calls.Add("embed:" + text);
            return Answer(text);
        }

        public float[] EmbedQuery(string text)
        {
            Calls.Add("query:" + text);
            return Answer(text);
        }

        public EmbedderReadiness CheckReady() => Readiness;
    }

    private static float[] Unit(double angle)
    {
        var v = new float[384];
        v[0] = (float)Math.Cos(angle);
        v[1] = (float)Math.Sin(angle);
        return v;
    }

    private static Core.Config.EmbeddingConfig Embedding(string? provider = null) => TestEmbedding.Config(provider);

    private static TranscriptsSettings Settings(bool allowRemote = false) =>
        new(Enabled: true, DbPath: string.Empty, DbPathError: null, RetentionDays: 0, AllowRemoteEmbedding: allowRemote);

    private TranscriptSearchService Service(IEmbedder embedder, string? provider = null, bool allowRemote = false, TranscriptLocation? location = null) =>
        new(() => new TranscriptSearchContext(
                location ?? new TranscriptLocation(_db.Path, IsMainDatabase: false),
                Settings(allowRemote),
                "sqlite",
                Embedding(provider),
                embedder),
            new ManualTimeProvider(Now));

    private static TranscriptSearchRequest Req(
        string query, string? scope = null, string? project = null, string? session = null,
        DateTimeOffset? since = null, bool includeAutomated = false, int topK = 10,
        CurrentSessionInfo? current = null, string? syncNote = null) =>
        new(query, scope, project, session, since, includeAutomated, topK, current, syncNote);

    private long AddChunk(
        long session, string body, int start, string ts = "2026-11-01T00:00:00.0000000Z", string type = "dialogue",
        bool error = false, double? angle = null, string project = "owner/repo", string kind = "interactive", string? header = "header line")
    {
        using var conn = _db.Connect();
        var content = header is null ? body : header + "\n\n" + body;
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO tx_chunk (session_id, exchange_start, exchange_end, part, chunk_type, has_error, ts, redaction_version, content) " +
            "VALUES ($s, $e, $e + 1, 0, $t, $err, $ts, 1, $c); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$s", session);
        cmd.Parameters.AddWithValue("$e", start);
        cmd.Parameters.AddWithValue("$t", type);
        cmd.Parameters.AddWithValue("$err", error ? 1 : 0);
        cmd.Parameters.AddWithValue("$ts", ts);
        cmd.Parameters.AddWithValue("$c", content);
        var id = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (angle is { } a)
            AddVector(conn, id, session, project, kind, DateTimeOffset.Parse(ts, CultureInfo.InvariantCulture).ToUnixTimeSeconds(), Unit(a));
        return id;
    }

    private static void AddVector(MsSqliteConnection conn, long chunkId, long sessionId, string project, string kind, long ts, float[] vector)
    {
        var bytes = new byte[384 * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO tx_chunk_vec (rowid, session_id, project, kind, ts, embedding) VALUES ($r, $s, $p, $k, $t, $e); " +
            "UPDATE tx_chunk SET embedded = 1 WHERE id = $r;";
        cmd.Parameters.AddWithValue("$r", chunkId);
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$p", project);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$t", ts);
        cmd.Parameters.AddWithValue("$e", bytes);
        cmd.ExecuteNonQuery();
    }

    private void Stamp(EmbedderDescriptor d)
    {
        using var conn = _db.Connect();
        foreach (var (k, v) in new[]
                 {
                     ("embed.provider", d.Provider), ("embed.model", d.Model), ("embed.revision", d.Revision),
                     ("embed.dimensions", d.Dimensions.ToString(CultureInfo.InvariantCulture)),
                 })
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO tx_state (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value";
            cmd.Parameters.AddWithValue("$k", k);
            cmd.Parameters.AddWithValue("$v", v);
            cmd.ExecuteNonQuery();
        }
    }

    private sealed record Seed(long S1, long S2, long S3, long C1a, long C1b, long C2, long C3, long C3Unembedded);

    /// <summary>
    /// s1 claude-code:sess-1 owner/repo interactive; s2 claude-code:sess-2 owner/repo automated; s3 pi:sess-3 other/proj
    /// interactive. Every chunk says "database locked"; s2's and s3's chunks are the nearest by meaning.
    /// </summary>
    private Seed Populate()
    {
        var s1 = _db.AddSession("claude-code", "sess-1", "owner/repo", kind: "interactive", title: "First");
        var s2 = _db.AddSession("claude-code", "sess-2", "owner/repo", kind: "automated", title: "Auto");
        var s3 = _db.AddSession("pi", "sess-3", "other/proj", kind: "interactive", title: "Third");
        var c1a = AddChunk(s1, "the database locked during ingest", 0, "2026-03-01T00:00:00.0000000Z", angle: 0.60);
        var c1b = AddChunk(s1, "database locked again in colour", 2, "2026-03-02T00:00:00.0000000Z", angle: 0.90);
        var c2 = AddChunk(s2, "automated run saw database locked", 0, "2026-03-03T00:00:00.0000000Z", angle: 0.10, kind: "automated");
        var c3 = AddChunk(s3, "another database locked case", 0, "2026-03-04T00:00:00.0000000Z", angle: 0.20, project: "other/proj");
        var c3u = AddChunk(s3, "unembedded database locked note", 2, "2026-03-05T00:00:00.0000000Z");
        Stamp(LocalDescriptor);
        return new Seed(s1, s2, s3, c1a, c1b, c2, c3, c3u);
    }

    private static long[] Ids(TranscriptSearchResponse r) => r.Results.Select(h => h.Id).ToArray();

    // ---- effective scope --------------------------------------------------------------------------------------------

    [Fact]
    public void Scope_ExplicitSession_WinsAndIgnoresProject()
    {
        var seed = Populate();
        var r = Service(new QueryEmbedder()).Search(Req("database locked", scope: "session", project: "other/proj", session: "claude-code:sess-1"), default);
        Assert.Null(r.Error);
        Assert.Equal("session", r.Scope);
        Assert.Null(r.ScopeNote);
        Assert.Equal(new[] { seed.C1a, seed.C1b }.OrderBy(x => x), Ids(r).OrderBy(x => x));
    }

    [Fact]
    public void Scope_SessionArgument_InfersSession()
    {
        var seed = Populate();
        var r = Service(new QueryEmbedder()).Search(Req("database locked", session: "pi:sess-3", project: "owner/repo"), default);
        Assert.Equal("session", r.Scope);
        Assert.Equal(new[] { seed.C3, seed.C3Unembedded }.OrderBy(x => x), Ids(r).OrderBy(x => x));
    }

    [Fact]
    public void Scope_ProjectArgument_InfersProject()
    {
        var seed = Populate();
        var r = Service(new QueryEmbedder()).Search(Req("database locked", project: "other/proj"), default);
        Assert.Equal("project", r.Scope);
        Assert.Equal(new[] { seed.C3, seed.C3Unembedded }.OrderBy(x => x), Ids(r).OrderBy(x => x));
    }

    [Fact]
    public void Scope_NoArgument_IsAll()
    {
        var seed = Populate();
        var r = Service(new QueryEmbedder()).Search(Req("database locked"), default);
        Assert.Equal("all", r.Scope);
        Assert.Null(r.ScopeNote);
        Assert.Equal(new[] { seed.C1a, seed.C1b, seed.C3, seed.C3Unembedded }.OrderBy(x => x), Ids(r).OrderBy(x => x));
    }

    [Fact]
    public void Scope_ProjectWithoutProject_IsAllWithNote()
    {
        var seed = Populate();
        var r = Service(new QueryEmbedder()).Search(Req("database locked", scope: "project"), default);
        Assert.Equal("all", r.Scope);
        Assert.False(string.IsNullOrWhiteSpace(r.ScopeNote));
        Assert.Contains(seed.C3, Ids(r));
        Assert.Contains(seed.C1a, Ids(r));
    }

    [Fact]
    public void Scope_ExplicitProject_WithProject()
    {
        var seed = Populate();
        var r = Service(new QueryEmbedder()).Search(Req("database locked", scope: "project", project: "owner/repo"), default);
        Assert.Equal("project", r.Scope);
        Assert.Equal(new[] { seed.C1a, seed.C1b }.OrderBy(x => x), Ids(r).OrderBy(x => x));
    }

    [Fact]
    public void Scope_SessionWithoutSession_Throws()
    {
        Populate();
        Assert.Throws<ArgumentException>(() => Service(new QueryEmbedder()).Search(Req("database locked", scope: "session"), default));
    }

    [Fact]
    public void Scope_AllIgnoresProject()
    {
        var seed = Populate();
        var r = Service(new QueryEmbedder()).Search(Req("database locked", scope: "all", project: "owner/repo"), default);
        Assert.Equal("all", r.Scope);
        Assert.Contains(seed.C3, Ids(r));
    }

    [Theory]
    [InlineData("sessions")]
    [InlineData("")]
    public void Scope_Unknown_Throws(string scope)
    {
        Populate();
        Assert.Throws<ArgumentException>(() => Service(new QueryEmbedder()).Search(Req("database locked", scope: scope), default));
    }

    // ---- defaults from the current session (spec 2026-11-24, section 4) ---------------------------------------------

    private static CurrentSessionInfo Marker(string? projectKey = "owner/repo", string @ref = "claude-code:sess-1") =>
        new("marker", @ref, projectKey);

    private static CurrentSessionInfo Guessed() => new("guessed", "claude-code:sess-1", "owner/repo");

    [Fact]
    public void Plan_NoScopeNothingGiven_Marker_DefaultsToCurrentProject_WithNote()
    {
        var seed = Populate();
        var r = Service(new QueryEmbedder()).Search(Req("database locked", current: Marker()), default);
        Assert.Equal("project", r.Scope);
        Assert.Equal("defaulted to the current project (owner/repo)", r.ScopeNote);
        Assert.Equal(new[] { seed.C1a, seed.C1b }.OrderBy(x => x), Ids(r).OrderBy(x => x));
    }

    [Fact]
    public void Plan_ScopeProjectNoProject_Marker_UsesCurrentProject()
    {
        var seed = Populate();
        var r = Service(new QueryEmbedder()).Search(Req("database locked", scope: "project", current: Marker()), default);
        Assert.Equal("project", r.Scope);
        Assert.Null(r.ScopeNote);
        Assert.Equal(new[] { seed.C1a, seed.C1b }.OrderBy(x => x), Ids(r).OrderBy(x => x));
    }

    [Theory]
    [InlineData("guessed")]
    [InlineData("none")]
    [InlineData(null)]
    public void Plan_ScopeProjectNoProject_NoMarker_AllWithNewNote(string? kind)
    {
        var seed = Populate();
        CurrentSessionInfo? current = kind is null ? null : new(kind, kind == "none" ? null : "claude-code:sess-1", kind == "none" ? null : "owner/repo");
        var r = Service(new QueryEmbedder()).Search(Req("database locked", scope: "project", current: current), default);
        Assert.Equal("all", r.Scope);
        Assert.Contains("the current project could not be determined", r.ScopeNote);
        Assert.DoesNotContain("not detected yet", r.ScopeNote);
        Assert.Contains(seed.C3, Ids(r));
        Assert.Contains(seed.C1a, Ids(r));
    }

    [Fact]
    public void Plan_ScopeSessionNoSession_Marker_UsesCurrentSession()
    {
        var seed = Populate();
        var r = Service(new QueryEmbedder()).Search(Req("database locked", scope: "session", current: Marker()), default);
        Assert.Equal("session", r.Scope);
        Assert.Null(r.ScopeNote);
        Assert.Equal(new[] { seed.C1a, seed.C1b }.OrderBy(x => x), Ids(r).OrderBy(x => x));
    }

    [Theory]
    [InlineData("guessed")]
    [InlineData("none")]
    [InlineData(null)]
    public void Plan_ScopeSessionNoSession_NoMarker_ErrorMentionsCurrentSessionUnknown(string? kind)
    {
        Populate();
        CurrentSessionInfo? current = kind is null ? null : new(kind, kind == "none" ? null : "claude-code:sess-1", kind == "none" ? null : "owner/repo");
        var ex = Assert.Throws<ArgumentException>(
            () => Service(new QueryEmbedder()).Search(Req("database locked", scope: "session", current: current), default));
        Assert.Contains("scope session needs a session argument (<host>:<host_session_id>)", ex.Message);
        Assert.Contains("the current session is not known here", ex.Message);
    }

    [Fact]
    public void Plan_ExplicitArgumentsAlwaysWin()
    {
        var seed = Populate();
        var svc = Service(new QueryEmbedder());

        var project = svc.Search(Req("database locked", project: "other/proj", current: Marker()), default);
        Assert.Equal("project", project.Scope);
        Assert.Null(project.ScopeNote);
        Assert.Equal(new[] { seed.C3, seed.C3Unembedded }.OrderBy(x => x), Ids(project).OrderBy(x => x));

        var session = svc.Search(Req("database locked", session: "pi:sess-3", current: Marker()), default);
        Assert.Equal("session", session.Scope);
        Assert.Equal(new[] { seed.C3, seed.C3Unembedded }.OrderBy(x => x), Ids(session).OrderBy(x => x));

        var namedWithScopeSession = svc.Search(Req("database locked", scope: "session", session: "pi:sess-3", current: Marker()), default);
        Assert.Equal(new[] { seed.C3, seed.C3Unembedded }.OrderBy(x => x), Ids(namedWithScopeSession).OrderBy(x => x));

        var all = svc.Search(Req("database locked", scope: "all", current: Marker()), default);
        Assert.Equal("all", all.Scope);
        Assert.Null(all.ScopeNote);
        Assert.Contains(seed.C3, Ids(all));
        Assert.Contains(seed.C1a, Ids(all));

        var projectArg = svc.Search(Req("database locked", scope: "project", project: "other/proj", current: Marker()), default);
        Assert.Equal(new[] { seed.C3, seed.C3Unembedded }.OrderBy(x => x), Ids(projectArg).OrderBy(x => x));
    }

    [Fact]
    public void Plan_Guessed_NeverChoosesAScope()
    {
        var seed = Populate();
        var svc = Service(new QueryEmbedder());

        var none = svc.Search(Req("database locked", current: Guessed()), default);
        Assert.Equal("all", none.Scope);
        Assert.Null(none.ScopeNote);
        Assert.Contains(seed.C3, Ids(none));

        Assert.Throws<ArgumentException>(() => svc.Search(Req("database locked", scope: "session", current: Guessed()), default));

        var project = svc.Search(Req("database locked", scope: "project", current: Guessed()), default);
        Assert.Equal("all", project.Scope);
        Assert.Contains("the current project could not be determined", project.ScopeNote);
    }

    [Fact]
    public void Plan_MarkerWithNoProjectKey_IsTodaysBehaviour()
    {
        var seed = Populate();
        var svc = Service(new QueryEmbedder());

        var none = svc.Search(Req("database locked", current: Marker(projectKey: null)), default);
        Assert.Equal("all", none.Scope);
        Assert.Null(none.ScopeNote);
        Assert.Contains(seed.C3, Ids(none));

        var project = svc.Search(Req("database locked", scope: "project", current: Marker(projectKey: null)), default);
        Assert.Equal("all", project.Scope);
        Assert.Contains("the current project could not be determined", project.ScopeNote);

        // The session itself is still known, so scope=session works without a project.
        var session = svc.Search(Req("database locked", scope: "session", current: Marker(projectKey: null)), default);
        Assert.Equal("session", session.Scope);
        Assert.Equal(new[] { seed.C1a, seed.C1b }.OrderBy(x => x), Ids(session).OrderBy(x => x));
    }

    [Theory]
    [InlineData("nocolon", null, false)]             // a reference that does not split is no session
    [InlineData("nocolon", "   ", false)]
    [InlineData("claude-code:sess-1", "   ", true)]   // a whitespace-only key is no project, like null
    [InlineData("claude-code:sess-1", "", true)]
    public void Plan_MarkerWithUnusableRefOrKey_FallsBackToTodaysBehaviour(string @ref, string? projectKey, bool sessionKnown)
    {
        var seed = Populate();
        var svc = Service(new QueryEmbedder());
        var current = Marker(projectKey: projectKey, @ref: @ref);

        var none = svc.Search(Req("database locked", current: current), default);
        Assert.Equal("all", none.Scope);
        Assert.Null(none.ScopeNote);
        Assert.Contains(seed.C3, Ids(none));
        Assert.Contains(seed.C1a, Ids(none));

        var project = svc.Search(Req("database locked", scope: "project", current: current), default);
        Assert.Equal("all", project.Scope);
        Assert.Contains("the current project could not be determined", project.ScopeNote);
        Assert.Contains(seed.C3, Ids(project));

        if (sessionKnown)
        {
            var session = svc.Search(Req("database locked", scope: "session", current: current), default);
            Assert.Equal("session", session.Scope);
            Assert.Equal(new[] { seed.C1a, seed.C1b }.OrderBy(x => x), Ids(session).OrderBy(x => x));
        }
        else
        {
            var ex = Assert.Throws<ArgumentException>(
                () => svc.Search(Req("database locked", scope: "session", current: current), default));
            Assert.Contains("the current session is not known here", ex.Message);
        }
    }

    [Fact]
    public void Response_CarriesCurrentSessionFields()
    {
        Populate();
        var svc = Service(new QueryEmbedder());

        var marker = svc.Search(Req("database locked", current: Marker(), syncNote: "the newest text of the current session may be missing: the catch-up ran out of time"), default);
        Assert.Equal("marker", marker.CurrentSession);
        Assert.Equal("claude-code:sess-1", marker.CurrentSessionRef);
        Assert.Equal("the newest text of the current session may be missing: the catch-up ran out of time", marker.SyncNote);

        var guessed = svc.Search(Req("database locked", current: Guessed()), default);
        Assert.Equal("guessed", guessed.CurrentSession);
        Assert.Equal("claude-code:sess-1", guessed.CurrentSessionRef);
        Assert.Null(guessed.SyncNote);

        var none = svc.Search(Req("database locked", current: new CurrentSessionInfo("none", null, null)), default);
        Assert.Equal("none", none.CurrentSession);
        Assert.Null(none.CurrentSessionRef);

        var absent = svc.Search(Req("database locked"), default);
        Assert.Equal("none", absent.CurrentSession);
        Assert.Null(absent.CurrentSessionRef);
        Assert.Null(absent.SyncNote);
    }

    [Fact]
    public void Search_AbsentArchive_WithMarker_AnswersCurrentSessionMarkerWithTheRef()
    {
        var missing = Path.Combine(Path.GetTempPath(), "tr-absent-" + Guid.NewGuid().ToString("N"), "archive.db");
        var r = Service(new QueryEmbedder(), location: new TranscriptLocation(missing, IsMainDatabase: false))
            .Search(Req("database locked", current: Marker(), syncNote: "a note"), default);
        Assert.Null(r.Error);
        Assert.Equal(TranscriptSearchService.NothingArchivedNote, r.Note);
        Assert.Equal("marker", r.CurrentSession);
        Assert.Equal("claude-code:sess-1", r.CurrentSessionRef);
        Assert.Equal("a note", r.SyncNote);
        Assert.Equal("project", r.Scope);
        Assert.Equal("defaulted to the current project (owner/repo)", r.ScopeNote);
    }

    [Fact]
    public void Search_EmptyArchive_And_FailurePath_CarryTheCurrentSessionFields()
    {
        // Empty archive (no chunk).
        var empty = Service(new QueryEmbedder()).Search(Req("database locked", current: Marker(), syncNote: "n"), default);
        Assert.Equal(TranscriptSearchService.NothingArchivedNote, empty.Note);
        Assert.Equal("marker", empty.CurrentSession);
        Assert.Equal("claude-code:sess-1", empty.CurrentSessionRef);
        Assert.Equal("n", empty.SyncNote);

        // Failure: no context in this storage mode.
        var noContext = new TranscriptSearchService(() => null)
            .Search(Req("database locked", current: Marker(), syncNote: "n"), default);
        Assert.NotNull(noContext.Error);
        Assert.Equal("marker", noContext.CurrentSession);
        Assert.Equal("claude-code:sess-1", noContext.CurrentSessionRef);
        Assert.Equal("n", noContext.SyncNote);

        // Failure: the factory throws.
        var throws = new TranscriptSearchService(() => throw new InvalidOperationException("bad"))
            .Search(Req("database locked", current: Guessed()), default);
        Assert.NotNull(throws.Error);
        Assert.Equal("guessed", throws.CurrentSession);
        Assert.Equal("claude-code:sess-1", throws.CurrentSessionRef);

        // Failure: the archive is refused.
        var refused = Service(new QueryEmbedder(), location: new TranscriptLocation(_db.Path, IsMainDatabase: true))
            .Search(Req("database locked", current: Marker()), default);
        Assert.NotNull(refused.Error);
        Assert.Equal("marker", refused.CurrentSession);
        Assert.Equal("claude-code:sess-1", refused.CurrentSessionRef);

        // Unknown session answer.
        Populate();
        var unknown = Service(new QueryEmbedder()).Search(Req("database locked", session: "pi:nope", current: Marker()), default);
        Assert.Equal("no archived session pi:nope", unknown.Note);
        Assert.Equal("marker", unknown.CurrentSession);
        Assert.Equal("claude-code:sess-1", unknown.CurrentSessionRef);
    }

    [Fact]
    public void ValidateScope_AcceptsNullAndTheThree_RefusesOthers()
    {
        foreach (var ok in new string?[] { null, "session", "project", "all" })
            TranscriptSearchService.ValidateScope(ok);
        foreach (var bad in new[] { "sessions", "", "ALL" })
        {
            var ex = Assert.Throws<ArgumentException>(() => TranscriptSearchService.ValidateScope(bad));
            Assert.Equal($"scope must be session, project or all; got '{bad}'", ex.Message);
        }
    }

    [Theory]
    [InlineData("no-colon")]
    [InlineData(":id")]
    [InlineData("host:")]
    public void Session_Malformed_Throws(string session)
    {
        Populate();
        Assert.Throws<ArgumentException>(() => Service(new QueryEmbedder()).Search(Req("database locked", session: session), default));
    }

    [Fact]
    public void Session_SplitsAtTheFirstColon()
    {
        Assert.Equal(("pi", "a:b:c"), TranscriptSearchService.SplitSession("pi:a:b:c"));
    }

    // ---- filters on both candidate lists ----------------------------------------------------------------------------

    [Fact]
    public void Filters_ApplyToTheMeaningList()
    {
        var seed = Populate();
        // "zzqv" matches no word, so every hit comes from the vectors; c2 and c3 are the nearest overall.
        var r = Service(new QueryEmbedder()).Search(Req("zzqv", project: "owner/repo"), default);
        Assert.True(r.MeaningOn);
        Assert.Equal(new[] { seed.C1a, seed.C1b }, Ids(r)); // c2 is automated, c3 another project
        Assert.All(r.Results, h => Assert.Equal(new[] { "meaning" }, h.Via));
    }

    [Fact]
    public void Filters_ApplyToTheWordLists()
    {
        var seed = Populate();
        var r = Service(new QueryEmbedder()).Search(Req("unembedded note", project: "owner/repo"), default);
        Assert.DoesNotContain(seed.C3Unembedded, Ids(r));
        var all = Service(new QueryEmbedder()).Search(Req("unembedded note"), default);
        Assert.Contains(seed.C3Unembedded, Ids(all));
    }

    [Fact]
    public void KindFilter_OffWhenScopeIsSession()
    {
        var seed = Populate();
        var r = Service(new QueryEmbedder()).Search(Req("database locked", session: "claude-code:sess-2"), default);
        Assert.Equal(new[] { seed.C2 }, Ids(r));
        Assert.Equal("automated", r.Results[0].Kind);
    }

    [Fact]
    public void Search_AutomatedOnlyWithFlag()
    {
        var seed = Populate();
        var svc = Service(new QueryEmbedder());
        Assert.DoesNotContain(seed.C2, Ids(svc.Search(Req("database locked"), default)));
        Assert.DoesNotContain(seed.C2, Ids(svc.Search(Req("database locked", project: "owner/repo"), default)));
        Assert.Contains(seed.C2, Ids(svc.Search(Req("database locked", includeAutomated: true), default)));
        Assert.Contains(seed.C2, Ids(svc.Search(Req("database locked", project: "owner/repo", includeAutomated: true), default)));
    }

    [Fact]
    public void Since_DateOnly_IsUtcMidnight()
    {
        Assert.Equal(new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero), TranscriptSearchService.ParseSince("2026-03-02"));
        Assert.Equal(new DateTimeOffset(2026, 3, 2, 10, 30, 0, TimeSpan.Zero), TranscriptSearchService.ParseSince("2026-03-02T10:30:00Z"));
        Assert.Equal(new DateTimeOffset(2026, 3, 2, 9, 30, 0, TimeSpan.Zero), TranscriptSearchService.ParseSince("2026-03-02T10:30:00+01:00"));
        Assert.Equal(new DateTimeOffset(2026, 3, 2, 10, 30, 0, 500, TimeSpan.Zero), TranscriptSearchService.ParseSince("2026-03-02T10:30:00.5Z"));
        Assert.Equal(TimeSpan.Zero, TranscriptSearchService.ParseSince("2026-03-02").Offset);
    }

    [Fact]
    public void Since_FiltersBothLists()
    {
        var seed = Populate();
        var since = TranscriptSearchService.ParseSince("2026-03-02");
        var words = Service(new QueryEmbedder()).Search(Req("database locked", since: since), default);
        Assert.DoesNotContain(seed.C1a, Ids(words));
        Assert.Contains(seed.C1b, Ids(words));
        var meaning = Service(new QueryEmbedder()).Search(Req("zzqv", since: since), default);
        Assert.DoesNotContain(seed.C1a, Ids(meaning));
        Assert.Contains(seed.C1b, Ids(meaning));
    }

    [Theory]
    [InlineData("yesterday")]
    [InlineData("2026-13-01")]
    [InlineData("")]
    [InlineData("03/02/2026")]
    public void Since_Unparsable_Throws(string text)
    {
        Assert.Throws<ArgumentException>(() => TranscriptSearchService.ParseSince(text));
    }

    [Fact]
    public void ParseSince_BeforeName_InError()
    {
        var ex = Assert.Throws<ArgumentException>(() => TranscriptSearchService.ParseSince("yesterday", "before"));
        Assert.StartsWith("before must be an ISO-8601 date", ex.Message);
    }

    [Fact]
    public void ParseSince_DefaultName_IsSince()
    {
        var ex = Assert.Throws<ArgumentException>(() => TranscriptSearchService.ParseSince("yesterday"));
        Assert.Equal(
            "since must be an ISO-8601 date (2026-11-18) or date-time (2026-11-18T10:00:00Z); got 'yesterday'",
            ex.Message);
    }

    // ---- the meaning decision ---------------------------------------------------------------------------------------

    [Fact]
    public void Search_MeaningOnAndFingerprintMatch_UsesVectors()
    {
        var seed = Populate();
        var embedder = new QueryEmbedder();
        var r = Service(embedder).Search(Req("zzqv", includeAutomated: true), default);
        Assert.True(r.MeaningOn);
        Assert.Null(r.MeaningReason);
        Assert.Equal(new[] { "query:zzqv" }, embedder.Calls);
        Assert.Equal(seed.C2, r.Results[0].Id); // the nearest vector
        Assert.Equal(new[] { "meaning" }, r.Results[0].Via);
    }

    [Fact]
    public void Search_Gate_Unchanged_AfterTheHelperIsFactored()
    {
        // The search keeps serverPass false and reindex false: a running memory re-embed does not touch it.
        Populate();
        var embedder = new QueryEmbedder();
        var settings = Settings();
        var config = Embedding();
        var viaHelper = TranscriptSearchService.BuildEmbedGate(
            embedder, settings, "sqlite", config, serverPass: false, memoryReindexRunning: true, _db.Store.CheckFingerprint, _db.Store.ReadEmbedFingerprint);
        Assert.Equal(EmbedState.Ready, viaHelper.Gate.State);
        Assert.Equal(new ArchiveFingerprint("local", "bge-small-en-v1.5", "rev1", 384), viaHelper.Current);

        // The pass's gate (the sync's) does wait for the re-embed.
        var pass = TranscriptSearchService.BuildEmbedGate(
            embedder, settings, "sqlite", config, serverPass: true, memoryReindexRunning: true, _db.Store.CheckFingerprint, _db.Store.ReadEmbedFingerprint);
        Assert.Equal(EmbedState.WaitingForReindex, pass.Gate.State);

        // A remote provider without the opt-in ends before any store read; a descriptor that throws gives no fingerprint.
        var remote = TranscriptSearchService.BuildEmbedGate(
            new QueryEmbedder(new EmbedderDescriptor("openai", "m", "", 384)), settings, "sqlite", Embedding("openai"),
            serverPass: true, memoryReindexRunning: false, _ => throw new InvalidOperationException("no store read"), null);
        Assert.Equal(EmbedState.RemoteNotAllowed, remote.Gate.State);
        var broken = TranscriptSearchService.BuildEmbedGate(
            new ThrowingDescriptorEmbedder(), settings, "sqlite", config, serverPass: true, memoryReindexRunning: false, null, null);
        Assert.Null(broken.Current);
        Assert.False(broken.Gate.CanEmbed);

        var r = Service(embedder).Search(Req("database locked"), default);
        Assert.True(r.MeaningOn);
        Assert.Null(r.MeaningReason);
    }

    [Fact]
    public void Search_Unstamped_UsesVectors()
    {
        var s = _db.AddSession("pi", "u1");
        var c = AddChunk(s, "some text", 0, angle: 0.1);
        var r = Service(new QueryEmbedder()).Search(Req("zzqv"), default);
        Assert.True(r.MeaningOn);
        Assert.Equal(new[] { c }, Ids(r));
    }

    [Fact]
    public void Search_Mismatch_WordsOnly_ReasonText()
    {
        Populate();
        var other = new EmbedderDescriptor("local", "other-model", "r9", 384);
        var embedder = new QueryEmbedder(other);
        var r = Service(embedder).Search(Req("database locked"), default);
        Assert.False(r.MeaningOn);
        Assert.Equal(
            EmbedGate.Mismatch(new ArchiveFingerprint("local", "bge-small-en-v1.5", "rev1", 384), new ArchiveFingerprint("local", "other-model", "r9", 384)).Text,
            r.MeaningReason);
        Assert.Empty(embedder.Calls);
        Assert.NotEmpty(r.Results);
        Assert.All(r.Results, h => Assert.DoesNotContain("meaning", h.Via));
    }

    [Fact]
    public void Search_RemoteWithoutOptIn_NeverCallsEmbedder()
    {
        var seed = Populate();
        var embedder = new QueryEmbedder(new EmbedderDescriptor("openai", "text-embedding-3-small", "", 384));
        var r = Service(embedder, provider: "openai", allowRemote: false).Search(Req("unembedded note"), default);
        Assert.Empty(embedder.Calls);
        Assert.False(r.MeaningOn);
        Assert.Equal("words only: the embedding provider is remote and allow_remote_embedding is off", r.MeaningReason);
        Assert.Contains(seed.C3Unembedded, Ids(r));
    }

    [Fact]
    public void Search_RemoteWithOptIn_EmbedsTheQuery()
    {
        var s = _db.AddSession("pi", "r1");
        AddChunk(s, "text", 0, angle: 0.1);
        var embedder = new QueryEmbedder(new EmbedderDescriptor("openai", "text-embedding-3-small", "", 384));
        var r = Service(embedder, provider: "openai", allowRemote: true).Search(Req("zzqv"), default);
        Assert.True(r.MeaningOn);
        Assert.Equal(new[] { "query:zzqv" }, embedder.Calls);
    }

    [Fact]
    public void Search_ModelNotReady_WordsOnly_NoCall()
    {
        Populate();
        var embedder = new QueryEmbedder { Readiness = new EmbedderReadiness(false, "model.onnx missing") };
        var r = Service(embedder).Search(Req("database locked"), default);
        Assert.False(r.MeaningOn);
        Assert.Equal("words only: embedding model not ready (model.onnx missing)", r.MeaningReason);
        Assert.Empty(embedder.Calls);
        Assert.NotEmpty(r.Results);
    }

    public static IEnumerable<object[]> Failures() => new[]
    {
        new object[] { "invalid-operation" },
        new object[] { "onnx" },
        new object[] { "http" },
        new object[] { "timeout" },
        new object[] { "wrong-size" },
    };

    [Theory, MemberData(nameof(Failures))]
    public void Search_EmbedderThrows_FallsBackToWords_NotAnError(string kind)
    {
        var seed = Populate();
        var embedder = new QueryEmbedder
        {
            Answer = kind switch
            {
                "invalid-operation" => _ => throw new InvalidOperationException("model file not found"),
                "onnx" => _ => throw (Exception)RuntimeHelpers.GetUninitializedObject(typeof(OnnxRuntimeException)),
                "http" => _ => throw new HttpRequestException("connection refused"),
                "timeout" => _ => throw new TaskCanceledExceptionStub(),
                "wrong-size" => _ => new float[768],
                _ => throw new ArgumentException(kind),
            },
        };
        var r = Service(embedder).Search(Req("unembedded note"), default);
        Assert.Null(r.Error);
        Assert.False(r.MeaningOn);
        Assert.False(string.IsNullOrWhiteSpace(r.MeaningReason));
        Assert.Contains(seed.C3Unembedded, Ids(r));
        Assert.All(r.Results, h => Assert.DoesNotContain("meaning", h.Via));
        if (kind == "invalid-operation")
            Assert.Equal("words only: embedding model not ready (model file not found)", r.MeaningReason);
        if (kind == "wrong-size")
            Assert.Equal("words only: the provider's vectors have 768 dimensions; the archive holds 384", r.MeaningReason);
        // meaning off: every chunk in scope is counted (c1a, c1b, c3, c3u)
        Assert.Equal(4, r.PendingEmbedding);
    }

    private sealed class TaskCanceledExceptionStub : System.Threading.Tasks.TaskCanceledException
    {
        public TaskCanceledExceptionStub() : base("the request timed out") { }
    }

    [Fact]
    public void Search_Cancelled_Throws()
    {
        Populate();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Service(new QueryEmbedder()).Search(Req("database locked"), cts.Token));
    }

    // ---- pendingEmbedding -------------------------------------------------------------------------------------------

    [Fact]
    public void Pending_CountsUnembeddedInScope_AllWhenMeaningOff()
    {
        var seed = Populate();
        var on = Service(new QueryEmbedder()).Search(Req("database locked"), default);
        Assert.True(on.MeaningOn);
        Assert.Equal(1, on.PendingEmbedding); // c3u (interactive scope: c1a, c1b, c3, c3u)

        var onProject = Service(new QueryEmbedder()).Search(Req("database locked", project: "owner/repo"), default);
        Assert.Equal(0, onProject.PendingEmbedding);

        var onSession = Service(new QueryEmbedder()).Search(Req("database locked", session: "pi:sess-3"), default);
        Assert.Equal(1, onSession.PendingEmbedding);

        var off = Service(new QueryEmbedder { Readiness = new EmbedderReadiness(false, "x") }).Search(Req("database locked"), default);
        Assert.False(off.MeaningOn);
        Assert.Equal(4, off.PendingEmbedding);

        var offAutomated = Service(new QueryEmbedder { Readiness = new EmbedderReadiness(false, "x") })
            .Search(Req("database locked", includeAutomated: true), default);
        Assert.Equal(5, offAutomated.PendingEmbedding);
        _ = seed;
    }

    // ---- notes, empty and error answers -----------------------------------------------------------------------------

    [Fact]
    public void Search_UnknownSession_EmptyWithNote()
    {
        Populate();
        var r = Service(new QueryEmbedder()).Search(Req("database locked", session: "pi:nope"), default);
        Assert.Null(r.Error);
        Assert.Empty(r.Results);
        Assert.Equal("session", r.Scope);
        Assert.Contains("pi:nope", r.Note);
        Assert.Equal(0, r.PendingEmbedding);
    }

    [Fact]
    public void Search_ProjectMatchingNothing_Empty()
    {
        Populate();
        var r = Service(new QueryEmbedder()).Search(Req("database locked", project: "nobody/nothing"), default);
        Assert.Null(r.Error);
        Assert.Equal("project", r.Scope);
        Assert.Empty(r.Results);
        Assert.Equal(0, r.PendingEmbedding);
    }

    [Fact]
    public void Search_NoArchiveFile_NoteNothingArchivedYet()
    {
        var missing = new TranscriptLocation(Path.Combine(_db.Dir, "absent", "none.db"), IsMainDatabase: false);
        var embedder = new QueryEmbedder();
        var r = Service(embedder, location: missing).Search(Req("anything"), default);
        Assert.Null(r.Error);
        Assert.Empty(r.Results);
        Assert.Equal(TranscriptSearchService.NothingArchivedNote, r.Note);
        Assert.Equal("nothing is archived yet; run total-recall transcripts backfill", r.Note);
        Assert.Empty(embedder.Calls);
        Assert.False(File.Exists(missing.Path));
    }

    [Fact]
    public void Search_EmptyArchive_EmptyResultsNote()
    {
        var embedder = new QueryEmbedder();
        var r = Service(embedder).Search(Req("anything"), default);
        Assert.Null(r.Error);
        Assert.Empty(r.Results);
        Assert.Equal(TranscriptSearchService.NothingArchivedNote, r.Note);
        Assert.Empty(embedder.Calls); // no model is loaded for an empty archive
    }

    /// <summary>Forwards to the real reads and records the calls of interest.</summary>
    private sealed class RecordingReads : ITranscriptSearchReads
    {
        private readonly ITranscriptSearchReads _inner;
        private readonly List<string> _log;

        public RecordingReads(ITranscriptSearchReads inner, List<string> log)
        {
            _inner = inner;
            _log = log;
        }

        public long? ResolveSession(string host, string hostSessionId) => _inner.ResolveSession(host, hostSessionId);
        public IReadOnlyList<VectorHit> Vector(float[] query, ChunkFilter filter, int k) => _inner.Vector(query, filter, k);
        public IReadOnlyList<TextHit> Text(string match, ChunkFilter filter, int limit) => _inner.Text(match, filter, limit);

        public bool AnyChunk()
        {
            _log.Add("any");
            return _inner.AnyChunk();
        }

        public long CountInScope(ChunkFilter filter, bool onlyUnembedded)
        {
            _log.Add($"count:{filter.Project ?? "*"}:{onlyUnembedded}");
            return _inner.CountInScope(filter, onlyUnembedded);
        }

        public IReadOnlyDictionary<long, ChunkRow> Chunks(IReadOnlyCollection<long> ids) => _inner.Chunks(ids);
        public void Dispose() => _inner.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Search_DetectsAnEmptyArchiveWithAnyChunk_CountsOnlyThePendingScope(bool populated)
    {
        if (populated) Populate();
        var log = new List<string>();
        var store = new DecoratingTranscriptStore(_db.Store)
        {
            OnBeginSearchReads = () => new RecordingReads(_db.Store.BeginSearchReads(), log),
        };
        var svc = new TranscriptSearchService(
            () => new TranscriptSearchContext(
                new TranscriptLocation(_db.Path, IsMainDatabase: false), Settings(), "sqlite", Embedding(), new QueryEmbedder()),
            new ManualTimeProvider(Now),
            _ => new TranscriptOpenResult(TranscriptOpenStatus.Opened, store, null));

        var r = svc.Search(Req("database locked", scope: "project", project: "owner/repo"), default);

        Assert.Null(r.Error);
        // The emptiness check is an EXISTS, not a count of the whole archive; the only count is pendingEmbedding's.
        Assert.Equal(populated ? new[] { "any", "count:owner/repo:True" } : new[] { "any" }, log);
        Assert.Equal(populated ? null : TranscriptSearchService.NothingArchivedNote, populated ? null : r.Note);
    }

    [Fact]
    public void Search_OpenRefused_ReturnsError()
    {
        // A separate archive file named as the main database: Open refuses it, even without create.
        var r = Service(new QueryEmbedder(), location: new TranscriptLocation(_db.Path, IsMainDatabase: true)).Search(Req("anything"), default);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
        Assert.Contains("holds only a transcript archive", r.Error);
        Assert.Empty(r.Results);
    }

    [Fact]
    public void Search_StoreThrowsArgumentException_IsAReadError_NotAnArgumentError()
    {
        Populate();
        var store = new DecoratingTranscriptStore(_db.Store)
        {
            OnBeginSearchReads = () => throw new ArgumentOutOfRangeException("ordinal", "a fault inside the store"),
        };
        var svc = new TranscriptSearchService(
            () => new TranscriptSearchContext(
                new TranscriptLocation(_db.Path, IsMainDatabase: false), Settings(), "sqlite", Embedding(), new QueryEmbedder()),
            new ManualTimeProvider(Now),
            _ => new TranscriptOpenResult(TranscriptOpenStatus.Opened, store, null));

        var r = svc.Search(Req("database locked"), default);

        Assert.NotNull(r.Error);
        Assert.StartsWith("the transcript archive could not be read: ", r.Error);
        Assert.Contains("a fault inside the store", r.Error);
        Assert.Empty(r.Results);
    }

    [Fact]
    public void Search_ContextFactoryThrows_ReturnsError()
    {
        var svc = new TranscriptSearchService(() => throw new InvalidOperationException("transcripts.db_path is invalid"));
        var r = svc.Search(Req("anything"), default);
        Assert.Contains("transcripts.db_path is invalid", r.Error);
    }

    [Fact]
    public void Search_ContextNull_ReturnsError()
    {
        var r = new TranscriptSearchService(() => null).Search(Req("anything"), default);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Search_EmptyQuery_Throws(string query)
    {
        Assert.Throws<ArgumentException>(() => Service(new QueryEmbedder()).Search(Req(query), default));
    }

    // ---- the hit ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Text_StripsHeaderLine()
    {
        Assert.Equal("body\n\nmore", TranscriptSearchService.ChunkText("[owner/repo 2026-11-01 interactive]\n\nbody\n\nmore"));
        Assert.Equal("single block of text", TranscriptSearchService.ChunkText("single block of text"));
        Assert.Equal(string.Empty, TranscriptSearchService.ChunkText("header\n\n"));

        var s = _db.AddSession("claude-code", "h1", "owner/repo", title: "The title");
        var c = AddChunk(s, "the body about zebras", 3, "2026-11-18T10:02:11.0000000Z", header: "[owner/repo 2026-11-18 interactive]");
        var whole = AddChunk(s, "no header zebras here", 5, header: null);
        var r = Service(new QueryEmbedder()).Search(Req("zebras"), default);
        var hit = r.Results.Single(h => h.Id == c);
        Assert.Equal("the body about zebras", hit.Text);
        Assert.Equal("claude-code:h1", hit.Session);
        Assert.Equal("owner/repo", hit.Project);
        Assert.Equal("interactive", hit.Kind);
        Assert.Equal("The title", hit.Title);
        Assert.Equal("2026-11-18T10:02:11.0000000Z", hit.Ts);
        Assert.Equal("dialogue", hit.ChunkType);
        Assert.False(hit.HasError);
        Assert.Equal("3-4", hit.Exchanges);
        Assert.Equal("no header zebras here", r.Results.Single(h => h.Id == whole).Text);
    }

    [Fact]
    public void Response_TextCap40000_TruncatedHitsCounted()
    {
        var s = _db.AddSession("pi", "big");
        var body = "zebra " + new string('x', 1794); // 1,800 characters
        for (var i = 0; i < 30; i++)
            AddChunk(s, body, i * 2);

        var r = Service(new QueryEmbedder()).Search(Req("zebra", session: "pi:big", topK: 30), default);
        Assert.Equal(22, r.Results.Count); // 22 x 1,800 = 39,600; the 23rd would pass 40,000
        Assert.Equal(8, r.TruncatedHits);
        Assert.True(r.Results.Sum(h => h.Text.Length) <= TranscriptSearchService.TextCap);
        Assert.Equal(40_000, TranscriptSearchService.TextCap);

        var small = Service(new QueryEmbedder()).Search(Req("zebra", session: "pi:big", topK: 10), default);
        Assert.Equal(10, small.Results.Count);
        Assert.Equal(0, small.TruncatedHits);
    }

    [Fact]
    public void TopK_1_And_50()
    {
        var s = _db.AddSession("pi", "many");
        for (var i = 0; i < 60; i++)
            AddChunk(s, "zebra number " + i, i * 2);

        var svc = Service(new QueryEmbedder());
        Assert.Single(svc.Search(Req("zebra", session: "pi:many", topK: 1), default).Results);
        Assert.Equal(50, svc.Search(Req("zebra", session: "pi:many", topK: 50), default).Results.Count);
        Assert.Throws<ArgumentException>(() => svc.Search(Req("zebra", topK: 0), default));
        Assert.Throws<ArgumentException>(() => svc.Search(Req("zebra", topK: 51), default));
    }

    [Fact]
    public void SessionCap_TwoPerSession_ExceptInSessionScope()
    {
        var s = _db.AddSession("pi", "capped");
        for (var i = 0; i < 5; i++)
            AddChunk(s, "zebra " + i, i * 2);
        var svc = Service(new QueryEmbedder());
        Assert.Equal(2, svc.Search(Req("zebra"), default).Results.Count);
        Assert.Equal(5, svc.Search(Req("zebra", session: "pi:capped"), default).Results.Count);
    }

    [Theory]
    [InlineData("-foo")]
    [InlineData("foo*")]
    [InlineData("a:b")]
    [InlineData("NEAR(a b)")]
    [InlineData("a OR b")]
    [InlineData("\"")]
    [InlineData("\"\"")]
    [InlineData("\"unbalanced")]
    [InlineData("... --- !!!")]
    [InlineData("über Straße")]
    [InlineData("日本語のテキスト 検索")]
    [InlineData("foo AND NOT bar ^baz {x y} (z)")]
    [InlineData("col:value title: \"x y\" + - * ~")]
    public void Search_OddQueries_NeverThrow(string query)
    {
        Populate();
        var r = Service(new QueryEmbedder()).Search(Req(query), default);
        Assert.Null(r.Error);
    }

    [Fact]
    public void Search_TenThousandCharacterQuery_Works_AndSkipsThePhrase()
    {
        var seed = Populate();
        var query = "database locked " + string.Join(' ', Enumerable.Range(0, 1700).Select(i => "tok" + i));
        Assert.True(query.Length > 10_000);
        var r = Service(new QueryEmbedder()).Search(Req(query), default);
        Assert.Null(r.Error);
        Assert.All(r.Results, h => Assert.DoesNotContain("phrase", h.Via));
        _ = seed;
    }

    [Fact]
    public void ExactPhrase_IsMarkedPhrase()
    {
        var seed = Populate();
        var r = Service(new QueryEmbedder()).Search(Req("database locked during ingest"), default);
        var hit = r.Results.Single(h => h.Id == seed.C1a);
        Assert.Contains("phrase", hit.Via);
        Assert.Contains("words", hit.Via);
        Assert.Contains("meaning", hit.Via);
    }

    [Fact]
    public void Scores_AreRelative_TopIsOne()
    {
        Populate();
        var r = Service(new QueryEmbedder()).Search(Req("database locked", includeAutomated: true), default);
        Assert.True(r.Results.Count >= 3);
        Assert.Equal(1.0, r.Results[0].Score, 6);
        Assert.True(r.Results.Zip(r.Results.Skip(1), (a, b) => a.Score >= b.Score).All(x => x));
        Assert.All(r.Results, h => Assert.InRange(h.Score, 0.0, 1.0));
    }

    [Fact]
    public void Response_EchoesTheQuery()
    {
        Populate();
        var r = Service(new QueryEmbedder()).Search(Req("database locked"), default);
        Assert.Equal("database locked", r.Query);
        Assert.Null(r.Note);
        Assert.Equal(0, r.TruncatedHits);
    }

    [Fact]
    public void Search_DoesNotHoldTheArchiveOpen()
    {
        Populate();
        Service(new QueryEmbedder()).Search(Req("database locked"), default);
        // a forget-style truncating checkpoint on another connection succeeds: no reader is left
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal(0L, r.GetInt64(0)); // not busy
    }
}
