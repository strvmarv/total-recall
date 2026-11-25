// The archive search acceptance queries through the session_start trigger (search the archive, spec section 8,
// query 13): a server composition built with the archive on and the fixture home, session_start called through the
// registry, the pass awaited through its WaitIdleAsync/Last seam (never the worker directly), then queries 1 to 4
// through the registry's transcript_search, with the real bge-small model.

namespace TotalRecall.Server.Tests;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Transcripts;
using TotalRecall.TestSupport.Transcripts;
using Xunit;
using Xunit.Abstractions;
using SqliteOpener = TotalRecall.Infrastructure.Storage.SqliteConnection;

[Collection("process-environment-console")]
public sealed class TranscriptSearchSessionStartAcceptanceTests : IDisposable
{
    private const string Host = "claude-code";

    private readonly ITestOutputHelper _output;
    private readonly Dictionary<string, string?> _saved = new();
    private readonly ClaudeFixtureHome _home = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tr-search-accept-" + Guid.NewGuid().ToString("N"));

    public TranscriptSearchSessionStartAcceptanceTests(ITestOutputHelper output) => _output = output;

    private string DataDir => Path.Combine(_dir, "data");
    private string Db => Path.Combine(DataDir, "total-recall.db");

    public void Dispose()
    {
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
    /// The data directory with the archive on (config, sentinel, empty archive tables), HOME and USERPROFILE redirected
    /// to an empty folder and the XDG and Hermes directories to absent folders under the test directory (so
    /// session_start's host imports, OpenCode's and Hermes's included, read nothing real), and Claude Code's directory at
    /// the fixture home. Dispose restores every variable.
    /// </summary>
    private void ArchiveOn()
    {
        Directory.CreateDirectory(DataDir);
        var userHome = Path.Combine(_dir, "userhome");
        Directory.CreateDirectory(userHome);
        Set("TOTAL_RECALL_HOME", DataDir);
        Set("TOTAL_RECALL_DB_PATH", null);
        Set("HOME", userHome);
        Set("USERPROFILE", userHome);
        Set("CLAUDE_CONFIG_DIR", _home.Root);
        Set("XDG_CONFIG_HOME", Path.Combine(_dir, "xdg-config"));
        Set("XDG_DATA_HOME", Path.Combine(_dir, "xdg-data"));
        Set("HERMES_HOME", Path.Combine(_dir, "hermes"));
        File.WriteAllText(Path.Combine(DataDir, "config.toml"), "[transcripts]\nenabled = true\n");
        new TranscriptSentinel(DataDir).Write();
        var open = TranscriptStore.Open(new TranscriptLocation(Db, IsMainDatabase: true), create: true);
        Assert.Equal(TranscriptOpenStatus.Opened, open.Status);
        open.Store!.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private long Scalar(string sql)
    {
        using var conn = SqliteOpener.Open(Db, pooling: false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string SessionKey(SearchCorpus corpus, string fixture) => Host + ":" + corpus.SessionIdOf(fixture);

    private static string Arguments(SearchCorpus corpus, SearchQuery q)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("query", q.Query);
            w.WriteString("scope", q.Scope);
            if (q.Project is not null) w.WriteString("project", q.Project);
            if (q.SessionFixture is not null) w.WriteString("session", SessionKey(corpus, q.SessionFixture));
            if (q.Since is not null) w.WriteString("since", q.Since);
            w.WriteBoolean("include_automated", q.IncludeAutomated);
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    [BgeModelFact]
    public async Task SessionStartPass_ThenQueries1To4_Pass()
    {
        BgeModel.Require(); // the composition builds its own embedder: a missing model must still fail under CI
        ArchiveOn();
        var corpus = SearchFixtureCorpus.Place(_home);
        var options = new ArchivePassOptions(
            EmbedBudget: new EmbedBudget(null, null),
            MinInterval: TimeSpan.Zero,
            Inspect: (cfg, threads) => BgeModel.Inspect(cfg, threads),
            Sync: HermeticSync.Options);

        using var handles = ServerComposition.OpenLocalForCatalog(Db, options);
        var pass = handles.ArchivePass;
        Assert.NotNull(pass);
        Assert.Equal(0, Scalar("SELECT count(*) FROM tx_session")); // building the composition read nothing

        Assert.True(handles.Registry.TryGet("session_start", out var sessionStart));
        var sw = Stopwatch.StartNew();
        var start = await sessionStart!.ExecuteAsync(null, CancellationToken.None);
        Assert.NotEqual(true, start.IsError);
        await pass!.WaitIdleAsync().WaitAsync(TimeSpan.FromMinutes(5));
        sw.Stop();

        var last = pass.Last;
        Assert.NotNull(last);
        Assert.Equal(ArchivePass.OutcomeDone, last!.Outcome);
        Assert.NotNull(last.Embed);
        Assert.Equal(0, last.Embed!.Remaining); // every chunk embedded
        Assert.Equal(0, last.Embed.GaveUp);
        var chunks = Scalar("SELECT count(*) FROM tx_chunk");
        Assert.True(chunks > 0);
        Assert.Equal(corpus.Sessions.Count, Scalar("SELECT count(*) FROM tx_session"));
        Assert.Equal(chunks, Scalar("SELECT count(*) FROM tx_chunk WHERE embedded = 1"));
        Assert.Equal(chunks, (long)last.Embed.Embedded);
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"corpus: {corpus.Sessions.Count} sessions, {chunks} chunks; session_start and its pass (ingest and embed, real bge-small) took {sw.Elapsed.TotalSeconds:F1} s"));

        Assert.True(handles.Registry.TryGet("transcript_search", out var search));
        foreach (var name in new[] { "how-to-staging", "how-to-flaky-test", "compaction-summary-fact", "exact-error", "session-paraphrase" })
        {
            var q = corpus.Queries.Single(x => x.Name == name);
            using var args = JsonDocument.Parse(Arguments(corpus, q));
            var result = await search!.ExecuteAsync(args.RootElement, CancellationToken.None);
            Assert.NotEqual(true, result.IsError);
            using var doc = JsonDocument.Parse(Assert.Single(result.Content).Text);
            var root = doc.RootElement;

            Assert.True(root.GetProperty("enabled").GetBoolean());
            Assert.Equal("on", root.GetProperty("search").GetProperty("meaning").GetString());
            Assert.Equal(0, root.GetProperty("pendingEmbedding").GetInt64());
            var top = root.GetProperty("results").EnumerateArray().Take(3).ToArray();
            _output.WriteLine($"{name}: " + string.Join(" | ", top.Select(h =>
                h.GetProperty("session").GetString() + " " + h.GetProperty("chunkType").GetString() + " [" +
                string.Join(",", h.GetProperty("via").EnumerateArray().Select(v => v.GetString())) + "]")));

            var key = SessionKey(corpus, q.ExpectedFixture);
            var hits = top.Where(h => h.GetProperty("session").GetString() == key).ToArray();
            Assert.True(hits.Length > 0, $"{q.ExpectedFixture} is not in the top three of \"{q.Query}\"");
            switch (q.Kind)
            {
                case "compaction-summary":
                    Assert.Contains(hits, h => h.GetProperty("chunkType").GetString() == "compaction-summary");
                    break;
                case "error":
                    Assert.Contains(hits, h => h.GetProperty("via").EnumerateArray().Any(v => v.GetString() == "phrase"));
                    break;
                case "session-paraphrase":
                    Assert.Equal("session", root.GetProperty("scope").GetString());
                    Assert.All(root.GetProperty("results").EnumerateArray(),
                        h => Assert.Equal(key, h.GetProperty("session").GetString()));
                    Assert.Contains(hits, h => h.GetProperty("text").GetString()!.Contains("exponential backoff", StringComparison.Ordinal));
                    // the words run alone finds this chunk ("attempts" is in the query): it must also come from the
                    // vector search, so a broken session filter on that search is detected
                    Assert.Contains(hits, h =>
                        h.GetProperty("text").GetString()!.Contains("exponential backoff", StringComparison.Ordinal)
                        && h.GetProperty("via").EnumerateArray().Any(v => v.GetString() == "meaning"));
                    break;
            }
        }
    }
}
