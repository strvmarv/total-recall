// The three archive tools in their off and not-set-up states (spec 2026-11-18, section 9; read spec 2026-11-23, section
// 4): each answers from a per-call gate, the input schemas are fixed, and the tools are registered only for the sqlite
// and cortex compositions.

namespace TotalRecall.Server.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Core;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Transcripts;
using TotalRecall.Server.Handlers;
using TotalRecall.TestSupport.Transcripts;
using Xunit;
using SqliteOpener = TotalRecall.Infrastructure.Storage.SqliteConnection;

[Collection("process-environment-console")]
public sealed class TranscriptToolHandlerTests
{
    private const string OffText =
        "Run `total-recall transcripts enable` in a terminal to turn the archive on.";
    private const string NotSetUpText =
        "The transcript archive is not set up in this server.";

    private static readonly string[] ToolNames = { "transcript_search", "transcript_get", "transcript_sessions" };

    private static IToolHandler Make(string name, Func<bool> isActive) => name switch
    {
        "transcript_search" => new TranscriptSearchHandler(isActive),
        "transcript_get" => new TranscriptGetHandler(isActive),
        "transcript_sessions" => new TranscriptSessionsHandler(isActive),
        _ => throw new ArgumentException(name),
    };

    public static IEnumerable<object[]> Names() => ToolNames.Select(n => new object[] { n });

    private static async Task<JsonElement> Call(IToolHandler h)
    {
        var r = await h.ExecuteAsync(null, CancellationToken.None);
        Assert.NotEqual(true, r.IsError);
        var content = Assert.Single(r.Content);
        Assert.Equal("text", content.Type);
        return JsonDocument.Parse(content.Text).RootElement.Clone();
    }

    [Theory, MemberData(nameof(Names))]
    public async Task Inactive_AnswersEnabledFalseWithTheHint(string name)
    {
        var json = await Call(Make(name, () => false));
        Assert.Equal(2, json.EnumerateObject().Count());
        Assert.False(json.GetProperty("enabled").GetBoolean());
        Assert.Equal(OffText, json.GetProperty("hint").GetString());
    }

    [Theory, MemberData(nameof(Names))]
    public async Task Active_WithoutAService_AnswersNotSetUp(string name)
    {
        var json = await Call(Make(name, () => true));
        Assert.Equal(3, json.EnumerateObject().Count());
        Assert.True(json.GetProperty("enabled").GetBoolean());
        Assert.False(json.GetProperty("available").GetBoolean());
        var hint = json.GetProperty("hint").GetString();
        Assert.Equal(NotSetUpText, hint);
        Assert.DoesNotContain("not available in this version", hint);
    }

    [Theory, MemberData(nameof(Names))]
    public async Task TheGate_IsReadOnEveryCall(string name)
    {
        var on = false;
        var h = Make(name, () => on);
        Assert.False((await Call(h)).GetProperty("enabled").GetBoolean());
        on = true;
        Assert.True((await Call(h)).GetProperty("enabled").GetBoolean());
        on = false;
        Assert.False((await Call(h)).GetProperty("enabled").GetBoolean());
    }

    [Theory]
    [InlineData("transcript_get")]
    [InlineData("transcript_sessions")]
    public void NameAndDescription_StateOffByDefaultAndNeverNotAvailable(string name)
    {
        var h = Make(name, () => false);
        Assert.Equal(name, h.Name);
        Assert.Contains("off by default", h.Description);
        Assert.DoesNotContain("not available", h.Description);
    }

    [Fact]
    public void SearchDescription_MeaningWordsAndTheScopeRule()
    {
        var h = new TranscriptSearchHandler(() => false);
        Assert.Equal("transcript_search", h.Name);
        var d = h.Description;
        Assert.Contains("off by default", d);
        Assert.Contains("by meaning and by words", d);
        Assert.Contains("no `scope`", d);
        Assert.Contains("`session`", d);
        Assert.Contains("`project`", d);
        Assert.Contains("everything", d);
        Assert.Contains("the current project", d);
        Assert.Contains("`currentSession`", d);
        Assert.Contains("`syncNote`", d);
        Assert.DoesNotContain("not available", d);
    }

    private static JsonElement Prop(IToolHandler h, string name) => h.InputSchema.GetProperty("properties").GetProperty(name);

    private static void AssertType(IToolHandler h, string name, string type) =>
        Assert.Equal(type, Prop(h, name).GetProperty("type").GetString());

    private static string[] Required(IToolHandler h) =>
        h.InputSchema.TryGetProperty("required", out var r) ? r.EnumerateArray().Select(e => e.GetString()!).ToArray() : Array.Empty<string>();

    private static string[] PropNames(IToolHandler h) =>
        h.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();

    [Fact]
    public void SearchSchema_MatchesTheSpec()
    {
        var h = new TranscriptSearchHandler(() => false);
        Assert.Equal("object", h.InputSchema.GetProperty("type").GetString());
        Assert.Equal(
            new[] { "query", "scope", "project", "session", "since", "include_automated", "top_k" }.OrderBy(x => x),
            PropNames(h).OrderBy(x => x));
        Assert.Equal(new[] { "query" }, Required(h));
        AssertType(h, "query", "string");
        AssertType(h, "scope", "string");
        Assert.Equal(new[] { "session", "project", "all" }.OrderBy(x => x),
            Prop(h, "scope").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).OrderBy(x => x));
        Assert.False(Prop(h, "scope").TryGetProperty("default", out _));
        // The defaults of spec 2026-11-24 section 4: the current project (from the session's marker) when nothing names
        // a scope, and the current project or session for a scope asked without its argument.
        var scopeDesc = Prop(h, "scope").GetProperty("description").GetString()!;
        Assert.Contains("when omitted", scopeDesc);
        Assert.Contains("else the current project when the current session is known from its marker, else all", scopeDesc);
        Assert.Contains("project without a `project` argument means the current project", scopeDesc);
        Assert.Contains("session without a `session` argument the current session", scopeDesc);
        var projectDesc = Prop(h, "project").GetProperty("description").GetString()!;
        Assert.Contains("without `scope` it selects project scope", projectDesc);
        Assert.Contains("when omitted, the current project is searched if the current session is known from its marker", projectDesc);
        Assert.DoesNotContain("(default)", scopeDesc);
        Assert.DoesNotContain("(default)", projectDesc);
        AssertType(h, "project", "string");
        AssertType(h, "session", "string");
        AssertType(h, "since", "string");
        AssertType(h, "include_automated", "boolean");
        Assert.False(Prop(h, "include_automated").GetProperty("default").GetBoolean());
        AssertType(h, "top_k", "integer");
        Assert.Equal(1, Prop(h, "top_k").GetProperty("minimum").GetInt32());
        Assert.Equal(50, Prop(h, "top_k").GetProperty("maximum").GetInt32());
        Assert.Equal(10, Prop(h, "top_k").GetProperty("default").GetInt32());
    }

    [Fact]
    public void GetSchema_MatchesTheSpecWithoutOneOf()
    {
        var h = new TranscriptGetHandler(() => false);
        Assert.Equal(
            new[] { "id", "session", "from_exchange", "to_exchange", "context", "max_tokens" }.OrderBy(x => x),
            PropNames(h).OrderBy(x => x));
        Assert.Empty(Required(h));
        AssertType(h, "id", "integer");
        AssertType(h, "session", "string");
        AssertType(h, "from_exchange", "integer");
        AssertType(h, "to_exchange", "integer");
        AssertType(h, "context", "integer");
        Assert.Equal(0, Prop(h, "context").GetProperty("minimum").GetInt32());
        Assert.Equal(10, Prop(h, "context").GetProperty("maximum").GetInt32());
        Assert.Equal(1, Prop(h, "context").GetProperty("default").GetInt32());
        AssertType(h, "max_tokens", "integer");
        Assert.Equal(200, Prop(h, "max_tokens").GetProperty("minimum").GetInt32());
        Assert.Equal(20000, Prop(h, "max_tokens").GetProperty("maximum").GetInt32());
        Assert.Equal(2000, Prop(h, "max_tokens").GetProperty("default").GetInt32());
        var budget = Prop(h, "max_tokens").GetProperty("description").GetString()!;
        Assert.Contains("token budget", budget);
        Assert.Contains("context: 0", budget);
        var raw = h.InputSchema.GetRawText();
        Assert.DoesNotContain("oneOf", raw);
        Assert.DoesNotContain("anyOf", raw);
        Assert.DoesNotContain("allOf", raw);
    }

    [Fact]
    public void SessionsSchema_MatchesTheSpec()
    {
        var h = new TranscriptSessionsHandler(() => false);
        Assert.Equal(
            new[] { "project", "since", "before", "limit", "include_automated" }.OrderBy(x => x),
            PropNames(h).OrderBy(x => x));
        Assert.Empty(Required(h));
        AssertType(h, "project", "string");
        AssertType(h, "since", "string");
        Assert.Contains("active since", Prop(h, "since").GetProperty("description").GetString());
        AssertType(h, "before", "string");
        AssertType(h, "limit", "integer");
        Assert.Equal(1, Prop(h, "limit").GetProperty("minimum").GetInt32());
        Assert.Equal(100, Prop(h, "limit").GetProperty("maximum").GetInt32());
        Assert.Equal(20, Prop(h, "limit").GetProperty("default").GetInt32());
        AssertType(h, "include_automated", "boolean");
        Assert.False(Prop(h, "include_automated").GetProperty("default").GetBoolean());
    }

    // ---- transcript_search with a service ----

    private sealed class FixedEmbedder : IEmbedder
    {
        public List<string> Calls { get; } = new();
        public EmbedderDescriptor Descriptor { get; } = new("local", "bge-small-en-v1.5", "rev1", 384);
        public float[] Embed(string text)
        {
            Calls.Add(text);
            var v = new float[384];
            v[0] = 1f;
            return v;
        }
    }

    /// <summary>A temporary archive file with one session of two chunks, one with a vector.</summary>
    private sealed class Archive : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "tr-ts-" + Guid.NewGuid().ToString("N"));
        public string File => Path.Combine(Dir, "archive.db");
        public long VectorChunk { get; }
        public long WordChunk { get; }

        public Archive(bool populate = true)
        {
            Directory.CreateDirectory(Dir);
            var open = TranscriptStore.Open(new TranscriptLocation(File, IsMainDatabase: false), create: true);
            Assert.Equal(TranscriptOpenStatus.Opened, open.Status);
            open.Store!.Dispose();
            if (!populate) return;

            using var conn = SqliteOpener.Open(File, pooling: false);
            long Scalar(string sql, params (string, object)[] ps)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
                return Convert.ToInt64(cmd.ExecuteScalar());
            }
            var s = Scalar(
                "INSERT INTO tx_session (host, host_session_id, project_key, project_key_source, kind, title) " +
                "VALUES ('claude-code', 'abc', 'owner/repo', 'remote', 'interactive', 'A title'); SELECT last_insert_rowid();");
            const string insertChunk =
                "INSERT INTO tx_chunk (session_id, exchange_start, exchange_end, part, chunk_type, has_error, ts, redaction_version, content) " +
                "VALUES ($s, $e, $e + 1, 0, 'dialogue', 0, '2026-11-18T10:02:11.0000000Z', 1, $c); SELECT last_insert_rowid();";
            VectorChunk = Scalar(insertChunk, ("$s", s), ("$e", 0), ("$c", "[owner/repo]\n\nwe deployed the staging site"));
            WordChunk = Scalar(insertChunk, ("$s", s), ("$e", 2), ("$c", "[owner/repo]\n\nSQLITE_BUSY: database is locked"));
            var vec = new float[384];
            vec[0] = 1f;
            var bytes = new byte[384 * sizeof(float)];
            Buffer.BlockCopy(vec, 0, bytes, 0, bytes.Length);
            Scalar("INSERT INTO tx_chunk_vec (rowid, session_id, project, kind, ts, embedding) VALUES ($r, $s, 'owner/repo', 'interactive', 0, $e); " +
                   "UPDATE tx_chunk SET embedded = 1 WHERE id = $r; SELECT 0;", ("$r", VectorChunk), ("$s", s), ("$e", bytes));
        }

        public static Core.Config.EmbeddingConfig LocalEmbedding { get; } = new("bge-small-en-v1.5", 384,
            FSharpOption<string>.None, FSharpOption<string>.None, FSharpOption<string>.None, FSharpOption<string>.None,
            FSharpOption<string>.None, FSharpOption<string>.None, FSharpOption<string>.None, FSharpOption<string>.None);

        public static TranscriptsSettings Settings { get; } = new(true, string.Empty, null, 0, false);

        /// <summary>The search over this archive; <paramref name="onContext"/> runs when the service reads its context.</summary>
        public TranscriptSearchService Service(IEmbedder embedder, Action? onContext = null) => new(() =>
        {
            onContext?.Invoke();
            return new TranscriptSearchContext(
                new TranscriptLocation(File, IsMainDatabase: false), Settings, "sqlite", LocalEmbedding, embedder);
        });

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
        }
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task Search_InactiveWithService_IsTheOffAnswer()
    {
        using var a = new Archive();
        var embedder = new FixedEmbedder();
        var sync = new UntouchableSync();
        var json = await Call(new TranscriptSearchHandler(() => false, a.Service(embedder), sync,
            () => throw new InvalidOperationException("no embed plan while off")));
        Assert.False(json.GetProperty("enabled").GetBoolean());
        Assert.Equal(OffText, json.GetProperty("hint").GetString());
        Assert.Empty(embedder.Calls);
        Assert.Equal(0, sync.Calls);
    }

    // ---- the sync before a read (spec 2026-11-24, section 3) ----

    private static SyncEmbedPlan Plan(IEmbedder embedder) =>
        new(embedder, Archive.Settings, "sqlite", Archive.LocalEmbedding, () => false);

    private const string IncompleteNote = "the newest text of the current session may be missing: the catch-up ran out of time";
    private const string FailedNote = "the newest text of the current session may be missing: the catch-up failed";

    private static CurrentSession MarkerSession(string id = "abc") =>
        new(CurrentKind.Marker, "claude-code", id, "/tmp/projects/x/" + id + ".jsonl", "/tmp/x");

    private static async Task<JsonElement> SearchJson(IToolHandler h, string args, CancellationToken ct = default)
    {
        var r = await h.ExecuteAsync(Args(args), ct);
        Assert.NotEqual(true, r.IsError);
        return JsonDocument.Parse(Assert.Single(r.Content).Text).RootElement.Clone();
    }

    [Theory, MemberData(nameof(Names))]
    public async Task Off_NoSyncCalled_NoMarkerRead(string name)
    {
        using var a = new Archive();
        var untouchable = new UntouchableSync();
        var ancestors = 0;
        var adapters = 0;
        var real = new TranscriptSyncService(
            () => throw new InvalidOperationException("no context while off"),
            new TranscriptSyncOptions(
                Ancestors: () => { Interlocked.Increment(ref ancestors); return new[] { 4242 }; },
                Adapters: () => { Interlocked.Increment(ref adapters); return Array.Empty<ITranscriptAdapter>(); },
                DataDir: a.Dir));
        var plans = 0;
        Func<SyncEmbedPlan?> plan = () => { plans++; return Plan(new FixedEmbedder()); };
        var reads = new TranscriptReadService(() => new TranscriptReadContext(new TranscriptLocation(a.File, IsMainDatabase: false)));

        foreach (ITranscriptSync sync in new ITranscriptSync[] { untouchable, real })
        {
            IToolHandler h = name switch
            {
                "transcript_search" => new TranscriptSearchHandler(() => false, a.Service(new FixedEmbedder()), sync, plan),
                "transcript_get" => new TranscriptGetHandler(() => false, reads, sync),
                _ => new TranscriptSessionsHandler(() => false, reads, sync),
            };
            var args = name == "transcript_search" ? "{\"query\":\"x\"}" : "{}";
            var json = await SearchJson(h, args);
            Assert.False(json.GetProperty("enabled").GetBoolean());
            Assert.Equal(OffText, json.GetProperty("hint").GetString());
        }

        Assert.Equal(0, untouchable.Calls);
        Assert.Equal(0, ancestors);
        Assert.Equal(0, adapters);
        Assert.Equal(0, plans);
    }

    [Fact]
    public async Task Search_CallsSyncAfterValidation_AndBeforeTheService()
    {
        using var a = new Archive();
        var order = new List<string>();
        var sync = new RecordingSync { OnCall = _ => order.Add("sync") };
        var embedder = new FixedEmbedder();
        var plan = Plan(embedder);
        var plans = 0;
        var h = new TranscriptSearchHandler(() => true, a.Service(embedder, () => order.Add("service")), sync,
            () => { plans++; return plan; });

        // Argument errors (an unknown scope, a malformed session, and the rest of the handler's parsing) sync nothing.
        foreach (var bad in new[]
                 {
                     "{\"query\":\"x\",\"scope\":\"everything\"}",
                     "{\"query\":\"x\",\"scope\":\"\"}",
                     "{\"query\":\"x\",\"session\":\"nocolon\"}",
                     "{\"query\":\"x\",\"since\":\"yesterday\"}",
                     "{\"query\":\"x\",\"top_k\":0}",
                     "{\"query\":\"\"}",
                 })
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => h.ExecuteAsync(Args(bad), CancellationToken.None));
        }
        Assert.Empty(sync.Plans);
        Assert.Empty(order);
        Assert.Equal(0, plans);

        // A valid call syncs once, with the search's embed plan, and only then searches.
        var json = await SearchJson(h, "{\"query\":\"SQLITE_BUSY: database is locked\",\"session\":\"claude-code:abc\"}");
        Assert.Equal(2, json.GetProperty("results").GetArrayLength());
        Assert.Same(plan, Assert.Single(sync.Plans));
        Assert.Equal(1, plans);
        Assert.Equal(new[] { "sync", "service" }, order);
    }

    [Fact]
    public async Task Search_SyncThrowing_StillAnswers_AndSaysTheTextMayBeMissing()
    {
        using var a = new Archive();
        const string args = "{\"query\":\"SQLITE_BUSY: database is locked\",\"session\":\"claude-code:abc\"}";

        void AssertMapped(JsonElement json)
        {
            Assert.True(json.GetProperty("enabled").GetBoolean());
            Assert.Equal(2, json.GetProperty("results").GetArrayLength());
            Assert.Equal("none", json.GetProperty("currentSession").GetString());
            Assert.False(json.TryGetProperty("currentSessionRef", out _));
            Assert.Equal(FailedNote, json.GetProperty("syncNote").GetString());
        }

        // A sync that throws (despite its contract), one that is cancelled by something other than this call, and an
        // embed plan that cannot be built: each is logged, and the search answers without the current session.
        var throwing = new RecordingSync { OnCall = _ => throw new InvalidOperationException("boom") };
        AssertMapped(await SearchJson(new TranscriptSearchHandler(() => true, a.Service(new FixedEmbedder()), throwing, () => null), args));
        Assert.Single(throwing.Plans);

        var otherCancel = new RecordingSync { OnCall = _ => throw new OperationCanceledException() };
        AssertMapped(await SearchJson(new TranscriptSearchHandler(() => true, a.Service(new FixedEmbedder()), otherCancel, () => null), args));

        var noPlan = new RecordingSync();
        AssertMapped(await SearchJson(new TranscriptSearchHandler(() => true, a.Service(new FixedEmbedder()), noPlan,
            () => throw new InvalidOperationException("settings unreadable")), args));
        Assert.Empty(noPlan.Plans);

        // The call's own cancellation is not swallowed.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = new RecordingSync { OnCall = ct => ct.ThrowIfCancellationRequested() };
        var serviceRead = false;
        var h = new TranscriptSearchHandler(() => true, a.Service(new FixedEmbedder(), () => serviceRead = true), cancelled, () => null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.ExecuteAsync(Args(args), cts.Token));
        Assert.False(serviceRead);
    }

    [Fact]
    public async Task Search_Answer_HasCurrentSessionFields_AndSyncNoteOnlyWhenIncomplete()
    {
        using var a = new Archive();
        var sync = new RecordingSync();
        var h = new TranscriptSearchHandler(() => true, a.Service(new FixedEmbedder()), sync, () => null);
        const string noScope = "{\"query\":\"SQLITE_BUSY: database is locked\"}";

        // A marker, complete: the current project is the default scope; no syncNote.
        sync.Answer = new SyncResult(MarkerSession(), true, null, "owner/repo");
        var marker = await SearchJson(h, noScope);
        Assert.Equal("marker", marker.GetProperty("currentSession").GetString());
        Assert.Equal("claude-code:abc", marker.GetProperty("currentSessionRef").GetString());
        Assert.False(marker.TryGetProperty("syncNote", out _));
        Assert.Equal("project", marker.GetProperty("scope").GetString());
        Assert.Equal("defaulted to the current project (owner/repo)", marker.GetProperty("scopeNote").GetString());
        Assert.Equal(2, marker.GetProperty("results").GetArrayLength());

        // A marker, incomplete: the note is passed through.
        sync.Answer = new SyncResult(MarkerSession(), false, IncompleteNote, "owner/repo");
        var late = await SearchJson(h, noScope);
        Assert.Equal("marker", late.GetProperty("currentSession").GetString());
        Assert.Equal(IncompleteNote, late.GetProperty("syncNote").GetString());

        // A marker for scope session without a session argument: the marker's session.
        sync.Answer = new SyncResult(MarkerSession(), true, null, "owner/repo");
        var own = await SearchJson(h, "{\"query\":\"SQLITE_BUSY: database is locked\",\"scope\":\"session\"}");
        Assert.Equal("session", own.GetProperty("scope").GetString());

        // A guess never chooses the scope, but is reported.
        sync.Answer = new SyncResult(new CurrentSession(CurrentKind.Guessed, "claude-code", "abc", "/p/abc.jsonl", null), true, null, null);
        var guessed = await SearchJson(h, noScope);
        Assert.Equal("guessed", guessed.GetProperty("currentSession").GetString());
        Assert.Equal("claude-code:abc", guessed.GetProperty("currentSessionRef").GetString());
        Assert.Equal("all", guessed.GetProperty("scope").GetString());
        Assert.False(guessed.TryGetProperty("syncNote", out _));

        // None: no ref, no note.
        sync.Answer = new SyncResult(CurrentSession.None, true, null, null);
        var none = await SearchJson(h, noScope);
        Assert.Equal("none", none.GetProperty("currentSession").GetString());
        Assert.False(none.TryGetProperty("currentSessionRef", out _));
        Assert.False(none.TryGetProperty("syncNote", out _));
        Assert.Equal("all", none.GetProperty("scope").GetString());

        Assert.Equal(5, sync.Plans.Count);
    }

    [Fact]
    public async Task Search_Active_ReturnsTheDtoJson()
    {
        using var a = new Archive();
        var embedder = new FixedEmbedder();
        var h = new TranscriptSearchHandler(() => true, a.Service(embedder));
        var r = await h.ExecuteAsync(Args("{\"query\":\"SQLITE_BUSY: database is locked\",\"session\":\"claude-code:abc\"}"), CancellationToken.None);
        Assert.NotEqual(true, r.IsError);
        var json = JsonDocument.Parse(Assert.Single(r.Content).Text).RootElement;

        Assert.True(json.GetProperty("enabled").GetBoolean());
        Assert.Equal("SQLITE_BUSY: database is locked", json.GetProperty("query").GetString());
        Assert.Equal("session", json.GetProperty("scope").GetString());
        Assert.False(json.TryGetProperty("scopeNote", out _));
        Assert.Equal("on", json.GetProperty("search").GetProperty("meaning").GetString());
        Assert.False(json.GetProperty("search").TryGetProperty("reason", out _));
        Assert.Equal(1, json.GetProperty("pendingEmbedding").GetInt64()); // the word chunk has no vector
        Assert.False(json.TryGetProperty("truncated", out _));
        Assert.False(json.TryGetProperty("truncatedHits", out _));
        Assert.False(json.TryGetProperty("note", out _));

        var results = json.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(2, results.Length);
        var top = results[0];
        Assert.Equal(a.WordChunk, top.GetProperty("id").GetInt64());
        Assert.Equal("claude-code:abc", top.GetProperty("session").GetString());
        Assert.Equal("owner/repo", top.GetProperty("project").GetString());
        Assert.Equal("interactive", top.GetProperty("kind").GetString());
        Assert.Equal("A title", top.GetProperty("title").GetString());
        Assert.Equal("2026-11-18T10:02:11.0000000Z", top.GetProperty("ts").GetString());
        Assert.Equal("dialogue", top.GetProperty("chunkType").GetString());
        Assert.False(top.GetProperty("hasError").GetBoolean());
        Assert.Equal("2-3", top.GetProperty("exchanges").GetString());
        Assert.Equal(new[] { "phrase", "words" }, top.GetProperty("via").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(1.0, top.GetProperty("score").GetDouble(), 6);
        Assert.Equal("SQLITE_BUSY: database is locked", top.GetProperty("text").GetString());

        var second = results[1];
        Assert.Equal(a.VectorChunk, second.GetProperty("id").GetInt64());
        Assert.Equal(new[] { "meaning" }, second.GetProperty("via").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Single(embedder.Calls);
    }

    [Fact]
    public void Dto_CurrentSessionAlwaysPresent_RefAndSyncNoteOnlyWhenSet()
    {
        static JsonElement Wire(TranscriptSearchResponse r) =>
            JsonDocument.Parse(JsonSerializer.Serialize(TranscriptSearchResponseDto.From(r), JsonContext.Default.TranscriptSearchResponseDto)).RootElement;
        static TranscriptSearchResponse Response(string current = "none", string? @ref = null, string? sync = null) =>
            new("q", "all", null, true, null, 0, Array.Empty<TranscriptSearchHit>(), 0, null, null, current, @ref, sync);

        var none = Wire(Response());
        Assert.Equal("none", none.GetProperty("currentSession").GetString());
        Assert.False(none.TryGetProperty("currentSessionRef", out _));
        Assert.False(none.TryGetProperty("syncNote", out _));

        var marker = Wire(Response("marker", "claude-code:abc", "the newest text of the current session may be missing: the catch-up ran out of time"));
        Assert.Equal("marker", marker.GetProperty("currentSession").GetString());
        Assert.Equal("claude-code:abc", marker.GetProperty("currentSessionRef").GetString());
        Assert.Equal("the newest text of the current session may be missing: the catch-up ran out of time", marker.GetProperty("syncNote").GetString());

        var guessed = Wire(Response("guessed", "pi:x"));
        Assert.Equal("guessed", guessed.GetProperty("currentSession").GetString());
        Assert.Equal("pi:x", guessed.GetProperty("currentSessionRef").GetString());
        Assert.False(guessed.TryGetProperty("syncNote", out _));
    }

    [Fact]
    public async Task Search_EmptyArchive_NoteAndEmptyResults()
    {
        using var a = new Archive(populate: false);
        var r = await new TranscriptSearchHandler(() => true, a.Service(new FixedEmbedder()))
            .ExecuteAsync(Args("{\"query\":\"anything\",\"scope\":\"project\"}"), CancellationToken.None);
        var json = JsonDocument.Parse(Assert.Single(r.Content).Text).RootElement;
        Assert.True(json.GetProperty("enabled").GetBoolean());
        Assert.Empty(json.GetProperty("results").EnumerateArray());
        Assert.Equal("nothing is archived yet; run total-recall transcripts backfill", json.GetProperty("note").GetString());
        Assert.Equal("all", json.GetProperty("scope").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("scopeNote").GetString()));
    }

    [Fact]
    public async Task Search_MeaningOff_CarriesTheReason()
    {
        using var a = new Archive();
        var svc = new TranscriptSearchService(() => new TranscriptSearchContext(
            new TranscriptLocation(a.File, IsMainDatabase: false),
            new TranscriptsSettings(true, string.Empty, null, 0, false),
            "sqlite",
            new Core.Config.EmbeddingConfig("m", 384, FSharpOption<string>.Some("openai"), FSharpOption<string>.None, FSharpOption<string>.None,
                FSharpOption<string>.None, FSharpOption<string>.None, FSharpOption<string>.None, FSharpOption<string>.None, FSharpOption<string>.None),
            new FixedEmbedder()));
        var r = await new TranscriptSearchHandler(() => true, svc).ExecuteAsync(Args("{\"query\":\"database\"}"), CancellationToken.None);
        var json = JsonDocument.Parse(Assert.Single(r.Content).Text).RootElement;
        Assert.Equal("off", json.GetProperty("search").GetProperty("meaning").GetString());
        Assert.Equal("words only: the embedding provider is remote and allow_remote_embedding is off",
            json.GetProperty("search").GetProperty("reason").GetString());
        Assert.Equal(2, json.GetProperty("pendingEmbedding").GetInt64());
    }

    [Fact]
    public async Task Search_Truncated_CarriesTheFlagAndCount()
    {
        using var a = new Archive(populate: false);
        using (var conn = SqliteOpener.Open(a.File, pooling: false))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO tx_session (host, host_session_id, project_key, project_key_source, kind) VALUES ('pi', 'big', 'o/r', 'remote', 'interactive')";
            cmd.ExecuteNonQuery();
            var body = "zebra " + new string('x', 1794);
            for (var i = 0; i < 30; i++)
            {
                using var ins = conn.CreateCommand();
                ins.CommandText = "INSERT INTO tx_chunk (session_id, exchange_start, exchange_end, part, chunk_type, redaction_version, content) VALUES (1, $e, $e, 0, 'dialogue', 1, $c)";
                ins.Parameters.AddWithValue("$e", i);
                ins.Parameters.AddWithValue("$c", body);
                ins.ExecuteNonQuery();
            }
        }
        var r = await new TranscriptSearchHandler(() => true, a.Service(new FixedEmbedder()))
            .ExecuteAsync(Args("{\"query\":\"zebra\",\"session\":\"pi:big\",\"top_k\":30}"), CancellationToken.None);
        var json = JsonDocument.Parse(Assert.Single(r.Content).Text).RootElement;
        Assert.True(json.GetProperty("truncated").GetBoolean());
        Assert.Equal(8, json.GetProperty("truncatedHits").GetInt32());
        Assert.Equal(22, json.GetProperty("results").GetArrayLength());
    }

    public static IEnumerable<object[]> BadArguments() => new[]
    {
        new object[] { "{}" },
        new object[] { "{\"query\":\"\"}" },
        new object[] { "{\"query\":\"   \"}" },
        new object[] { "{\"query\":5}" },
        new object[] { "{\"query\":\"x\",\"since\":\"yesterday\"}" },
        new object[] { "{\"query\":\"x\",\"session\":\"nocolon\"}" },
        new object[] { "{\"query\":\"x\",\"top_k\":0}" },
        new object[] { "{\"query\":\"x\",\"top_k\":51}" },
        new object[] { "{\"query\":\"x\",\"top_k\":\"ten\"}" },
        new object[] { "{\"query\":\"x\",\"scope\":\"everything\"}" },
        new object[] { "{\"query\":\"x\",\"scope\":\"session\"}" },
        new object[] { "{\"query\":\"x\",\"include_automated\":\"yes\"}" },
    };

    [Theory, MemberData(nameof(BadArguments))]
    public async Task Search_BadArguments_SurfaceThroughErrorTranslator(string args)
    {
        using var a = new Archive();
        var h = new TranscriptSearchHandler(() => true, a.Service(new FixedEmbedder()));
        var ex = await Assert.ThrowsAnyAsync<ArgumentException>(() => h.ExecuteAsync(Args(args), CancellationToken.None));
        var r = ErrorTranslator.Translate(ex, new StringWriter());
        Assert.True(r.IsError);
        Assert.StartsWith("Invalid arguments:", Assert.Single(r.Content).Text);
    }

    [Fact]
    public async Task Search_NullArguments_IsAnArgumentError()
    {
        using var a = new Archive();
        var h = new TranscriptSearchHandler(() => true, a.Service(new FixedEmbedder()));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => h.ExecuteAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task Search_ServiceError_IsAnIsErrorResult()
    {
        using var a = new Archive();
        // a separate archive file named as the main database: Open refuses it
        var svc = new TranscriptSearchService(() => new TranscriptSearchContext(
            new TranscriptLocation(a.File, IsMainDatabase: true),
            new TranscriptsSettings(true, string.Empty, null, 0, false),
            "sqlite",
            new Core.Config.EmbeddingConfig("m", 384, FSharpOption<string>.None, FSharpOption<string>.None, FSharpOption<string>.None,
                FSharpOption<string>.None, FSharpOption<string>.None, FSharpOption<string>.None, FSharpOption<string>.None, FSharpOption<string>.None),
            new FixedEmbedder()));
        var r = await new TranscriptSearchHandler(() => true, svc).ExecuteAsync(Args("{\"query\":\"x\"}"), CancellationToken.None);
        Assert.True(r.IsError);
        Assert.Contains("holds only a transcript archive", Assert.Single(r.Content).Text);
    }

    // ---- registration points ----

    [Fact]
    public void SqliteRegistry_ContainsTheThreeToolsAnd53Total()
    {
        var home = Path.Combine(Path.GetTempPath(), "tr-tt-" + Guid.NewGuid().ToString("N"));
        var prev = Environment.GetEnvironmentVariable("TOTAL_RECALL_HOME");
        try
        {
            Environment.SetEnvironmentVariable("TOTAL_RECALL_HOME", home);
            using var handles = ServerComposition.OpenLocalForCatalog(Path.Combine(home, "t.db"));
            foreach (var n in ToolNames) Assert.True(handles.Registry.TryGet(n, out _), n);
            Assert.Equal(53, handles.Registry.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TOTAL_RECALL_HOME", prev);
            try { Directory.Delete(home, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task SqliteRegistry_GetAndSessions_ReadAPopulatedArchive()
    {
        var home = Path.Combine(Path.GetTempPath(), "tr-tt-" + Guid.NewGuid().ToString("N"));
        var prev = Environment.GetEnvironmentVariable("TOTAL_RECALL_HOME");
        try
        {
            Environment.SetEnvironmentVariable("TOTAL_RECALL_HOME", home);
            Directory.CreateDirectory(home);
            var db = Path.Combine(home, "t.db");
            // The archive lives in the main database by default ([transcripts] db_path unset).
            var open = TranscriptStore.Open(new TranscriptLocation(db, IsMainDatabase: true), create: true);
            Assert.Equal(TranscriptOpenStatus.Opened, open.Status);
            using (var store = open.Store!)
            {
                new ReadArchiveBuilder(store)
                    .Session("claude-code", "reg", title: "Registry session",
                        endedAt: new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero))
                    .Exchange(0, "first exchange").Exchange(1, "second exchange").Commit();
            }
            new TranscriptSentinel(home).Write();

            using var handles = ServerComposition.OpenLocalForCatalog(db, new ArchivePassOptions(Sync: HermeticSync.Options));
            Assert.True(handles.Registry.TryGet("transcript_sessions", out var sessions));
            Assert.True(handles.Registry.TryGet("transcript_get", out var get));

            var listed = await Call(sessions!);
            Assert.Equal(1, listed.GetProperty("total").GetInt64());
            var row = Assert.Single(listed.GetProperty("sessions").EnumerateArray());
            Assert.Equal("claude-code:reg", row.GetProperty("session").GetString());
            Assert.Equal("Registry session", row.GetProperty("title").GetString());

            var r = await get!.ExecuteAsync(Args("{\"session\":\"claude-code:reg\",\"from_exchange\":1,\"context\":0}"), CancellationToken.None);
            Assert.NotEqual(true, r.IsError);
            var read = JsonDocument.Parse(Assert.Single(r.Content).Text).RootElement;
            var ex = Assert.Single(read.GetProperty("exchanges").EnumerateArray());
            Assert.Equal(1, ex.GetProperty("n").GetInt32());
            Assert.Equal("second exchange", ex.GetProperty("text").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("TOTAL_RECALL_HOME", prev);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(home, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void CortexRegistry_ContainsTheThreeToolsAnd54Total()
    {
        var home = Path.Combine(Path.GetTempPath(), "tr-tt-" + Guid.NewGuid().ToString("N"));
        var prev = Environment.GetEnvironmentVariable("TOTAL_RECALL_HOME");
        try
        {
            Environment.SetEnvironmentVariable("TOTAL_RECALL_HOME", home);
            using var handles = ServerComposition.OpenCortexForTest(":memory:", "https://cortex.test", "tr_test123");
            foreach (var n in ToolNames) Assert.True(handles.Registry.TryGet(n, out _), n);
            Assert.Equal(54, handles.Registry.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TOTAL_RECALL_HOME", prev);
            try { Directory.Delete(home, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void TheHandlers_AreConstructedOnlyInOpenSqliteAndOpenCortexCore()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "src", "TotalRecall.Server", "ServerComposition.cs")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var text = File.ReadAllText(Path.Combine(dir!, "src", "TotalRecall.Server", "ServerComposition.cs"));

        // The method each construction sits in: the nearest preceding method header.
        var methodStarts = new[] { "BuildRegistry(", "OpenSqlite(", "OpenPostgres(", "OpenCortexCore(" }
            .Select(m => (Name: m, At: text.IndexOf("ServerCompositionHandles " + m, StringComparison.Ordinal) is var a && a >= 0 ? a : text.IndexOf("ToolRegistry " + m, StringComparison.Ordinal)))
            .Where(x => x.At >= 0).OrderBy(x => x.At).ToArray();
        Assert.Equal(4, methodStarts.Length);

        foreach (var type in new[] { "TranscriptSearchHandler", "TranscriptGetHandler", "TranscriptSessionsHandler" })
        {
            var homes = new List<string>();
            var i = 0;
            while ((i = text.IndexOf("new " + type + "(", i, StringComparison.Ordinal)) >= 0)
            {
                homes.Add(methodStarts.Last(m => m.At < i).Name);
                i++;
            }
            Assert.Equal(new[] { "OpenSqlite(", "OpenCortexCore(" }, homes.OrderBy(h => h == "OpenSqlite(" ? 0 : 1).ToArray());
        }
    }
}
