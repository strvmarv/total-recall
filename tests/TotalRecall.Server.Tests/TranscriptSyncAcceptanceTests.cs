// The sync-on-read acceptance tests (spec 2026-11-24, section 6: sync tests 1, 3, 5, 6, 9, 10, the composition seam
// and the acceptance table), on the real path: Claude Code session files generated with ReadFixtureHome are backfilled
// with TranscriptBackfill, the files then grow, and the three transcript tools are called through the registry of a
// server composition built with ServerComposition.OpenLocalForCatalog. The sync's seams are REAL ones, not the
// hermetic no-op (this file is exempt from TranscriptToolHermeticGuardTests on purpose): a marker for a fake host pid
// is written by hand under <dataDir>/live/ in the shape the hook writes, the engine's ancestor chain is a fake
// engine -> shim -> host chain given through TranscriptSyncOptions.Ancestors, and the adapter is the fixture home's
// ClaudeCodeAdapter (the real Claude Code directory is never read: HOME, USERPROFILE and CLAUDE_CONFIG_DIR point into
// the test's temporary tree). The process start check is skipped (ProcessStart: _ => null): the fake pids name no
// process.

namespace TotalRecall.Server.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Transcripts;
using TotalRecall.Server.Handlers;
using TotalRecall.TestSupport.Transcripts;
using Xunit;
using Xunit.Abstractions;
using SqliteOpener = TotalRecall.Infrastructure.Storage.SqliteConnection;

[Collection("process-environment-console")]
public sealed class TranscriptSyncAcceptanceTests : IDisposable
{
    private const string Host = "claude-code";
    private const string NotePrefix = "the newest text of the current session may be missing: ";
    private const string OffText = "Run `total-recall transcripts enable` in a terminal to turn the archive on.";

    // Fake pids: the engine's ancestors are engine -> shim -> host (the engine itself is not in its own list).
    private const int ShimA = 1_999_999_901;
    private const int HostA = 1_999_999_911;
    private const int ShimB = 1_999_999_902;
    private const int HostB = 1_999_999_922;
    private const int NoMarkerPid = 1_999_999_933;

    /// <summary>A remote provider the composition can build (no call at construction) and no allow_remote_embedding.</summary>
    private const string OpenAiEmbedding =
        "[embedding]\nprovider = \"openai\"\nendpoint = \"http://127.0.0.1:1\"\napi_key = \"x\"\n";

    private readonly ITestOutputHelper _output;
    private readonly Dictionary<string, string?> _saved = new();
    private readonly List<IDisposable> _compositions = new();
    private readonly List<Action> _cleanups = new();
    private readonly ClaudeFixtureHome _home = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tr-sync-accept-" + Guid.NewGuid().ToString("N"));

    public TranscriptSyncAcceptanceTests(ITestOutputHelper output) => _output = output;

    private string DataDir => Path.Combine(_dir, "data");
    private string Db => Path.Combine(DataDir, "total-recall.db");
    private string UserHome => Path.Combine(_dir, "userhome");
    private TranscriptLocation MainLocation => new(Db, IsMainDatabase: true);
    private string Alpha => Path.Combine(_home.Root, "work", "alpha");
    private string Beta => Path.Combine(_home.Root, "work", "beta");

    public void Dispose()
    {
        foreach (var cleanup in _cleanups)
        {
            try { cleanup(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        foreach (var c in _compositions) c.Dispose();
        foreach (var (k, v) in _saved) Environment.SetEnvironmentVariable(k, v);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        _home.Dispose();
    }

    // ---- setup -----------------------------------------------------------------------------------------------------

    private void Set(string name, string? value)
    {
        if (!_saved.ContainsKey(name)) _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    /// <summary>
    /// The data directory with <paramref name="config"/>, HOME and USERPROFILE redirected to an empty folder, the XDG
    /// and Hermes directories to absent folders, Claude Code's directory at the fixture home; the sentinel written when
    /// <paramref name="sentinel"/> and the archive tables created in the main database when <paramref name="createArchive"/>.
    /// </summary>
    private void Setup(string config, bool sentinel = true, bool createArchive = true)
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(UserHome);
        Set("TOTAL_RECALL_HOME", DataDir);
        Set("TOTAL_RECALL_DB_PATH", null);
        Set("HOME", UserHome);
        Set("USERPROFILE", UserHome);
        Set("CLAUDE_CONFIG_DIR", _home.Root);
        Set("XDG_CONFIG_HOME", Path.Combine(_dir, "xdg-config"));
        Set("XDG_DATA_HOME", Path.Combine(_dir, "xdg-data"));
        Set("HERMES_HOME", Path.Combine(_dir, "hermes"));
        File.WriteAllText(ConfigPath, config);
        if (sentinel) new TranscriptSentinel(DataDir).Write();
        if (createArchive)
        {
            var open = TranscriptStore.Open(MainLocation, create: true, UserHome);
            Assert.Equal(TranscriptOpenStatus.Opened, open.Status);
            open.Store!.Dispose();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private string ConfigPath => Path.Combine(DataDir, "config.toml");

    /// <summary>The archive on in the main database, with <paramref name="embedding"/> (a remote provider without the opt-in by default).</summary>
    private void ArchiveOn(string embedding = OpenAiEmbedding) => Setup("[transcripts]\nenabled = true\n" + embedding);

    private ITranscriptAdapter Adapter() => new ClaudeCodeAdapter(_home.ProjectsDir);

    /// <summary>The sync's seams: the fake ancestor chain, the fixture adapter, no process start check, and an optional embedder.</summary>
    private TranscriptSyncOptions Sync(int[] chain, ITranscriptAdapter? adapter = null, IEmbedder? embedder = null) =>
        new(Ancestors: () => chain,
            ProcessStart: _ => null,
            Adapters: () => new[] { adapter ?? Adapter() },
            Embedder: embedder);

    private ServerCompositionHandles Compose(TranscriptSyncOptions sync)
    {
        var handles = ServerComposition.OpenLocalForCatalog(Db, new ArchivePassOptions(Sync: sync));
        _compositions.Add(handles);
        return handles;
    }

    /// <summary>Backfills every placed session into the main database (ingest only, no embedder).</summary>
    private BackfillSummary Backfill(TranscriptLocation? location = null)
    {
        var open = TranscriptStore.Open(location ?? MainLocation, create: true, UserHome);
        Assert.Equal(TranscriptOpenStatus.Opened, open.Status);
        using var store = open.Store!;
        var summary = new TranscriptBackfill(store, new ClaudeCodeAdapter(_home.ProjectsDir), resolver: null, userHome: UserHome)
            .Run(CancellationToken.None);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(0, summary.Retry);
        return summary;
    }

    /// <summary>Runs <paramref name="act"/> on a second store over the main database, as the CLI does.</summary>
    private void WithStore(Action<ITranscriptStore> act)
    {
        var open = TranscriptStore.Open(MainLocation, create: false, UserHome);
        Assert.Equal(TranscriptOpenStatus.Opened, open.Status);
        using var store = open.Store!;
        act(store);
    }

    /// <summary>Writes <c>&lt;data&gt;/live/claude-code-&lt;pid&gt;.json</c> naming <paramref name="s"/>, in the shape the hook writes.</summary>
    private void Marker(PlacedSession s, int pid, string? cwd = null)
    {
        var live = Path.Combine(DataDir, "live");
        Directory.CreateDirectory(live);
        var marker = new JsonObject
        {
            ["v"] = 1,
            ["host"] = Host,
            ["host_pid"] = pid,
            ["session_id"] = s.Id,
            ["transcript_path"] = s.Path,
            ["cwd"] = cwd ?? s.Cwd,
            ["at"] = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        };
        File.WriteAllText(Path.Combine(live, Host + "-" + pid.ToString(CultureInfo.InvariantCulture) + ".json"), marker.ToJsonString());
    }

    private string Stamp(int seconds) =>
        _home.BaseTime.AddSeconds(seconds).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Appends the compaction fixture's boundary and its summary (which holds <c>zqxsummary</c>) to <paramref name="s"/>
    /// at <paramref name="second"/> and the second after, with record ids of their own.
    /// </summary>
    private void AppendCompaction(PlacedSession s, int second)
    {
        var lines = Encoding.UTF8.GetString(_home.Render("compaction", s.Cwd, s.Id)).Split('\n');
        string Restamp(string line, int from, int to) => line
            .Replace(Stamp(from), Stamp(to), StringComparison.Ordinal)
            .Replace("00000000-0000-4000-8000-", "11111111-1111-4111-8111-", StringComparison.Ordinal);
        _home.AppendLine(s, Restamp(lines.Single(l => l.Contains("compact_boundary", StringComparison.Ordinal)), 20, second));
        _home.AppendLine(s, Restamp(lines.Single(l => l.Contains("zqxsummary", StringComparison.Ordinal)), 21, second + 1));
    }

    /// <summary>A directory that the project rule reads as a repository with an <c>origin</c> remote.</summary>
    private static void SetRemote(string repo, string remoteUrl)
    {
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".git", "config"),
            $"[core]\n\trepositoryformatversion = 0\n[remote \"origin\"]\n\turl = {remoteUrl}\n");
    }

    private long Scalar(string sql, params (string Name, object Value)[] args) => ScalarIn(Db, sql, args);

    private static long ScalarIn(string db, string sql, params (string Name, object Value)[] args)
    {
        using var conn = SqliteOpener.Open(db, pooling: false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private List<string> Strings(string sql)
    {
        using var conn = SqliteOpener.Open(Db, pooling: false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values;
    }

    private long ChunksWith(string word, string? db = null) =>
        ScalarIn(db ?? Db, "SELECT count(*) FROM tx_chunk WHERE content LIKE $p", ("$p", "%" + word + "%"));

    private long SessionRows(string hostSessionId) =>
        Scalar("SELECT count(*) FROM tx_session WHERE host_session_id = $h", ("$h", hostSessionId));

    private long Embedded() => Scalar("SELECT count(*) FROM tx_chunk WHERE embedded = 1");

    private static string Key(string hostSessionId) => Host + ":" + hostSessionId;

    // ---- calls -----------------------------------------------------------------------------------------------------

    private static JsonElement? Parse(string? args) => args is null ? null : JsonDocument.Parse(args).RootElement.Clone();

    private static async Task<JsonElement> Call(ServerCompositionHandles h, string tool, string? args)
    {
        Assert.True(h.Registry.TryGet(tool, out var handler), tool);
        var r = await handler!.ExecuteAsync(Parse(args), CancellationToken.None);
        var content = Assert.Single(r.Content);
        Assert.True(r.IsError != true, $"{tool} {args} answered an error: {content.Text}");
        return JsonDocument.Parse(content.Text).RootElement.Clone();
    }

    private static async Task<string> ArgumentError(ServerCompositionHandles h, string tool, string? args)
    {
        Assert.True(h.Registry.TryGet(tool, out var handler));
        var ex = await Assert.ThrowsAnyAsync<ArgumentException>(() => handler!.ExecuteAsync(Parse(args), CancellationToken.None));
        return ex.Message;
    }

    private static string SearchArgs(string query, string? scope = null, string? project = null, string? session = null)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("query", query);
            if (scope is not null) w.WriteString("scope", scope);
            if (project is not null) w.WriteString("project", project);
            if (session is not null) w.WriteString("session", session);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static Task<JsonElement> Search(
        ServerCompositionHandles h, string query, string? scope = null, string? project = null, string? session = null) =>
        Call(h, "transcript_search", SearchArgs(query, scope, project, session));

    private static JsonElement[] Hits(JsonElement r) => r.GetProperty("results").EnumerateArray().ToArray();
    private static string[] HitSessions(JsonElement r) => Hits(r).Select(h => h.GetProperty("session").GetString()!).ToArray();
    private static bool Finds(JsonElement r, string word) =>
        Hits(r).Any(h => h.GetProperty("text").GetString()!.Contains(word, StringComparison.Ordinal));
    private static string? Opt(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) ? v.GetString() : null;

    private static void AssertMarker(JsonElement r, string hostSessionId)
    {
        Assert.Equal("marker", r.GetProperty("currentSession").GetString());
        Assert.Equal(Key(hostSessionId), Opt(r, "currentSessionRef"));
    }

    private static string GetArgs(string session, int from, int to) =>
        "{\"session\":\"" + session + "\",\"from_exchange\":" + from.ToString(CultureInfo.InvariantCulture)
        + ",\"to_exchange\":" + to.ToString(CultureInfo.InvariantCulture) + ",\"context\":0,\"max_tokens\":20000}";

    // ---- seams that count ------------------------------------------------------------------------------------------

    /// <summary>The fixture adapter, counting what is read and listed (per path).</summary>
    private sealed class CountingAdapter(ITranscriptAdapter inner) : ITranscriptAdapter
    {
        private readonly ConcurrentDictionary<string, int> _heads = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _records = new(StringComparer.Ordinal);
        private int _enumerations;

        public int Enumerations => Volatile.Read(ref _enumerations);
        public int HeadsOf(string path) => _heads.TryGetValue(path, out var n) ? n : 0;
        public int RecordReadsOf(string path) => _records.TryGetValue(path, out var n) ? n : 0;
        public int AnyReadOf(string path) => HeadsOf(path) + RecordReadsOf(path);

        public string Host => inner.Host;
        public string Root => inner.Root;
        public bool Detect() => inner.Detect();

        public SessionEnumeration EnumerateSessions()
        {
            Interlocked.Increment(ref _enumerations);
            return inner.EnumerateSessions();
        }

        public SessionHead ReadHead(SessionSource source)
        {
            _heads.AddOrUpdate(source.Path, 1, (_, n) => n + 1);
            return inner.ReadHead(source);
        }

        public bool IsLineStart(SessionSource source, long offset)
        {
            _records.AddOrUpdate(source.Path, 1, (_, n) => n + 1);
            return inner.IsLineStart(source, offset);
        }

        public IEnumerable<TranscriptRecord> ReadRecords(SessionSource source, long startOffset, ReadStats stats, CancellationToken ct)
        {
            _records.AddOrUpdate(source.Path, 1, (_, n) => n + 1);
            return inner.ReadRecords(source, startOffset, stats, ct);
        }
    }

    /// <summary>Counters behind the Ancestors, Adapters and Open seams of a sync.</summary>
    private sealed class SyncCounters
    {
        public int Ancestors;
        public int Adapters;
        public int Opens;

        public (int, int, int) Snapshot() => (Volatile.Read(ref Ancestors), Volatile.Read(ref Adapters), Volatile.Read(ref Opens));
    }

    private TranscriptSyncOptions CountingSync(SyncCounters counters, int[] chain, ITranscriptAdapter adapter) =>
        new(Ancestors: () => { Interlocked.Increment(ref counters.Ancestors); return chain; },
            ProcessStart: _ => null,
            Adapters: () => { Interlocked.Increment(ref counters.Adapters); return new[] { adapter }; },
            Open: location =>
            {
                Interlocked.Increment(ref counters.Opens);
                return TranscriptStore.Open(location, create: false, busyTimeoutMs: 500);
            });

    // ---- 1: the tail, across a compaction, with no pass and no other hook ------------------------------------------

    [Fact]
    public async Task AppendedText_AfterTheLastPass_FoundBySearch_NoPassNoHook()
    {
        ArchiveOn();
        var s = ReadFixtureHome.Place(_home, Alpha, "live-append",
            ReadFixtureHome.Dialogue(3, n => $"Opening question {n} zqxopen{n}", n => $"Opening answer {n}."));
        Assert.Equal(1, Backfill().New); // the last pass
        _home.AppendExchange(s, "zqxbefore1 what about the index?", "zqxbefore2 the index is fine.", 50);
        AppendCompaction(s, 60);
        _home.AppendExchange(s, "zqxafter1 now add the column.", "zqxafter2 the column is added.", 100);
        Marker(s, HostA);
        foreach (var word in new[] { "zqxbefore1", "zqxsummary", "zqxafter1" })
            Assert.Equal(0, ChunksWith(word)); // not archived by anything yet

        var h = Compose(Sync(new[] { ShimA, HostA }));

        foreach (var (word, chunkType) in new[]
                 {
                     ("zqxafter1", "dialogue"), ("zqxafter2", "dialogue"), ("zqxsummary", "compaction-summary"),
                     ("zqxbefore1", "dialogue"), ("zqxbefore2", "dialogue"), ("zqxopen1", "dialogue"),
                 })
        {
            var r = await Search(h, word);
            AssertMarker(r, s.Id);
            Assert.Null(Opt(r, "syncNote"));
            Assert.Equal("project", r.GetProperty("scope").GetString());
            Assert.Equal("defaulted to the current project (alpha)", Opt(r, "scopeNote"));
            var hit = Hits(r).FirstOrDefault(x => x.GetProperty("text").GetString()!.Contains(word, StringComparison.Ordinal));
            Assert.True(hit.ValueKind == JsonValueKind.Object, $"{word} was not found: {r}");
            Assert.Equal(Key(s.Id), hit.GetProperty("session").GetString());
            Assert.Equal(chunkType, hit.GetProperty("chunkType").GetString());
        }

        // No pass ran (session_start was never called), and the archive holds each new text once.
        Assert.Null(h.ArchivePass!.Last);
        Assert.Equal(1, ChunksWith("zqxafter1"));
        Assert.Equal(1, ChunksWith("zqxsummary"));
        Assert.Equal(1, SessionRows(s.Id));
    }

    // ---- 2: two sessions in one directory --------------------------------------------------------------------------

    [Fact]
    public async Task TwoSessionsOneDirectory_EachSearchResolvesItsOwn()
    {
        ArchiveOn();
        var a = ReadFixtureHome.Place(_home, Alpha, "two-a",
            ReadFixtureHome.Dialogue(2, n => $"Session A zqxshared question {n}", n => $"A answer {n}."));
        var b = ReadFixtureHome.Place(_home, Alpha, "two-b",
            ReadFixtureHome.Dialogue(2, n => $"Session B zqxshared question {n}", n => $"B answer {n}."));
        Assert.Equal(2, Backfill().New);
        _home.AppendExchange(a, "zqxshared zqxtaila new in A", "A tail answer.", 100);
        _home.AppendExchange(b, "zqxshared zqxtailb new in B", "B tail answer.", 100);
        Marker(a, HostA);
        Marker(b, HostB);

        var ha = Compose(Sync(new[] { ShimA, HostA }));
        var hb = Compose(Sync(new[] { ShimB, HostB }));

        foreach (var (h, own, other, ownTail, otherTail) in new[]
                 {
                     (ha, a, b, "zqxtaila", "zqxtailb"),
                     (hb, b, a, "zqxtailb", "zqxtaila"),
                 })
        {
            var r = await Search(h, "zqxshared", scope: "session");
            AssertMarker(r, own.Id);
            Assert.Equal("session", r.GetProperty("scope").GetString());
            Assert.NotEmpty(Hits(r));
            Assert.All(HitSessions(r), x => Assert.Equal(Key(own.Id), x));
            Assert.True(Finds(r, ownTail), $"{ownTail} is not in its own session's search: {r}");

            Assert.Empty(Hits(await Search(h, otherTail, scope: "session")));

            // Both sessions are in the one project, so the session scope is what separates them.
            var project = await Search(h, "zqxshared");
            Assert.Equal("defaulted to the current project (alpha)", Opt(project, "scopeNote"));
            Assert.Contains(Key(other.Id), HitSessions(project));
        }

        // Each composition brought in its own session's tail.
        Assert.Equal(1, ChunksWith("zqxtaila"));
        Assert.Equal(1, ChunksWith("zqxtailb"));
    }

    // ---- 3: forgotten text never comes back through a sync ---------------------------------------------------------

    [Fact]
    public async Task ForgottenAndCutoff_NeverComeBack_ThroughSync()
    {
        ArchiveOn();
        var adapter = new CountingAdapter(Adapter());
        var gone = ReadFixtureHome.Place(_home, Alpha, "gone-1",
            ReadFixtureHome.Dialogue(2, n => $"Gone question {n} zqxgone", n => $"Gone answer {n}."));
        var keep = ReadFixtureHome.Place(_home, Alpha, "keep-1",
            ReadFixtureHome.Dialogue(2, n => $"Keep question {n} zqxkeep", n => $"Keep answer {n}."));
        var repo = Path.Combine(_home.Root, "repos", "proj");
        SetRemote(repo, "https://github.com/Owner/Repo.git");
        var sub = Path.Combine(repo, "sub");
        Directory.CreateDirectory(sub);
        var proj = ReadFixtureHome.Place(_home, sub, "proj-1",
            ReadFixtureHome.Dialogue(2, n => $"Project question {n} zqxproj", n => $"Project answer {n}."));
        Assert.Equal(3, Backfill().New);
        Assert.Equal(1, Scalar("SELECT count(*) FROM tx_session WHERE host_session_id = 'proj-1' AND project_key = 'owner/repo'"));

        WithStore(store =>
        {
            Assert.Equal(1, store.ForgetSession(Host, gone.Id).Sessions);
            Assert.Equal(1, store.ForgetProject("owner/repo").Sessions);
        });
        _home.AppendExchange(gone, "zqxgonetail more", "Gone tail answer.", 100);
        _home.AppendExchange(proj, "zqxprojtail more", "Project tail answer.", 100);

        var h = Compose(Sync(new[] { ShimA, HostA }, adapter));

        async Task AssertNothing(PlacedSession current, params string[] words)
        {
            foreach (var word in words)
            {
                var r = await Search(h, word, scope: "all");
                AssertMarker(r, current.Id);
                Assert.Null(Opt(r, "syncNote")); // an excluded session is a complete sync
                Assert.Empty(Hits(r));
                Assert.Equal(0, ChunksWith(word));
            }
        }

        // Forgotten by id: nothing stored, and the file is never opened (not even its head).
        Marker(gone, HostA);
        await AssertNothing(gone, "zqxgone", "zqxgonetail");
        Assert.Equal(0, adapter.AnyReadOf(gone.Path));
        Assert.Equal(0, SessionRows(gone.Id));

        // Forgotten by project key: the head is read (the key comes from it), the records never.
        Marker(proj, HostA);
        await AssertNothing(proj, "zqxproj", "zqxprojtail");
        Assert.Equal(0, adapter.RecordReadsOf(proj.Path));
        Assert.True(adapter.HeadsOf(proj.Path) > 0, "the head of the project's session was read: the adapter counts reads");
        Assert.Equal(0, SessionRows(proj.Id));

        // Forgotten by root: the repository is re-keyed, so only the stored root keeps a new session under it out.
        SetRemote(repo, "https://github.com/Other/Thing.git");
        var rekeyed = ReadFixtureHome.Place(_home, sub, "proj-2",
            ReadFixtureHome.Dialogue(2, n => $"Rekeyed question {n} zqxrekeyed", n => $"Rekeyed answer {n}."));
        Marker(rekeyed, HostA);
        await AssertNothing(rekeyed, "zqxrekeyed");
        Assert.Equal(0, adapter.RecordReadsOf(rekeyed.Path));
        Assert.Equal(0, SessionRows(rekeyed.Id));

        // The tools that sync text only obey the same exclusions.
        var list = await Call(h, "transcript_sessions", "{\"include_automated\":true}");
        Assert.Equal(new[] { Key(keep.Id) }, list.GetProperty("sessions").EnumerateArray().Select(x => x.GetProperty("session").GetString()).ToArray());

        // The control: a session that is not forgotten is synced (the same marker, the same composition).
        var late = ReadFixtureHome.Place(_home, Alpha, "late-1",
            ReadFixtureHome.Dialogue(2, n => $"Late question {n} zqxearly", n => $"Late answer {n}."));
        Marker(late, HostA);
        Assert.True(Finds(await Search(h, "zqxearly", scope: "all"), "zqxearly"));
        Assert.True(adapter.AnyReadOf(late.Path) > 0, "the control session was read through the counting adapter");

        // Forget all: the global cutoff. Text written at or before it never comes back; text after it is kept.
        WithStore(store => Assert.True(store.ForgetAll(DateTimeOffset.UtcNow).Sessions >= 2));
        Thread.Sleep(20);
        var afterCutoff = (int)(DateTimeOffset.UtcNow.AddHours(1) - _home.BaseTime).TotalSeconds;
        _home.AppendExchange(late, "zqxstillold written with an old time", "Old answer.", 200);
        _home.AppendExchange(late, "zqxafterall written after the cutoff", "New answer.", afterCutoff);
        _home.AppendExchange(keep, "zqxkeepold written with an old time", "Old answer.", 200);

        await AssertNothing(late, "zqxearly", "zqxstillold");
        Assert.True(Finds(await Search(h, "zqxafterall", scope: "all"), "zqxafterall")); // the control
        Marker(keep, HostA);
        await AssertNothing(keep, "zqxkeep", "zqxkeepold");
        Marker(gone, HostA);
        await AssertNothing(gone, "zqxgone", "zqxgonetail");
        Assert.Equal(0, adapter.AnyReadOf(gone.Path));
    }

    // ---- 4: the defaults of section 4, through the registry --------------------------------------------------------

    [Fact]
    public async Task DefaultScope_Marker_And_NoMarker()
    {
        ArchiveOn();
        var cur = ReadFixtureHome.Place(_home, Alpha, "cur-1",
            ReadFixtureHome.Dialogue(2, n => $"Current zqxshared question {n}", n => $"Current answer {n}."));
        var oth = ReadFixtureHome.Place(_home, Beta, "oth-1",
            ReadFixtureHome.Dialogue(2, n => $"Other zqxshared question {n}", n => $"Other answer {n}."));
        Assert.Equal(2, Backfill().New);
        Marker(cur, HostA);
        var adapterCalls = 0;
        var h = Compose(new TranscriptSyncOptions(
            Ancestors: () => new[] { ShimA, HostA },
            ProcessStart: _ => null,
            Adapters: () => { Interlocked.Increment(ref adapterCalls); return new[] { Adapter() }; }));
        var curKey = Key(cur.Id);
        var othKey = Key(oth.Id);

        void Only(JsonElement r, string session)
        {
            Assert.NotEmpty(Hits(r));
            Assert.All(HitSessions(r), x => Assert.Equal(session, x));
        }

        void Both(JsonElement r) =>
            Assert.Equal(new[] { curKey, othKey }, HitSessions(r).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray());

        // With a marker.
        var r1 = await Search(h, "zqxshared", session: othKey); // scope omitted, session given
        AssertMarker(r1, cur.Id);
        Assert.Equal("session", r1.GetProperty("scope").GetString());
        Assert.Null(Opt(r1, "scopeNote"));
        Only(r1, othKey);

        var r2 = await Search(h, "zqxshared", project: "beta"); // scope omitted, project given
        Assert.Equal("project", r2.GetProperty("scope").GetString());
        Assert.Null(Opt(r2, "scopeNote"));
        Only(r2, othKey);

        var r3 = await Search(h, "zqxshared"); // neither: the current project, with a note
        Assert.Equal("project", r3.GetProperty("scope").GetString());
        Assert.Equal("defaulted to the current project (alpha)", Opt(r3, "scopeNote"));
        Only(r3, curKey);

        var r4 = await Search(h, "zqxshared", scope: "project"); // scope project, no project
        Assert.Equal("project", r4.GetProperty("scope").GetString());
        Assert.Null(Opt(r4, "scopeNote"));
        Only(r4, curKey);

        var r5 = await Search(h, "zqxshared", scope: "session"); // scope session, no session
        Assert.Equal("session", r5.GetProperty("scope").GetString());
        Only(r5, curKey);

        var r6 = await Search(h, "zqxshared", scope: "all");
        Assert.Equal("all", r6.GetProperty("scope").GetString());
        Assert.Null(Opt(r6, "scopeNote"));
        Both(r6);

        // A marker cwd that differs from the session file's first cwd (a cd into a subdirectory, a directory with no
        // remote elsewhere) still defaults to the project the chunks were stored under.
        foreach (var cwd in new[] { Path.Combine(Alpha, "sub", "deeper"), Path.Combine(_dir, "elsewhere", "other") })
        {
            Marker(cur, HostA, cwd);
            var r = await Search(h, "zqxshared");
            AssertMarker(r, cur.Id);
            Assert.Equal("defaulted to the current project (alpha)", Opt(r, "scopeNote"));
            Only(r, curKey);
        }

        // An unknown scope is refused before any sync.
        var before = Volatile.Read(ref adapterCalls);
        Assert.True(before > 0);
        foreach (var scope in new[] { "bogus", "", "Project" })
            Assert.Contains("scope must be session, project or all", await ArgumentError(h, "transcript_search", SearchArgs("zqxshared", scope)));
        Assert.Equal(before, Volatile.Read(ref adapterCalls));

        // Without a marker (no marker for this chain): the guess (the newest file) is disclosed and chooses nothing.
        var now = DateTimeOffset.UtcNow;
        _home.SetMtime(cur, now.AddMinutes(-5));
        _home.SetMtime(oth, now.AddMinutes(-1));
        var g = Compose(Sync(new[] { NoMarkerPid }));
        const string ProjectNote =
            "scope project was asked without a project argument (the current project could not be determined), so every project was searched";
        const string SessionError =
            "scope session needs a session argument (<host>:<host_session_id>); the current session is not known here";

        async Task TodaysBehaviour(string kind, string? currentRef)
        {
            var neither = await Search(g, "zqxshared");
            Assert.Equal(kind, neither.GetProperty("currentSession").GetString());
            Assert.Equal(currentRef, Opt(neither, "currentSessionRef"));
            Assert.Equal("all", neither.GetProperty("scope").GetString());
            Assert.Null(Opt(neither, "scopeNote"));
            Both(neither);

            var project = await Search(g, "zqxshared", scope: "project");
            Assert.Equal("all", project.GetProperty("scope").GetString());
            Assert.Equal(ProjectNote, Opt(project, "scopeNote"));
            Both(project);

            Assert.Contains(SessionError, await ArgumentError(g, "transcript_search", SearchArgs("zqxshared", "session")));

            var named = await Search(g, "zqxshared", session: curKey);
            Assert.Equal("session", named.GetProperty("scope").GetString());
            Only(named, curKey);
            var byProject = await Search(g, "zqxshared", project: "alpha");
            Assert.Equal("project", byProject.GetProperty("scope").GetString());
            Only(byProject, curKey);
            Both(await Search(g, "zqxshared", scope: "all"));
        }

        await TodaysBehaviour("guessed", othKey);

        // No file within the guess window: no current session at all.
        _home.SetMtime(cur, now.AddHours(-2));
        _home.SetMtime(oth, now.AddHours(-2));
        await TodaysBehaviour("none", null);
    }

    // ---- 5: the guess --------------------------------------------------------------------------------------------

    [Fact]
    public async Task GuessedSession_IsDisclosed_AndNeverChoosesScope()
    {
        ArchiveOn();
        var guessed = ReadFixtureHome.Place(_home, Alpha, "guess-1",
            ReadFixtureHome.Dialogue(2, n => $"Guessed zqxshared question {n}", n => $"Guessed answer {n}."));
        var older = ReadFixtureHome.Place(_home, Beta, "older-1",
            ReadFixtureHome.Dialogue(2, n => $"Older zqxshared question {n}", n => $"Older answer {n}."));
        Assert.Equal(2, Backfill().New);
        _home.AppendExchange(guessed, "zqxguesstail what next?", "Next we test.", 100);
        var now = DateTimeOffset.UtcNow;
        _home.SetMtime(older, now.AddMinutes(-20));
        _home.SetMtime(guessed, now.AddMinutes(-2));
        Marker(older, HostB); // the marker of another process (not an ancestor of this engine) is never used

        var h = Compose(Sync(new[] { ShimA, NoMarkerPid }));

        // The guessed session's tail is brought in, and the answer says the session was guessed.
        var tail = await Search(h, "zqxguesstail");
        Assert.Equal("guessed", tail.GetProperty("currentSession").GetString());
        Assert.Equal(Key(guessed.Id), Opt(tail, "currentSessionRef"));
        Assert.Null(Opt(tail, "syncNote"));
        Assert.True(Finds(tail, "zqxguesstail"), tail.ToString());
        Assert.Equal("all", tail.GetProperty("scope").GetString());

        // It never chooses a scope or a session.
        var shared = await Search(h, "zqxshared");
        Assert.Equal("all", shared.GetProperty("scope").GetString());
        Assert.Null(Opt(shared, "scopeNote"));
        Assert.Contains(Key(older.Id), HitSessions(shared));
        Assert.Contains(Key(guessed.Id), HitSessions(shared));
        Assert.Equal("all", (await Search(h, "zqxshared", scope: "project")).GetProperty("scope").GetString());
        Assert.Contains("the current session is not known here",
            await ArgumentError(h, "transcript_search", SearchArgs("zqxshared", "session")));

        // A file written 31 minutes ago is not guessed.
        _home.SetMtime(guessed, now.AddMinutes(-31));
        _home.SetMtime(older, now.AddMinutes(-40));
        var none = await Search(h, "zqxshared");
        Assert.Equal("none", none.GetProperty("currentSession").GetString());
        Assert.Null(Opt(none, "currentSessionRef"));
        Assert.Equal("all", none.GetProperty("scope").GetString());
    }

    // ---- 6: a failing sync never fails the read --------------------------------------------------------------------

    [Fact]
    public async Task FailingSync_NeverFailsTheRead()
    {
        ArchiveOn();
        var s = ReadFixtureHome.Place(_home, Alpha, "fail-1",
            ReadFixtureHome.Dialogue(2, n => $"Archived question {n} zqxarchived{n}", n => $"Archived answer {n}."));
        Assert.Equal(1, Backfill().New);
        Marker(s, HostA);
        var h = Compose(Sync(new[] { ShimA, HostA }));

        // The search answers from what is archived and says the newest text may be missing; get and sessions answer
        // as they do today. Returns the three calls' times in ms.
        async Task<double[]> AssertReadAnswers(string why)
        {
            var sw = Stopwatch.StartNew();
            var r = await Search(h, "zqxarchived1");
            var searchMs = sw.Elapsed.TotalMilliseconds;
            AssertMarker(r, s.Id);
            Assert.True(Finds(r, "zqxarchived1"), why + ": " + r);
            var note = Opt(r, "syncNote");
            Assert.True(note is not null, why + ": no syncNote in " + r);
            Assert.StartsWith(NotePrefix, note!, StringComparison.Ordinal);

            sw.Restart();
            var get = await Call(h, "transcript_get", GetArgs(Key(s.Id), 0, 1));
            var getMs = sw.Elapsed.TotalMilliseconds;
            Assert.Equal(2, get.GetProperty("exchanges").GetArrayLength());
            sw.Restart();
            var list = await Call(h, "transcript_sessions", null);
            var listMs = sw.Elapsed.TotalMilliseconds;
            Assert.Contains(Key(s.Id), list.GetProperty("sessions").EnumerateArray().Select(x => x.GetProperty("session").GetString()));
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{why}: syncNote \"{note}\"; search {searchMs:F0} ms, get {getMs:F0} ms, sessions {listMs:F0} ms"));
            return new[] { searchMs, getMs, listMs };
        }

        // A store busy past the short timeout: another connection holds the write lock.
        _home.AppendExchange(s, "zqxbusytail written while busy", "Busy answer.", 100);
        using (var writer = SqliteOpener.Open(Db, pooling: false))
        {
            using (var begin = writer.CreateCommand())
            {
                begin.CommandText = "BEGIN IMMEDIATE;";
                begin.ExecuteNonQuery();
            }

            // Each call waits for the lock once, for about 1 s (the 500 ms busy timeout, but the driver's Default
            // Timeout rounded up to whole seconds), not the 5 s default and well inside the 6 s deadline; the bound
            // leaves room for a slow machine and still catches a wait of two seconds or more.
            var times = await AssertReadAnswers("a store busy past the timeout");
            Assert.All(times, ms => Assert.True(ms < 2500, $"a read took {ms:F0} ms against a held lock"));
            Assert.Equal(0, ChunksWith("zqxbusytail"));

            using var rollback = writer.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
        }
        var recovered = await Search(h, "zqxbusytail");
        Assert.Null(Opt(recovered, "syncNote"));
        Assert.True(Finds(recovered, "zqxbusytail"));

        if (OperatingSystem.IsWindows())
        {
            _output.WriteLine("the unreadable-file and read-only-store cases use Unix file modes: not run on Windows");
            return;
        }

        // An unreadable session file.
        _home.AppendExchange(s, "zqxunreadtail written while unreadable", "Unreadable answer.", 200);
        File.SetUnixFileMode(s.Path, UnixFileMode.None);
        _cleanups.Add(() =>
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(s.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        });
        if (CanRead(s.Path))
        {
            ModeCheckUnavailable("this user reads a mode-000 file (root?): the unreadable-file case was not run");
        }
        else
        {
            await AssertReadAnswers("an unreadable session file");
            Assert.Equal(0, ChunksWith("zqxunreadtail"));
        }
        File.SetUnixFileMode(s.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Assert.True(Finds(await Search(h, "zqxunreadtail"), "zqxunreadtail")); // readable again: brought in

        // A store opened read-only: the archive file is not writable, so the sync's write fails. The archive moves to
        // its own file for this (the settings are read per call): made read-only by a chmod, the main database, which
        // the composition holds open, was still written by the sync (observed), so it cannot stand in for it here.
        var archive = Path.Combine(_dir, "separate", "archive.db");
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        File.WriteAllText(ConfigPath, "[transcripts]\nenabled = true\ndb_path = \""
            + archive.Replace("\\", "/", StringComparison.Ordinal) + "\"\n" + OpenAiEmbedding);
        Assert.Equal(1, Backfill(new TranscriptLocation(archive, IsMainDatabase: false)).New);
        _home.AppendExchange(s, "zqxreadonlytail written while read-only", "Read-only answer.", 300);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var mode = File.GetUnixFileMode(archive);
        File.SetUnixFileMode(archive, UnixFileMode.UserRead);
        _cleanups.Add(() =>
        {
            foreach (var file in new[] { archive, archive + "-wal", archive + "-shm" })
                if (!OperatingSystem.IsWindows() && File.Exists(file)) File.SetUnixFileMode(file, mode);
        });
        if (CanWrite(archive))
        {
            ModeCheckUnavailable("this user writes a read-only file (root?): the read-only-store case was not run");
        }
        else
        {
            await AssertReadAnswers("a store opened read-only");
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        // SQLite created the -wal and -shm files of the read-only archive with its mode (read-only), so the fault is
        // gone only once every file of the archive is writable again.
        foreach (var file in new[] { archive, archive + "-wal", archive + "-shm" })
            if (File.Exists(file)) File.SetUnixFileMode(file, mode);
        Assert.Equal(0, ChunksWith("zqxreadonlytail", archive));

        // Once the fault is gone the next read brings the tail in.
        var healed = await Search(h, "zqxreadonlytail");
        Assert.True(Opt(healed, "syncNote") is null, healed.ToString());
        Assert.True(Finds(healed, "zqxreadonlytail"));
        Assert.Equal(1, ChunksWith("zqxreadonlytail", archive));
    }

    /// <summary>
    /// A Unix-mode case that cannot take effect (the user can read or write the file anyway, as root can): skipped with
    /// a line in the test output, except under CI (the <c>CI</c> variable is set), where it fails instead.
    /// </summary>
    private void ModeCheckUnavailable(string why)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")))
            Assert.Fail(why + " (CI is set: run the tests as a user that file modes apply to)");
        _output.WriteLine(why);
    }

    private static bool CanRead(string path)
    {
        try { using var _ = File.OpenRead(path); return true; }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    private static bool CanWrite(string path)
    {
        try { using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite); return true; }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    // ---- 7: get and sessions store text only; no provider is touched without the opt-in ----------------------------

    [Fact]
    public async Task GetAndSessions_StoreTextOnly_NoProviderTouched()
    {
        // A loopback listener stands in for the remote provider and counts every connection made to it.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var connections = 0;
        var stop = new CancellationTokenSource();
        var accept = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    Interlocked.Increment(ref connections);
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { break; }
                catch (ObjectDisposedException) { break; }
            }
        });
        _cleanups.Add(() =>
        {
            stop.Cancel();
            listener.Stop();
            accept.Wait(TimeSpan.FromSeconds(5));
            stop.Dispose();
        });
        var port = ((IPEndPoint)listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);
        string Config(bool optIn) =>
            "[transcripts]\nenabled = true\n" + (optIn ? "allow_remote_embedding = true\n" : "") +
            "[embedding]\nprovider = \"openai\"\nendpoint = \"http://127.0.0.1:" + port + "\"\napi_key = \"x\"\n";

        Setup(Config(optIn: false));
        var s = ReadFixtureHome.Place(_home, Alpha, "text-only",
            ReadFixtureHome.Dialogue(2, n => $"Text question {n} zqxtext{n}", n => $"Text answer {n}."));
        Assert.Equal(1, Backfill().New);
        Marker(s, HostA);
        var recorder = new RecordingEmbedder("openai", 384);
        var seamed = Compose(Sync(new[] { ShimA, HostA }, embedder: recorder)); // the transcript tools use the recorder
        var real = Compose(Sync(new[] { ShimA, HostA }));                        // the composition's own remote embedder
        var baseline = Volatile.Read(ref connections);
        _output.WriteLine($"connections while composing: {baseline}");

        // transcript_get and transcript_sessions sync the tail (it is stored) and embed nothing, on either composition.
        var tail = 0;
        foreach (var h in new[] { seamed, real })
        {
            foreach (var tool in new[] { "transcript_get", "transcript_sessions" })
            {
                var word = "zqxtail" + (++tail).ToString(CultureInfo.InvariantCulture);
                _home.AppendExchange(s, word + " appended", "Tail answer.", 100 + 10 * tail);
                await Call(h, tool, tool == "transcript_get" ? GetArgs(Key(s.Id), 0, 0) : null);
                Assert.Equal(1, ChunksWith(word)); // the call synced
            }
        }
        Assert.Empty(recorder.Calls);
        Assert.Equal(0, Embedded());
        Assert.Equal(baseline, Volatile.Read(ref connections));

        // transcript_search without the opt-in: words only, no embedder call, no connection.
        foreach (var h in new[] { seamed, real })
        {
            var word = "zqxtail" + (++tail).ToString(CultureInfo.InvariantCulture);
            _home.AppendExchange(s, word + " appended", "Tail answer.", 100 + 10 * tail);
            var r = await Search(h, word, scope: "all");
            Assert.Equal("off", r.GetProperty("search").GetProperty("meaning").GetString());
            Assert.True(Finds(r, word));
        }
        Assert.Empty(recorder.Calls);
        Assert.Equal(0, Embedded());
        Assert.Equal(baseline, Volatile.Read(ref connections));

        // With the opt-in, the remote provider is used by the search alone (the control: the listener counts it).
        File.WriteAllText(ConfigPath, Config(optIn: true));
        var control = await Search(real, "zqxtext1", scope: "all");
        Assert.True(Finds(control, "zqxtext1"));
        var afterControl = Volatile.Read(ref connections);
        Assert.True(afterControl > baseline, "with the opt-in the search's embed step did not reach the provider");
        Assert.Equal(0, Embedded());

        // ...and its use is bounded: one embed round of at most 8 chunks, the probe and the query.
        for (var i = 0; i < 12; i++)
            _home.AppendExchange(s, $"zqxbound{i} appended", "Bound answer.", 1000 + 10 * i);
        var bounded = await Search(seamed, "zqxbound3", scope: "all");
        var documents = recorder.Calls.Where(c => c != EmbedProbe.Text && c != "zqxbound3").ToArray();
        _output.WriteLine($"with the opt-in: {recorder.Calls.Count} embedder calls, {documents.Length} chunks embedded");
        Assert.InRange(documents.Length, 1, 8);
        Assert.Equal(documents.Length, Embedded());
        var total = Scalar("SELECT count(*) FROM tx_chunk");
        Assert.Equal(total - documents.Length, bounded.GetProperty("pendingEmbedding").GetInt64());
        Assert.Equal(afterControl, Volatile.Read(ref connections)); // the seam's embedder replaced the remote one

        // get and sessions still embed nothing with the opt-in.
        var callsBefore = recorder.Calls.Count;
        var embeddedBefore = Embedded();
        _home.AppendExchange(s, "zqxlastget appended", "Last answer.", 2000);
        await Call(seamed, "transcript_get", GetArgs(Key(s.Id), 0, 0));
        _home.AppendExchange(s, "zqxlastlist appended", "Last answer.", 2010);
        await Call(seamed, "transcript_sessions", null);
        Assert.Equal(1, ChunksWith("zqxlastget"));
        Assert.Equal(1, ChunksWith("zqxlastlist"));
        Assert.Equal(callsBefore, recorder.Calls.Count);
        Assert.Equal(embeddedBefore, Embedded());

        // Nothing the read tools are built from names an embedder.
        foreach (var type in new[] { typeof(TranscriptGetHandler), typeof(TranscriptSessionsHandler), typeof(TranscriptReadService) })
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var mentioned = type.GetConstructors(all).SelectMany(c => c.GetParameters()).Select(p => p.ParameterType)
                .Concat(type.GetFields(all).Select(f => f.FieldType))
                .Concat(type.GetProperties(all).Select(p => p.PropertyType))
                .Concat(type.GetMethods(all).SelectMany(m => m.GetParameters()).Select(p => p.ParameterType))
                .Concat(type.GetMethods(all).Select(m => m.ReturnType))
                .Select(t => t.FullName ?? t.Name)
                .ToArray();
            foreach (var name in new[] { "IEmbedder", "SyncEmbedPlan", "EmbeddingConfig" })
                Assert.DoesNotContain(mentioned, n => n.Contains(name, StringComparison.Ordinal));
        }

        var root = RepoRoot();
        foreach (var file in new[]
                 {
                     Path.Combine(root, "src", "TotalRecall.Server", "Handlers", "TranscriptGetHandler.cs"),
                     Path.Combine(root, "src", "TotalRecall.Server", "Handlers", "TranscriptSessionsHandler.cs"),
                     Path.Combine(root, "src", "TotalRecall.Infrastructure", "Transcripts", "TranscriptReadService.cs"),
                 })
        {
            var source = File.ReadAllText(file);
            foreach (var name in new[] { "IEmbedder", "SyncEmbedPlan", "EmbedderFactory", "HttpClient", "_embedPlan" })
                Assert.False(Regex.IsMatch(source, @"\b" + name + @"\b"), $"{Path.GetFileName(file)} names {name}");
            if (file.EndsWith("Handler.cs", StringComparison.Ordinal))
                Assert.Matches(@"TranscriptSyncCall\.Run\(_sync,\s*null,", source); // no embed plan
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "catalog.json")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("the repository root was not found walking up from " + AppContext.BaseDirectory);
    }

    // ---- 8: off --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Off_NothingReadNothingOpened()
    {
        var archive = Path.Combine(_dir, "separate", "archive.db");
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        Setup("[transcripts]\nenabled = true\ndb_path = \"" + archive.Replace("\\", "/", StringComparison.Ordinal) + "\"\n"
              + OpenAiEmbedding, sentinel: false, createArchive: false);
        var s = ReadFixtureHome.Place(_home, Alpha, "off-1",
            ReadFixtureHome.Dialogue(2, n => $"Off question {n}", n => $"Off answer {n}."));
        Marker(s, HostA); // a sync that ran would find this session
        var counters = new SyncCounters();
        var adapter = new CountingAdapter(Adapter());
        var h = Compose(CountingSync(counters, new[] { ShimA, HostA }, adapter));

        async Task AssertOff()
        {
            foreach (var (tool, args) in new (string, string?)[]
                     {
                         ("transcript_search", "{\"query\":\"off\"}"),
                         ("transcript_search", "{\"query\":\"off\",\"scope\":\"session\"}"),
                         ("transcript_get", "{\"id\":1}"),
                         ("transcript_get", GetArgs(Key(s.Id), 0, 1)),
                         ("transcript_sessions", null),
                     })
            {
                var off = await Call(h, tool, args);
                Assert.False(off.GetProperty("enabled").GetBoolean());
                Assert.Equal(OffText, off.GetProperty("hint").GetString());
            }
        }

        await AssertOff();
        Assert.Equal((0, 0, 0), counters.Snapshot()); // no marker read, no host listed, no archive opened
        Assert.Equal(0, adapter.Enumerations);
        Assert.Equal(0, adapter.AnyReadOf(s.Path));
        Assert.False(File.Exists(archive));

        // The control: on, the same seams are used (and the archive, absent, is still not created).
        var sentinel = new TranscriptSentinel(DataDir);
        sentinel.Write();
        var on = await Call(h, "transcript_search", "{\"query\":\"off\"}");
        Assert.Equal("marker", on.GetProperty("currentSession").GetString());
        var (ancestors, adapters, opens) = counters.Snapshot();
        Assert.True(ancestors > 0 && adapters > 0 && opens > 0, $"ancestors {ancestors}, adapters {adapters}, opens {opens}");
        Assert.False(File.Exists(archive));

        // Off again: nothing more.
        sentinel.Remove();
        var snapshot = counters.Snapshot();
        await AssertOff();
        Assert.Equal(snapshot, counters.Snapshot());
    }

    // ---- 9: session_start and memory_search construct no finder ----------------------------------------------------

    [BgeModelFact]
    public async Task MemorySearchAndSessionStart_ConstructNoFinder()
    {
        BgeModel.Require(); // memory_search uses the composition's own embedder: a missing model must still fail under CI
        ArchiveOn(embedding: "");
        var s = ReadFixtureHome.Place(_home, Alpha, "boot-1",
            ReadFixtureHome.Dialogue(2, n => $"Boot question {n}", n => $"Boot answer {n}."));
        Marker(s, HostA);
        var counters = new SyncCounters();
        var adapter = new CountingAdapter(Adapter());
        var h = ServerComposition.OpenLocalForCatalog(Db, new ArchivePassOptions(
            MinInterval: TimeSpan.Zero, Sync: CountingSync(counters, new[] { ShimA, HostA }, adapter)));
        _compositions.Add(h);
        Assert.Equal((0, 0, 0), counters.Snapshot());

        await Call(h, "session_start", null);
        await h.ArchivePass!.WaitIdleAsync().WaitAsync(TimeSpan.FromMinutes(2));
        await Call(h, "memory_store", "{\"content\":\"We deploy the staging site with the blue-green script.\"}");
        await Call(h, "memory_search", "{\"query\":\"how do we deploy\"}");
        await Call(h, "session_start", null);
        Assert.Equal((0, 0, 0), counters.Snapshot());
        Assert.Equal(0, adapter.Enumerations); // the pass uses its own adapters, never the sync's

        // The control: the first transcript tool call uses the seams.
        var r = await Search(h, "boot");
        Assert.Equal("marker", r.GetProperty("currentSession").GetString());
        var (ancestors, adapters, opens) = counters.Snapshot();
        Assert.True(ancestors > 0 && adapters > 0 && opens > 0, $"ancestors {ancestors}, adapters {adapters}, opens {opens}");

        // Source scan: outside the sync itself, only the three transcript tools and the composition name the sync,
        // and only the sync builds the finder.
        var root = RepoRoot();
        var allowed = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["LiveSessionFinder"] = new[] { "LiveSessionFinder.cs", "TranscriptSyncService.cs" },
            ["ProcessAncestry"] = new[] { "ProcessAncestry.cs", "LiveSessionFinder.cs", "TranscriptSyncService.cs" },
            // TranscriptSyncCall.cs names only its NoteFailed constant (checked below).
            ["TranscriptSyncService"] = new[] { "TranscriptSyncService.cs", "ServerComposition.cs", "TranscriptSyncCall.cs" },
            ["ITranscriptSync"] = new[]
            {
                "TranscriptSyncService.cs", "TranscriptSearchHandler.cs", "TranscriptGetHandler.cs", "TranscriptSessionsHandler.cs",
                "TranscriptSyncCall.cs",
            },
            ["TranscriptSyncCall"] = new[]
            {
                "TranscriptSyncCall.cs", "TranscriptSearchHandler.cs", "TranscriptGetHandler.cs", "TranscriptSessionsHandler.cs",
            },
        };
        var sep = Path.DirectorySeparatorChar;
        var sources = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(sep + "bin" + sep, StringComparison.Ordinal) && !f.Contains(sep + "obj" + sep, StringComparison.Ordinal))
            .Select(f => (Name: Path.GetFileName(f), Code: CodeOnly(File.ReadAllText(f))))
            .ToArray();
        foreach (var (name, files) in allowed)
        {
            var naming = sources.Where(f => Regex.IsMatch(f.Code, @"\b" + name + @"\b")).Select(f => f.Name).Distinct().ToArray();
            Assert.Contains(files[0], naming); // the scan sees the type itself
            Assert.True(naming.All(files.Contains), $"{name} is named in {string.Join(", ", naming.Except(files))}");
        }
        var syncCall = sources.Single(f => f.Name == "TranscriptSyncCall.cs").Code;
        Assert.All(Regex.Matches(syncCall, @"\bTranscriptSyncService\b(\.\w+)?").Select(m => m.Value),
            v => Assert.Equal("TranscriptSyncService.NoteFailed", v));
        var composition = sources.Single(f => f.Name == "ServerComposition.cs").Code;
        foreach (var line in composition.Split('\n').Where(l => Regex.IsMatch(l, @"\btranscriptSync\b")))
            Assert.Matches(@"BuildTranscriptSync\(|TranscriptGetHandler\(|TranscriptSessionsHandler\(|^\s*transcriptSync,\s*$", line.TrimEnd('\r'));
    }

    /// <summary>The source without its comment lines (<c>//</c> and <c>///</c>), so a doc reference is not a use.</summary>
    private static string CodeOnly(string source) =>
        string.Join('\n', source.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    // ---- 10: a backlog; the current session's tail first ----------------------------------------------------------

    [Fact]
    public async Task Backlog_Search_EmbedsTailFirst_RestPending_DoesNotWait()
    {
        ArchiveOn(embedding: ""); // the local provider: the sync's gate lets it embed
        // The backlog: 200 waiting chunks of older sessions, stored first (so their ids are the lowest).
        var open = TranscriptStore.Open(MainLocation, create: false, UserHome);
        Assert.Equal(TranscriptOpenStatus.Opened, open.Status);
        using (var store = open.Store!)
        {
            var archive = new ReadArchiveBuilder(store);
            for (var i = 0; i < 50; i++)
            {
                var session = archive.Session(Host, "old-" + i.ToString("D3", CultureInfo.InvariantCulture), project: "owner/old",
                    title: "Old " + i.ToString(CultureInfo.InvariantCulture),
                    startedAt: _home.BaseTime.AddDays(-10), endedAt: _home.BaseTime.AddDays(-10).AddMinutes(5));
                for (var n = 0; n < 4; n++)
                    session.Exchange(n, $"User: old question {n}\n\nAssistant: old answer {n} of session {i}.");
                Assert.Equal(IngestOutcomeKind.Applied, session.Commit().Kind);
            }
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var s = ReadFixtureHome.Place(_home, Alpha, "backlog-cur",
            ReadFixtureHome.Dialogue(3, n => $"Current question {n} zqxcurrent{n}", n => $"Current answer {n}."));
        Assert.Equal(1, Backfill().New);
        for (var i = 0; i < 3; i++)
            _home.AppendExchange(s, $"zqxbacklogtail{i} new in the current session", "Tail answer.", 100 + 10 * i);
        Marker(s, HostA);
        var recorder = new RecordingEmbedder("local", 384);
        var h = Compose(Sync(new[] { ShimA, HostA }, embedder: recorder));

        var sw = Stopwatch.StartNew();
        var r = await Search(h, "zqxbacklogtail1", scope: "all");
        sw.Stop();

        const string CurrentChunks =
            "FROM tx_chunk c JOIN tx_session s ON s.id = c.session_id WHERE s.host_session_id = 'backlog-cur'";
        var current = Scalar("SELECT count(*) " + CurrentChunks);
        var total = Scalar("SELECT count(*) FROM tx_chunk");
        var embedded = Embedded();
        Assert.Equal(6, current);
        Assert.Equal(206, total);
        Assert.True(Scalar("SELECT min(c.id) " + CurrentChunks) >
                    Scalar("SELECT max(c.id) FROM tx_chunk c JOIN tx_session s ON s.id = c.session_id WHERE s.project_key = 'owner/old'"),
            "the backlog's ids must be the lowest, so a plain read by id would embed the backlog first");

        // The current session's chunks were embedded first; at most 8 in all; the rest waits and is counted.
        Assert.Equal(0, Scalar("SELECT count(*) " + CurrentChunks + " AND c.embedded = 0"));
        Assert.InRange(embedded, current, 8);
        Assert.Equal("on", r.GetProperty("search").GetProperty("meaning").GetString());
        Assert.Equal(total - embedded, r.GetProperty("pendingEmbedding").GetInt64());
        Assert.True(Finds(r, "zqxbacklogtail1"));
        AssertMarker(r, s.Id);
        Assert.Null(Opt(r, "syncNote"));

        var documents = recorder.Calls.Where(c => c != EmbedProbe.Text && c != "zqxbacklogtail1").ToArray();
        Assert.Equal(embedded, documents.Length);
        // The first texts sent to the embedder (after its probe) are exactly the current session's chunks.
        Assert.Equal(
            Strings("SELECT c.content " + CurrentChunks).OrderBy(x => x, StringComparer.Ordinal),
            documents.Take((int)current).OrderBy(x => x, StringComparer.Ordinal));
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"backlog search: {sw.Elapsed.TotalMilliseconds:F0} ms; {embedded} of {total} chunks embedded ({current} of the current session); pendingEmbedding {total - embedded}"));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6), $"the search took {sw.Elapsed} with a backlog of {total - current}");
    }
}
