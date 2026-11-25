// The sync-on-read through a server composition (spec 2026-11-24, sections 3 and 4): each composition (sqlite and
// cortex) builds one TranscriptSyncService lazily (no marker, ancestry or host directory is read at boot, by
// session_start or by another tool), and a marker for the host process makes transcript_search default to the current
// project. Every variable that locates a store or a host's files points into a temporary tree; the sync's seams come
// through ArchivePassOptions.Sync (the ancestors are a fake host pid, the adapter is the fixture home's).

namespace TotalRecall.Server.Tests;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Transcripts;
using TotalRecall.TestSupport.Transcripts;
using Xunit;

[Collection("process-environment-console")]
public sealed class TranscriptSyncHandlerTests : IDisposable
{
    private const string Host = "claude-code";
    private const int FakeHostPid = 1_999_999_917;

    /// <summary>A remote provider the composition can build (no call at construction) and no allow_remote_embedding.</summary>
    private const string OpenAiEmbedding =
        "[embedding]\nprovider = \"openai\"\nendpoint = \"http://127.0.0.1:1\"\napi_key = \"x\"\n";

    private readonly Dictionary<string, string?> _saved = new();
    private readonly List<IDisposable> _compositions = new();
    private readonly ClaudeFixtureHome _home = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tr-sync-handler-" + Guid.NewGuid().ToString("N"));

    private string DataDir => Path.Combine(_dir, "data");
    private string Db => Path.Combine(DataDir, "total-recall.db");
    private string UserHome => Path.Combine(_dir, "userhome");
    private TranscriptLocation MainLocation => new(Db, IsMainDatabase: true);
    private string Alpha => Path.Combine(_home.Root, "work", "alpha");
    private string Beta => Path.Combine(_home.Root, "work", "beta");

    public void Dispose()
    {
        foreach (var c in _compositions) c.Dispose();
        foreach (var (k, v) in _saved) Environment.SetEnvironmentVariable(k, v);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        _home.Dispose();
    }

    private void Set(string name, string? value)
    {
        if (!_saved.ContainsKey(name)) _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    /// <summary>
    /// The data directory with <paramref name="config"/>, the sentinel and the archive tables in the main database; HOME
    /// and USERPROFILE an empty folder, Claude Code's directory the fixture home, the XDG and Hermes directories absent.
    /// </summary>
    private void Setup(string config)
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
        File.WriteAllText(Path.Combine(DataDir, "config.toml"), config);
        new TranscriptSentinel(DataDir).Write();
        var open = TranscriptStore.Open(MainLocation, create: true, UserHome);
        Assert.Equal(TranscriptOpenStatus.Opened, open.Status);
        open.Store!.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private ServerCompositionHandles Compose(ArchivePassOptions options, string mode = "sqlite")
    {
        var handles = mode == "cortex"
            ? ServerComposition.OpenCortexForTest(Db, "https://cortex.test", "tr_test123", options)
            : ServerComposition.OpenLocalForCatalog(Db, options);
        _compositions.Add(handles);
        return handles;
    }

    private static async Task<JsonElement> Call(ServerCompositionHandles h, string tool, string? arguments)
    {
        Assert.True(h.Registry.TryGet(tool, out var handler), tool);
        using var args = arguments is null ? null : JsonDocument.Parse(arguments);
        var result = await handler!.ExecuteAsync(args?.RootElement, CancellationToken.None);
        Assert.NotEqual(true, result.IsError);
        using var doc = JsonDocument.Parse(Assert.Single(result.Content).Text);
        return doc.RootElement.Clone();
    }

    /// <summary>Backfills every placed session into the main database (no embedder).</summary>
    private void Backfill()
    {
        var open = TranscriptStore.Open(MainLocation, create: true, UserHome);
        Assert.Equal(TranscriptOpenStatus.Opened, open.Status);
        using (var store = open.Store!)
        {
            var summary = new TranscriptBackfill(store, new ClaudeCodeAdapter(_home.ProjectsDir), resolver: null, userHome: UserHome)
                .Run(CancellationToken.None);
            Assert.Equal(0, summary.Failed);
            Assert.True(summary.New > 0);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    /// <summary>Writes <c>&lt;data&gt;/live/claude-code-&lt;pid&gt;.json</c> naming <paramref name="s"/>, as the hook does.</summary>
    private void Marker(PlacedSession s, int pid)
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
            ["cwd"] = s.Cwd,
            ["at"] = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        };
        File.WriteAllText(Path.Combine(live, Host + "-" + pid.ToString(CultureInfo.InvariantCulture) + ".json"), marker.ToJsonString());
    }

    [BgeModelFact]
    public async Task Sync_NotConstructedAtBoot()
    {
        BgeModel.Require(); // memory_search uses the composition's own embedder: a missing model must still fail under CI
        Setup("[transcripts]\nenabled = true\n");
        var ancestors = 0;
        var adapters = 0;
        var options = new ArchivePassOptions(
            MinInterval: TimeSpan.Zero,
            Sync: new TranscriptSyncOptions(
                Ancestors: () => { Interlocked.Increment(ref ancestors); return new[] { FakeHostPid }; },
                ProcessStart: _ => null,
                Adapters: () =>
                {
                    Interlocked.Increment(ref adapters);
                    return new ITranscriptAdapter[] { new ClaudeCodeAdapter(_home.ProjectsDir) };
                }));

        var h = Compose(options);
        Assert.Equal(0, Volatile.Read(ref ancestors));
        Assert.Equal(0, Volatile.Read(ref adapters));

        await Call(h, "session_start", null);
        await h.ArchivePass!.WaitIdleAsync().WaitAsync(TimeSpan.FromMinutes(2));
        await Call(h, "memory_search", "{\"query\":\"how do we deploy\"}");
        Assert.Equal(0, Volatile.Read(ref ancestors));
        Assert.Equal(0, Volatile.Read(ref adapters));

        // The first transcript tool call builds it (the control: the seams above are the ones the sync uses).
        var json = await Call(h, "transcript_search", "{\"query\":\"anything\"}");
        Assert.Equal("none", json.GetProperty("currentSession").GetString());
        Assert.True(Volatile.Read(ref adapters) >= 1);
        Assert.True(Volatile.Read(ref ancestors) >= 1);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("cortex")]
    public async Task Composition_MarkerForFakeHostPid_MakesSearchDefaultToTheCurrentProject(string mode)
    {
        Setup("[transcripts]\nenabled = true\n" + OpenAiEmbedding);
        var alpha = ReadFixtureHome.Place(_home, Alpha, "alpha-live",
            ReadFixtureHome.Dialogue(2, n => $"Alpha zqxshared question {n}", n => $"Alpha answer {n}."));
        ReadFixtureHome.Place(_home, Beta, "beta-other",
            ReadFixtureHome.Dialogue(2, n => $"Beta zqxshared question {n}", n => $"Beta answer {n}."));
        Backfill();
        Marker(alpha, FakeHostPid);

        var h = Compose(new ArchivePassOptions(Sync: new TranscriptSyncOptions(
            Ancestors: () => new[] { FakeHostPid },
            ProcessStart: _ => null,
            Adapters: () => new ITranscriptAdapter[] { new ClaudeCodeAdapter(_home.ProjectsDir) })), mode);

        // Every project, asked for: the fixture has two, and the marker is still reported.
        var all = await Call(h, "transcript_search", "{\"query\":\"zqxshared\",\"scope\":\"all\"}");
        Assert.Equal("all", all.GetProperty("scope").GetString());
        var projects = all.GetProperty("results").EnumerateArray()
            .GroupBy(r => r.GetProperty("session").GetString()!)
            .ToDictionary(g => g.Key, g => g.First().GetProperty("project").GetString()!);
        Assert.Equal(new[] { Host + ":alpha-live", Host + ":beta-other" }, projects.Keys.OrderBy(k => k, StringComparer.Ordinal));
        var alphaProject = projects[Host + ":alpha-live"];
        Assert.NotEqual(alphaProject, projects[Host + ":beta-other"]);

        // No scope: the marker's project only.
        var json = await Call(h, "transcript_search", "{\"query\":\"zqxshared\"}");
        Assert.Equal("marker", json.GetProperty("currentSession").GetString());
        Assert.Equal(Host + ":alpha-live", json.GetProperty("currentSessionRef").GetString());
        Assert.False(json.TryGetProperty("syncNote", out _));
        Assert.Equal("project", json.GetProperty("scope").GetString());
        Assert.Equal($"defaulted to the current project ({alphaProject})", json.GetProperty("scopeNote").GetString());
        var results = json.GetProperty("results").EnumerateArray().ToArray();
        Assert.NotEmpty(results);
        Assert.All(results, r =>
        {
            Assert.Equal(alphaProject, r.GetProperty("project").GetString());
            Assert.Equal(Host + ":alpha-live", r.GetProperty("session").GetString());
        });
    }
}
