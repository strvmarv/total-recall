// session_start with the transcript archive on (search the archive, spec section 8, test 14): with the archive on and
// populated and the pass's worker held busy by a delayed embedder, the median session_start is within 10% of the
// median with the archive off; and memory_search returns identical results with the archive off, on and empty, and on
// and populated.

namespace TotalRecall.Server.Tests;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Importers;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using TotalRecall.Infrastructure.Transcripts;
using TotalRecall.Server.Handlers;
using TotalRecall.TestSupport.Transcripts;
using Xunit;
using Xunit.Abstractions;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;
using SqliteOpener = TotalRecall.Infrastructure.Storage.SqliteConnection;

[Collection("process-environment-console")]
public sealed class SessionStartArchiveSpeedTests : IDisposable
{
    private static readonly TimeSpan EnteredTimeout = TimeSpan.FromSeconds(30);

    private readonly ITestOutputHelper _output;
    private readonly Dictionary<string, string?> _saved = new();
    private readonly ClaudeFixtureHome _home = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tr-archive-speed-" + Guid.NewGuid().ToString("N"));

    public SessionStartArchiveSpeedTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
        Isolate();
    }

    public void Dispose()
    {
        foreach (var (k, v) in _saved) Environment.SetEnvironmentVariable(k, v);
        MsSqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        _home.Dispose();
    }

    // ---- isolation and setup ---------------------------------------------------------------------------------------

    private void Set(string name, string? value)
    {
        if (!_saved.ContainsKey(name)) _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    /// <summary>
    /// Every variable that locates a store or a host's files points into the temporary tree: the data directory and
    /// database path, HOME and USERPROFILE (an empty folder, so the host importers read nothing real), Claude Code's
    /// directory (the fixture home), and the XDG and Hermes directories.
    /// </summary>
    private void Isolate()
    {
        var userHome = Path.Combine(_dir, "userhome");
        Directory.CreateDirectory(userHome);
        Set("TOTAL_RECALL_HOME", Path.Combine(_dir, "unused"));
        Set("TOTAL_RECALL_DB_PATH", null);
        Set("HOME", userHome);
        Set("USERPROFILE", userHome);
        Set("CLAUDE_CONFIG_DIR", _home.Root);
        Set("XDG_CONFIG_HOME", Path.Combine(_dir, "xdg-config"));
        Set("XDG_DATA_HOME", Path.Combine(_dir, "xdg-data"));
        Set("HERMES_HOME", Path.Combine(_dir, "hermes"));
    }

    /// <summary>A data directory selected as <c>TOTAL_RECALL_HOME</c>; returns its database path.</summary>
    private string UseDataDir(string name)
    {
        var dataDir = Path.Combine(_dir, name);
        Directory.CreateDirectory(dataDir);
        Set("TOTAL_RECALL_HOME", dataDir);
        return Path.Combine(dataDir, "total-recall.db");
    }

    /// <summary>The archive on in the current data directory: config, sentinel and the archive tables in the main database.</summary>
    private static void EnableArchive(string db)
    {
        var dataDir = Path.GetDirectoryName(db)!;
        File.WriteAllText(Path.Combine(dataDir, "config.toml"), "[transcripts]\nenabled = true\n");
        new TranscriptSentinel(dataDir).Write();
        var open = TranscriptStore.Open(new TranscriptLocation(db, IsMainDatabase: true), create: true);
        Assert.Equal(TranscriptOpenStatus.Opened, open.Status);
        open.Store!.Dispose();
        MsSqliteConnection.ClearAllPools();
    }

    /// <summary>Ingests the search corpus into the archive with <see cref="TranscriptBackfill"/> alone (nothing embedded).</summary>
    private void Populate(string db)
    {
        var open = TranscriptStore.Open(new TranscriptLocation(db, IsMainDatabase: true), create: false);
        Assert.Equal(TranscriptOpenStatus.Opened, open.Status);
        using (var store = open.Store!)
        {
            var summary = new TranscriptBackfill(store, new ClaudeCodeAdapter(_home.ProjectsDir)).Run(CancellationToken.None);
            Assert.True(summary.New > 0);
        }
        MsSqliteConnection.ClearAllPools();
    }

    private static long Scalar(string db, string sql)
    {
        using var conn = SqliteOpener.Open(db, pooling: false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    // ---- the speed test --------------------------------------------------------------------------------------------

    /// <summary>
    /// A local 384-dimension embedder whose every call blocks on <see cref="Release"/> (a wait, no CPU spin) and whose
    /// first call sets <see cref="Entered"/>. The worker's first call is its probe, so once it is entered the pass has
    /// left the ingest step and holds the lease, idle.
    /// </summary>
    private sealed class DelayedEmbedder : IEmbedder
    {
        private int _calls;
        public EmbedderDescriptor Descriptor { get; } = new("local", "bge-small-en-v1.5", "rev1", 384);
        public ManualResetEventSlim Release { get; } = new(false);
        public ManualResetEventSlim Entered { get; } = new(false);
        public int Calls => Volatile.Read(ref _calls);

        public float[] Embed(string text)
        {
            Interlocked.Increment(ref _calls);
            Entered.Set();
            Release.Wait(TimeSpan.FromMinutes(5));
            var v = new float[384];
            v[text.Length % 384] = 1f;
            return v;
        }
    }

    private sealed record Arm(string Name, ServerCompositionHandles Handles, MsSqliteConnection Conn);

    /// <summary>One session_start on a new lifecycle and handler over the arm's database and its persistent pass; the elapsed ms.</summary>
    private static async Task<double> Call(Arm arm)
    {
        var lifecycle = new SessionLifecycle(
            Array.Empty<IImporter>(),
            new SqliteStore(arm.Conn),
            new CompactionLog(arm.Conn),
            sessionId: "speed-" + Guid.NewGuid().ToString("N"));
        var handler = new SessionStartHandler(lifecycle, archivePass: arm.Handles.ArchivePass);

        var sw = Stopwatch.StartNew();
        var result = await handler.ExecuteAsync(null, CancellationToken.None);
        sw.Stop();

        Assert.NotEqual(true, result.IsError);
        return sw.Elapsed.TotalMilliseconds;
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    [Fact]
    public async Task SessionStart_ArchiveOnAndPopulated_PassBusy_MedianWithinTenPercentOfArchiveOff()
    {
        var embedder = new DelayedEmbedder();
        var options = new ArchivePassOptions(
            MinInterval: TimeSpan.FromHours(1), // one pass per composition: later Kicks are no-ops
            Inspect: (_, _) =>
            {
                var d = embedder.Descriptor;
                return new EmbedderInspectionResult(
                    embedder, new ArchiveFingerprint(d.Provider, d.Model, d.Revision, d.Dimensions), new EmbedderReadiness(true, null), null);
            });

        // The archive-off arm: no config and no sentinel in its data directory.
        var offDb = UseDataDir("off");
        var offHandles = ServerComposition.OpenLocalForCatalog(offDb, options);

        // The archive-on arm: on, populated by an ingest that embeds nothing (so the fingerprint is unstamped).
        var onDb = UseDataDir("on");
        EnableArchive(onDb);
        SearchFixtureCorpus.Place(_home);
        Populate(onDb);
        var chunks = Scalar(onDb, "SELECT count(*) FROM tx_chunk");
        Assert.True(chunks > 0);
        Assert.Equal(0, Scalar(onDb, "SELECT count(*) FROM tx_chunk WHERE embedded = 1"));
        Assert.Equal(0, Scalar(onDb, "SELECT count(*) FROM tx_state WHERE key LIKE 'embed.%'"));
        var onHandles = ServerComposition.OpenLocalForCatalog(onDb, options);

        var off = new Arm("off", offHandles, SqliteOpener.Open(offDb));
        var on = new Arm("on", onHandles, SqliteOpener.Open(onDb));
        var pass = onHandles.ArchivePass!;
        try
        {
            // Two warm-up calls, kept out of the medians; the archive-on one starts the pass (the first Kick's whole path:
            // the interval check, the sentinel read and the Task.Run), whose worker then waits in its probe. Every later
            // on-arm call returns at Kick's running check, so the warm-up is the one timing of the starting path.
            var warmOff = await Call(off);
            var warmOn = await Call(on);
            Assert.True(embedder.Entered.Wait(EnteredTimeout),
                "the archive pass never reached the embedder within 30 s (the ingest or the gate stopped it): " +
                (pass.Last is { } last ? $"{last.Outcome}, embed {last.Embed?.State.Text}" : "the pass has not finished"));

            var timings = new List<(string Arm, double Ms)>();
            for (var i = 0; i < 10; i++)
            {
                timings.Add(("off", await Call(off)));
                timings.Add(("on", await Call(on)));
            }

            // The worker was held busy throughout, by the one pass the first Kick started.
            Assert.Null(pass.Last);
            Assert.Equal(1, embedder.Calls);
            Assert.Null(offHandles.ArchivePass!.Last);

            var medianOff = Median(timings.Where(t => t.Arm == "off").Select(t => t.Ms).ToArray());
            var medianOn = Median(timings.Where(t => t.Arm == "on").Select(t => t.Ms).ToArray());
            var slack = medianOff < 50 ? 5.0 : 0.0;
            var bound = medianOff * 1.10 + slack;
            var all = string.Join(", ", timings.Select(t => string.Create(CultureInfo.InvariantCulture, $"{t.Arm} {t.Ms:F2}")));
            var report = string.Create(CultureInfo.InvariantCulture,
                $"session_start medians over 10 calls each ({chunks} archived chunks, pass held in its probe): " +
                $"off {medianOff:F2} ms, on {medianOn:F2} ms; bound off x 1.10{(slack > 0 ? " + 5 ms" : "")} = {bound:F2} ms. " +
                $"Warm-up calls (first of each arm; the on one starts the pass): off {warmOff:F2} ms, on {warmOn:F2} ms. " +
                $"All twenty, in order: {all}");
            _output.WriteLine(report);

            Assert.True(medianOn <= bound, report);
        }
        finally
        {
            embedder.Release.Set();
            onHandles.Dispose();
            offHandles.Dispose();
            await pass.WaitIdleAsync().WaitAsync(TimeSpan.FromSeconds(60));
            off.Conn.Dispose();
            on.Conn.Dispose();
            MsSqliteConnection.ClearAllPools();
        }
    }

    // ---- memory_search is untouched --------------------------------------------------------------------------------

    private static string Json(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            write(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task<JsonElement> Call(ServerCompositionHandles handles, string tool, string arguments)
    {
        Assert.True(handles.Registry.TryGet(tool, out var handler));
        using var args = JsonDocument.Parse(arguments);
        var result = await handler!.ExecuteAsync(args.RootElement, CancellationToken.None);
        Assert.NotEqual(true, result.IsError);
        using var doc = JsonDocument.Parse(Assert.Single(result.Content).Text);
        return doc.RootElement.Clone();
    }

    private static void CopyDatabase(string from, string to)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            if (File.Exists(from + suffix))
                File.Copy(from + suffix, to + suffix);
        }
    }

    private const string Query = "how do we deploy the staging site";

    /// <summary>The <c>results</c> of one fixed <c>memory_search</c> on a composition over <paramref name="db"/>, as raw JSON.</summary>
    private static async Task<string> SearchResults(string db)
    {
        using var handles = ServerComposition.OpenLocalForCatalog(db);
        var root = await Call(handles, "memory_search", Json(w => { w.WriteString("query", Query); w.WriteNumber("topK", 5); }));
        var results = root.GetProperty("results");
        Assert.Equal(JsonValueKind.Array, results.ValueKind);
        return results.GetRawText();
    }

    [BgeModelFact]
    public async Task MemorySearch_IdenticalResults_ArchiveOff_OnEmpty_OnPopulated()
    {
        BgeModel.Require(); // the composition builds its own embedder: a missing model must still fail under CI

        // One memory store, seeded once with the archive off; each state searches its own copy of it, so the access
        // counts a search writes cannot differ between the states.
        var baseDb = UseDataDir("base");
        using (var handles = ServerComposition.OpenLocalForCatalog(baseDb))
        {
            foreach (var content in new[]
                     {
                         "We deploy the staging site with the blue-green script in tools/deploy.",
                         "The flaky integration test was fixed by retrying the timeout once.",
                         "Invoices are generated on the first of the month by the billing job.",
                         "Prefer short commit subjects in the imperative mood.",
                         "The staging database is refreshed from a production snapshot every Sunday.",
                     })
            {
                await Call(handles, "memory_store", Json(w => w.WriteString("content", content)));
            }
        }
        MsSqliteConnection.ClearAllPools();

        // Archive off.
        var offDb = UseDataDir("state-off");
        CopyDatabase(baseDb, offDb);
        var offResults = await SearchResults(offDb);
        MsSqliteConnection.ClearAllPools();

        // Archive on and empty.
        var emptyDb = UseDataDir("state-empty");
        CopyDatabase(baseDb, emptyDb);
        EnableArchive(emptyDb);
        var emptyResults = await SearchResults(emptyDb);
        MsSqliteConnection.ClearAllPools();

        // Archive on and populated: the corpus ingested and embedded (a fake embedder fills the vector table).
        var fullDb = UseDataDir("state-populated");
        CopyDatabase(baseDb, fullDb);
        EnableArchive(fullDb);
        SearchFixtureCorpus.Place(_home);
        Populate(fullDb);
        var open = TranscriptStore.Open(new TranscriptLocation(fullDb, IsMainDatabase: true), create: false);
        using (var store = open.Store!)
        {
            var run = new TranscriptEmbedWorker(store, new RecordingEmbedder("local", 384)).Run(
                new EmbedGateResult(EmbedState.Ready, "ready"), new ArchiveFingerprint("local", "recording-model", "1", 384),
                new EmbedBudget(null, null), CancellationToken.None);
            Assert.Equal(EmbedStopReason.Done, run.StoppedBy);
        }
        MsSqliteConnection.ClearAllPools();
        Assert.True(Scalar(fullDb, "SELECT count(*) FROM tx_chunk_vec") > 0);
        var fullResults = await SearchResults(fullDb);

        _output.WriteLine("memory_search results (archive off): " + offResults);
        using (var parsed = JsonDocument.Parse(offResults))
            Assert.True(parsed.RootElement.GetArrayLength() > 0, "the fixed query found no memory, so the comparison would be empty");
        Assert.Equal(offResults, emptyResults);
        Assert.Equal(offResults, fullResults);
    }
}
