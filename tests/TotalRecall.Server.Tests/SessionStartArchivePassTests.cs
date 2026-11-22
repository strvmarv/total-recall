// The session_start trigger of the transcript archive (search the archive, spec section 4): the handler kicks the
// pass once per call after the response is built, a throwing pass never fails session_start, the sqlite and cortex
// compositions build the pass before BuildRegistry (with the test options applied) and Postgres passes none.

namespace TotalRecall.Server.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Transcripts;
using TotalRecall.Server.Handlers;
using TotalRecall.Server.Tests.TestSupport;
using Xunit;
using SqliteOpener = TotalRecall.Infrastructure.Storage.SqliteConnection;

[Collection("process-environment-console")]
public sealed class SessionStartArchivePassTests
{
    private sealed class RecordingPass : IArchivePass
    {
        private readonly List<string> _events;
        public RecordingPass(List<string> events) => _events = events;
        public int Kicks { get; private set; }
        public Exception? Throw { get; init; }

        public void Kick()
        {
            Kicks++;
            _events.Add("kick");
            if (Throw is not null) throw Throw;
        }
    }

    private sealed class OrderedLifecycle : ISessionLifecycle
    {
        private readonly List<string> _events;
        public OrderedLifecycle(List<string> events) => _events = events;
        public string SessionId => "sess-archive";
        public bool IsInitialized => true;

        public Task<SessionInitResult> EnsureInitializedAsync(CancellationToken ct = default)
        {
            _events.Add("init");
            return Task.FromResult(FakeSessionLifecycle.MakeDefaultResult("sess-archive"));
        }

        public Task<RefreshResult> RefreshAsync(string? task = null, CancellationToken ct = default) =>
            Task.FromResult(new RefreshResult());
    }

    [Fact]
    public async Task SessionStart_KicksThePass_AfterTheResponse()
    {
        var events = new List<string>();
        var pass = new RecordingPass(events);
        var handler = new SessionStartHandler(new OrderedLifecycle(events), archivePass: pass);

        var first = await handler.ExecuteAsync(null, CancellationToken.None);
        Assert.Equal(1, pass.Kicks);
        Assert.Equal(new[] { "init", "kick" }, events);

        var second = await handler.ExecuteAsync(null, CancellationToken.None);
        Assert.Equal(2, pass.Kicks); // once per ExecuteAsync; the pass itself decides whether one is due

        foreach (var r in new[] { first, second })
        {
            Assert.NotEqual(true, r.IsError);
            using var doc = JsonDocument.Parse(Assert.Single(r.Content).Text);
            Assert.Equal("sess-archive", doc.RootElement.GetProperty("sessionId").GetString());
        }
    }

    [Fact]
    public async Task SessionStart_PassThrows_StillReturnsResult()
    {
        var events = new List<string>();
        var pass = new RecordingPass(events) { Throw = new InvalidOperationException("pass exploded") };
        var handler = new SessionStartHandler(new OrderedLifecycle(events), archivePass: pass);

        var original = Console.Error;
        try
        {
            Console.SetError(new StringWriter());
            var r = await handler.ExecuteAsync(null, CancellationToken.None);

            Assert.NotEqual(true, r.IsError);
            using var doc = JsonDocument.Parse(Assert.Single(r.Content).Text);
            Assert.Equal("sess-archive", doc.RootElement.GetProperty("sessionId").GetString());
            Assert.Equal(1, pass.Kicks);
        }
        finally
        {
            Console.SetError(original);
        }
    }

    [Fact]
    public async Task SessionStart_NoPass_Works()
    {
        var handler = new SessionStartHandler(new FakeSessionLifecycle());
        var r = await handler.ExecuteAsync(null, CancellationToken.None);
        Assert.NotEqual(true, r.IsError);
    }

    // ---- composition ----

    private static string CompositionText()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "src", "TotalRecall.Server", "ServerComposition.cs")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, "src", "TotalRecall.Server", "ServerComposition.cs"));
    }

    private static string Method(string text, string header)
    {
        var start = text.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing " + header);
        var next = text.IndexOf("\n    private static ", start + header.Length, StringComparison.Ordinal);
        var nextInternal = text.IndexOf("\n    internal static ", start + header.Length, StringComparison.Ordinal);
        var nextPublic = text.IndexOf("\n    public static ", start + header.Length, StringComparison.Ordinal);
        var end = new[] { next, nextInternal, nextPublic }.Where(i => i > 0).DefaultIfEmpty(text.Length).Min();
        return text.Substring(start, end - start);
    }

    [Fact]
    public void BuildRegistry_Postgres_NoPass()
    {
        var text = CompositionText();
        var postgres = Method(text, "private static ServerCompositionHandles OpenPostgres(");
        Assert.Contains("BuildRegistry(", postgres);
        Assert.DoesNotContain("archivePass", postgres, StringComparison.OrdinalIgnoreCase);

        // BuildRegistry hands the optional pass to the session_start handler and builds none itself.
        var build = Method(text, "public static ToolRegistry BuildRegistry(");
        Assert.Contains("IArchivePass? archivePass = null", build);
        Assert.Contains("archivePass: archivePass", build);
        Assert.DoesNotContain("new Infrastructure.Transcripts.ArchivePass(", build);
        Assert.DoesNotContain("new ArchivePass(", build);

        // The sqlite and cortex compositions build it before BuildRegistry and hand it in.
        foreach (var header in new[] { "private static ServerCompositionHandles OpenSqlite(", "private static ServerCompositionHandles OpenCortexCore(" })
        {
            var body = Method(text, header);
            var built = body.IndexOf("BuildArchivePass(", StringComparison.Ordinal);
            var registry = body.IndexOf("BuildRegistry(", StringComparison.Ordinal);
            Assert.True(built >= 0 && registry > built, header + " must build the pass before BuildRegistry");
            Assert.Contains("archivePass: archivePass", body);
        }
    }

    [Fact]
    public void BuildRegistry_WithoutAPass_StillRegistersSessionStart()
    {
        // The postgres and test paths call BuildRegistry with no pass: session_start is there and works.
        var home = Path.Combine(Path.GetTempPath(), "tr-sap-" + Guid.NewGuid().ToString("N"));
        var prev = Environment.GetEnvironmentVariable("TOTAL_RECALL_HOME");
        try
        {
            Environment.SetEnvironmentVariable("TOTAL_RECALL_HOME", home);
            using var handles = ServerComposition.OpenLocalForCatalog(Path.Combine(home, "t.db"));
            Assert.True(handles.Registry.TryGet("session_start", out _));
            Assert.NotNull(handles.ArchivePass); // sqlite builds one; nothing has started it
            Assert.Null(handles.ArchivePass!.Last);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TOTAL_RECALL_HOME", prev);
            try { Directory.Delete(home, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class CountingEmbedder : IEmbedder, IDisposable
    {
        public int Calls;
        public bool Disposed;
        public EmbedderDescriptor Descriptor { get; } = new("local", "bge-small-en-v1.5", "rev1", 384);

        public float[] Embed(string text)
        {
            Interlocked.Increment(ref Calls);
            var v = new float[384];
            v[text.Length % 384] = 1f;
            return v;
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// A data directory with the archive on: <c>config.toml</c> enables it, the sentinel exists, the main database holds
    /// the archive tables with one session of three chunks, and Claude Code's directory points at an empty folder.
    /// </summary>
    private sealed class ArchiveHome : IDisposable
    {
        private readonly Dictionary<string, string?> _saved = new();

        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "tr-sap-" + Guid.NewGuid().ToString("N"));
        public string Db => Path.Combine(Dir, "total-recall.db");

        public ArchiveHome(bool sentinel = true)
        {
            Directory.CreateDirectory(Dir);
            Set("TOTAL_RECALL_HOME", Dir);
            Set("TOTAL_RECALL_DB_PATH", null);
            Set("CLAUDE_CONFIG_DIR", Path.Combine(Dir, "claude"));
            File.WriteAllText(Path.Combine(Dir, "config.toml"), "[transcripts]\nenabled = true\n");
            if (sentinel) new TranscriptSentinel(Dir).Write();

            var open = TranscriptStore.Open(new TranscriptLocation(Db, IsMainDatabase: true), create: true);
            Assert.Equal(TranscriptOpenStatus.Opened, open.Status);
            open.Store!.Dispose();
            using var conn = SqliteOpener.Open(Db, pooling: false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO tx_session (host, host_session_id, project_key, project_key_source, kind) VALUES ('claude-code', 'abc', 'o/r', 'remote', 'interactive');" +
                "INSERT INTO tx_chunk (session_id, exchange_start, exchange_end, part, chunk_type, redaction_version, content) VALUES (1, 0, 0, 0, 'dialogue', 1, 'one');" +
                "INSERT INTO tx_chunk (session_id, exchange_start, exchange_end, part, chunk_type, redaction_version, content) VALUES (1, 1, 1, 0, 'dialogue', 1, 'two two');" +
                "INSERT INTO tx_chunk (session_id, exchange_start, exchange_end, part, chunk_type, redaction_version, content) VALUES (1, 2, 2, 0, 'dialogue', 1, 'three three three');";
            cmd.ExecuteNonQuery();
        }

        private void Set(string name, string? value)
        {
            if (!_saved.ContainsKey(name)) _saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public long Embedded()
        {
            using var conn = SqliteOpener.Open(Db, pooling: false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM tx_chunk WHERE embedded = 1";
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        public void Dispose()
        {
            foreach (var (k, v) in _saved) Environment.SetEnvironmentVariable(k, v);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
        }
    }

    private static (ArchivePassOptions Options, List<int?> Threads, CountingEmbedder Embedder) FakeOptions()
    {
        var threads = new List<int?>();
        var embedder = new CountingEmbedder();
        var options = new ArchivePassOptions(
            EmbedBudget: new EmbedBudget(null, null),
            MinInterval: TimeSpan.Zero,
            Inspect: (cfg, t) =>
            {
                lock (threads) threads.Add(t);
                var d = embedder.Descriptor;
                return new EmbedderInspectionResult(embedder, new ArchiveFingerprint(d.Provider, d.Model, d.Revision, d.Dimensions),
                    new EmbedderReadiness(true, null), null);
            });
        return (options, threads, embedder);
    }

    [Fact]
    public async Task SqliteComposition_ThePass_EmbedsTheArchive_WithTheTestOptions()
    {
        using var home = new ArchiveHome();
        var (options, threads, embedder) = FakeOptions();
        using var handles = ServerComposition.OpenLocalForCatalog(home.Db, options);
        var pass = handles.ArchivePass;
        Assert.NotNull(pass);
        Assert.Equal(0, home.Embedded()); // building the composition started nothing

        pass!.Kick();
        await pass.WaitIdleAsync().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal("done", pass.Last!.Outcome);
        Assert.Equal(3, pass.Last.Embed!.Embedded);
        Assert.Equal(3, home.Embedded());
        Assert.Equal(new int?[] { 2 }, threads.ToArray());
        Assert.True(embedder.Disposed);
    }

    [Fact]
    public async Task SqliteComposition_NoSentinel_ThePassNeverStarts()
    {
        using var home = new ArchiveHome(sentinel: false);
        var (options, threads, _) = FakeOptions();
        using var handles = ServerComposition.OpenLocalForCatalog(home.Db, options);

        handles.ArchivePass!.Kick();
        await handles.ArchivePass.WaitIdleAsync().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Null(handles.ArchivePass.Last);
        Assert.Empty(threads);
        Assert.Equal(0, home.Embedded());
    }

    [Fact]
    public async Task CortexComposition_BuildsThePass_WithTheTestOptions()
    {
        using var home = new ArchiveHome();
        var (options, threads, _) = FakeOptions();
        using var handles = ServerComposition.OpenCortexForTest(home.Db, "https://cortex.test", "tr_test123", options);

        handles.ArchivePass!.Kick();
        await handles.ArchivePass.WaitIdleAsync().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(3, handles.ArchivePass.Last!.Embed!.Embedded);
        Assert.Equal(new int?[] { 2 }, threads.ToArray());
    }

    [Fact]
    public void Handles_Dispose_DisposesThePass()
    {
        using var home = new ArchiveHome();
        var (options, _, _) = FakeOptions();
        var handles = ServerComposition.OpenLocalForCatalog(home.Db, options);
        var pass = handles.ArchivePass!;

        handles.Dispose();
        pass.Kick(); // a disposed pass never starts

        Assert.Null(pass.Last);
        Assert.True(pass.WaitIdleAsync().IsCompleted);
    }
}
