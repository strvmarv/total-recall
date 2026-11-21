using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using TotalRecall.Infrastructure.Transcripts;
using TotalRecall.TestSupport.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

/// <summary>
/// The acceptance tests of the Claude Code backup (spec "Acceptance"): the whole synthetic corpus, each fixture placed
/// under its own working directory, one <see cref="TranscriptBackfill"/>, then assertions on the real archive.
/// </summary>
public sealed class TranscriptCorpusTests : IDisposable
{
    private const string Host = IngestHarness.Host;

    private static readonly string[] Fixtures =
    [
        "decision", "compaction", "error", "automated-sdk", "automated-opening",
        "secrets", "no-kept", "straddle", "worktree-missing", "worktree-repo",
    ];

    private readonly IngestHarness _h = new();
    private readonly Dictionary<string, PlacedSession> _placed = new();
    private string _repo = "";

    public void Dispose() => _h.Dispose();

    private TranscriptTestDb Db => _h.Db;
    private ClaudeFixtureHome Home => _h.Home;

    /// <summary>Places every fixture: one working directory each (never created), the two worktree ones under a repository with a remote.</summary>
    private void PlaceCorpus()
    {
        _repo = IngestHarness.MakeRepo(Path.Combine(Home.Root, "repos", "widgets"), "https://github.com/Owner/Repo.git");
        foreach (var fixture in Fixtures)
        {
            var cwd = fixture switch
            {
                "worktree-missing" => Path.Combine(_repo, ".claude", "worktrees", "gone"),
                "worktree-repo" => _repo,
                _ => Path.Combine(Home.Root, "work", fixture),
            };
            _placed[fixture] = Home.Place(fixture, cwd, "corpus-" + fixture);
        }
    }

    private BackfillSummary Run(ITranscriptAdapter? adapter = null) =>
        new TranscriptBackfill(Db.Store, adapter ?? _h.Adapter(), resolver: null, userHome: _h.UserHome).Run(CancellationToken.None);

    private BackfillSummary PlaceAndRun()
    {
        PlaceCorpus();
        return Run();
    }

    private List<string> Contents() => IngestHarness.Contents(Db);

    private string AllText() => string.Join("\n\u0001\n", Contents().Concat(Titles()));

    private List<string> Titles()
    {
        var list = new List<string>();
        using var conn = Db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT coalesce(title, '') || ' ' || coalesce(cwd, '') || ' ' || coalesce(project_key, '') FROM tx_session";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    private List<string> ContentsOf(string fixture)
    {
        var list = new List<string>();
        using var conn = Db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT c.content FROM tx_chunk c JOIN tx_session s ON s.id = c.session_id " +
                          "WHERE s.host_session_id = $id ORDER BY c.exchange_start, c.part";
        cmd.Parameters.AddWithValue("$id", _placed[fixture].Id);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    private string? Col(string column, string fixture) =>
        Db.Text($"SELECT CAST({column} AS TEXT) FROM tx_session WHERE host_session_id = '{_placed[fixture].Id}'");

    /// <summary>The block (blank-line separated) of a chunk text that holds <paramref name="marker"/>.</summary>
    private static string BlockWith(IEnumerable<string> contents, string marker)
    {
        foreach (var content in contents)
        {
            // Skip the header line; blocks are separated by one blank line.
            foreach (var block in content.Split("\n\n").Skip(1))
                if (block.Contains(marker, StringComparison.Ordinal)) return block;
        }
        Assert.Fail("no block holds " + marker);
        return "";
    }

    // ------------------------------------------------------------------
    // Sessions and chunks per project and kind
    // ------------------------------------------------------------------

    [Fact]
    public void Backfill_Corpus_SessionsAndChunksPerProjectAndKind()
    {
        var summary = PlaceAndRun();

        Assert.Equal(10, summary.Seen);
        Assert.Equal(9, summary.New);
        Assert.Equal(1, summary.Empty);      // no-kept
        Assert.Equal(0, summary.Failed + summary.Skipped + summary.Excluded + summary.Changed + summary.Retry);
        Assert.Equal(Db.Scalar("SELECT count(*) FROM tx_chunk"), summary.ChunksNew);

        var status = Db.Store.Status();
        Assert.Equal(9, status.Sessions);
        Assert.Equal(summary.ChunksNew, status.Chunks);
        var expected = new[]
        {
            ("decision", "interactive"), ("compaction", "interactive"), ("error", "interactive"),
            ("secrets", "interactive"), ("straddle", "interactive"), ("owner/repo", "interactive"),
            ("automated-sdk", "automated"), ("automated-opening", "automated"),
        }.OrderBy(p => p).ToArray();
        Assert.Equal(expected, status.ByProjectAndKind.Select(r => (r.ProjectKey, r.Kind)).OrderBy(p => p).ToArray());
        Assert.All(status.ByProjectAndKind, r =>
        {
            Assert.True(r.Sessions > 0);
            Assert.True(r.Chunks > 0);
        });
        Assert.Equal(2, status.ByProjectAndKind.Single(r => r.ProjectKey == "owner/repo").Sessions);
        Assert.Equal(status.Sessions, status.ByProjectAndKind.Sum(r => r.Sessions));
        Assert.Equal(status.Chunks, status.ByProjectAndKind.Sum(r => r.Chunks));
        Assert.Equal(7, status.SessionsByKind["interactive"]);
        Assert.Equal(2, status.SessionsByKind["automated"]);
        Assert.Equal(0, Db.Scalar("SELECT count(*) FROM tx_session WHERE host_session_id = '" + _placed["no-kept"].Id + "'"));
    }

    [Fact]
    public void Corpus_SecondRun_ChangesNothing()
    {
        var first = PlaceAndRun();
        Db.Exec("UPDATE tx_chunk SET embedded = 1");
        var before = ArchiveSnapshot.Read(Db.Path, includeIds: true);
        Assert.NotEmpty(before.Chunks);
        var stored = Db.Scalar("SELECT count(*) FROM tx_session");

        var second = Run();

        Assert.Equal(stored, second.Unchanged);
        Assert.Equal(1, second.Empty);       // no-kept is read again, cheaply
        Assert.Equal(0, second.New + second.Updated + second.Changed + second.Failed + second.Retry);
        Assert.Equal(0, second.ChunksNew + second.ChunksChanged + second.ChunksRemoved + second.ChunksUnchanged);
        Assert.Equal(first.Seen, second.Seen);
        IngestHarness.AssertSame(before, ArchiveSnapshot.Read(Db.Path, includeIds: true), "second run");
        Assert.Equal(Db.Scalar("SELECT count(*) FROM tx_chunk"), Db.Scalar("SELECT count(*) FROM tx_chunk WHERE embedded = 1"));
    }

    // ------------------------------------------------------------------
    // Kept and dropped
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("zqxtoolresult")]
    [InlineData("zqxthinking")]
    [InlineData("zqxreminder")]
    [InlineData("zqxmeta")]
    [InlineData("zqxsidechain")]
    [InlineData("zqxnotification")]
    [InlineData("zqxeditinput")]
    [InlineData("zqxnokept")]
    [InlineData("zqxqueueop")]
    public void Corpus_DroppedContent_NotStored(string marker)
    {
        PlaceAndRun();
        Assert.NotEmpty(Contents());

        Assert.DoesNotContain(marker, AllText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Corpus_KeptContent_Stored()
    {
        PlaceAndRun();
        var decision = ContentsOf("decision");
        var all = string.Join("\n\n", decision);

        Assert.Contains("zqxqueued", all);
        Assert.Contains("/develop 148", all);
        Assert.Contains("[Bash: ls -l]", all);
        Assert.Contains("[AskUserQuestion: ", all);
        Assert.Contains("[mcp__fixture__lookup: zqxunknowninput]", all);   // an unknown tool: its first string field
        Assert.Contains("[AskUserQuestion: zqxquestion", all);
        Assert.StartsWith("Assistant:", BlockWith(decision, "zqxplan"));
        Assert.StartsWith("Assistant:", BlockWith(ContentsOf("automated-sdk"), "zqxverdict"));
    }

    [Fact]
    public void Corpus_Error_StoredCutTo500()
    {
        PlaceAndRun();
        var error = ContentsOf("error");

        Assert.Contains(error, c => c.Contains("Parse error: no such table: zqx_schema_version"));
        Assert.Equal(1, Db.Scalar("SELECT count(*) FROM tx_chunk c JOIN tx_session s ON s.id = c.session_id " +
                                  $"WHERE s.host_session_id = '{_placed["error"].Id}' AND c.has_error = 1"));
        var block = BlockWith(error, "Parse error: no such table");
        Assert.StartsWith("[error: ", block);
        Assert.EndsWith("...]", block);
        var text = block["[error: ".Length..^1];
        Assert.Equal(503, text.Length);
        Assert.DoesNotContain("zqxerrortail", string.Join("\n", error));
    }

    [Fact]
    public void Corpus_CompactionSummary_OwnChunkType()
    {
        PlaceAndRun();
        var id = _placed["compaction"].Id;

        var summaries = new List<string>();
        using (var conn = Db.Connect())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT c.content FROM tx_chunk c JOIN tx_session s ON s.id = c.session_id " +
                              $"WHERE s.host_session_id = '{id}' AND c.chunk_type = 'compaction-summary'";
            using var r = cmd.ExecuteReader();
            while (r.Read()) summaries.Add(r.GetString(0));
        }

        var summary = Assert.Single(summaries);
        Assert.Contains("zqxsummary", summary);
        Assert.Contains("| compaction-summary |", summary);
        Assert.DoesNotContain("User:", summary);
        Assert.Equal(0, Db.Scalar("SELECT count(*) FROM tx_chunk WHERE chunk_type = 'dialogue' AND content LIKE '%zqxsummary%'"));
        Assert.True(Db.Scalar("SELECT count(*) FROM tx_chunk c JOIN tx_session s ON s.id = c.session_id " +
                              $"WHERE s.host_session_id = '{id}' AND c.chunk_type = 'dialogue'") > 0);
    }

    // ------------------------------------------------------------------
    // Secrets
    // ------------------------------------------------------------------

    private static readonly (string Name, string Marker)[] Planted =
    [
        ("github-token", "[redacted:token]"), ("github-pat", "[redacted:token]"), ("gitlab-token", "[redacted:token]"),
        ("slack-token", "[redacted:token]"), ("openai-key", "[redacted:token]"), ("anthropic-key", "[redacted:token]"),
        ("openai-project-key", "[redacted:token]"), ("openai-svcacct-key", "[redacted:token]"),
        ("openai-admin-key", "[redacted:token]"), ("aws-key", "[redacted:token]"),
        ("private-key", "[redacted:private-key]"), ("private-key-unterminated", "[redacted:private-key]"),
        ("bearer-token", "[redacted:bearer]"), ("url-password", "[redacted:url-credentials]"),
        ("api-key-value", "[redacted:secret-assignment]"),
    ];

    private void AssertSecretsAbsentAndMarked(string dbPath)
    {
        foreach (var (name, marker) in Planted)
        {
            var secret = Home.Secrets.Get(name);
            Assert.True(secret.Length >= 12, name);
            LeakScan.AssertNotStored(dbPath, secret);
            LeakScan.AssertStored(dbPath, marker);
        }
    }

    [Fact]
    public void Corpus_PlantedSecrets_OnlyMarkersStored()
    {
        PlaceAndRun();

        // The store is open: the write-ahead log and the shared-memory file exist and are scanned as well.
        Assert.True(File.Exists(Db.Path + "-wal"), "the -wal file should exist while the store is open");
        Assert.True(File.Exists(Db.Path + "-shm"), "the -shm file should exist while the store is open");
        AssertSecretsAbsentAndMarked(Db.Path);

        // Straddlers: a prefix of the token at the 160-character cut of the stub and the 500-character cut of the error.
        var secrets = ContentsOf("secrets");
        var stub = BlockWith(secrets, "[Bash: xxx");
        Assert.DoesNotContain("[redacted", stub);
        Assert.EndsWith("...]", stub);
        var error = BlockWith(secrets, "[error: xxx");
        Assert.DoesNotContain("[redacted", error);
        Assert.EndsWith("...]", error);

        // The title is derived from redacted text.
        Assert.Contains("[redacted:token]", Col("title", "secrets"));

        // References and placeholders are not redacted.
        LeakScan.AssertStored(Db.Path, "GH_TOKEN=\"$t\" gh api");
        LeakScan.AssertStored(Db.Path, "API_KEY=<your-key>");

        // The same assertions over the files of a closed store (checkpointed, WAL gone or empty).
        Db.Store.Dispose();
        SqliteConnection.ClearAllPools();
        AssertSecretsAbsentAndMarked(Db.Path);
        LeakScan.AssertStored(Db.Path, "GH_TOKEN=\"$t\" gh api");
    }

    // ------------------------------------------------------------------
    // Source files that go away, forgotten sessions and cutoffs
    // ------------------------------------------------------------------

    [Fact]
    public void Corpus_DeletedSource_KeepsChunks()
    {
        PlaceAndRun();
        var before = ArchiveSnapshot.Read(Db.Path, includeIds: true);
        foreach (var s in _placed.Values) Home.Delete(s);

        var summary = Run();

        Assert.Equal(0, summary.Seen);
        Assert.Equal(0, summary.New + summary.Updated + summary.Changed + summary.Failed);
        Assert.Equal(9, Db.Scalar("SELECT count(*) FROM tx_session"));
        IngestHarness.AssertSame(before, ArchiveSnapshot.Read(Db.Path, includeIds: true), "after deleting every source");
    }

    [Fact]
    public void Corpus_ForgottenSessionAndProject_StayOut()
    {
        PlaceCorpus();
        Db.Store.ForgetSession(Host, _placed["decision"].Id);
        Db.Store.ForgetProject("automated-sdk");

        var summary = Run();

        Assert.Equal(2, summary.Excluded);
        Assert.Equal(7, summary.New);
        Assert.Equal(7, Db.Scalar("SELECT count(*) FROM tx_session"));
        Assert.Equal(0, Db.Scalar($"SELECT count(*) FROM tx_session WHERE host_session_id = '{_placed["decision"].Id}'"));
        Assert.Equal(0, Db.Scalar($"SELECT count(*) FROM tx_session WHERE host_session_id = '{_placed["automated-sdk"].Id}'"));
        Assert.True(File.Exists(_placed["decision"].Path));
        Assert.True(File.Exists(_placed["automated-sdk"].Path));
        Assert.DoesNotContain("zqxqueued", AllText());
        Assert.DoesNotContain("zqxverdict", AllText());
    }

    private PlacedSession PlaceStraddleAfterCutoff()
    {
        var s = Home.Place("straddle", Path.Combine(Home.Root, "work", "straddle"), "straddle-1");
        Home.SetMtime(s, Home.BaseTime.AddDays(1));
        Db.Store.ForgetAll(Home.BaseTime.AddSeconds(50));
        return s;
    }

    [Fact]
    public void Corpus_Straddle_OnlyAfterCutoff()
    {
        PlaceStraddleAfterCutoff();

        var summary = Run();

        Assert.Equal(1, summary.New);
        var all = string.Join("\n", Contents());
        foreach (var kept in new[] { "zqxstraddle4", "zqxstraddle5", "zqxstraddle6" }) Assert.Contains(kept, all);
        foreach (var hidden in new[] { "zqxstraddle1", "zqxstraddle2", "zqxstraddle3" }) Assert.DoesNotContain(hidden, all);
    }

    [Fact]
    public void Corpus_UnforgetCutoff_HiddenRecordsStored()
    {
        PlaceStraddleAfterCutoff();
        Assert.Equal(1, Run().New);
        Assert.DoesNotContain("zqxstraddle1", string.Join("\n", Contents()));

        Assert.True(Db.Store.ClearCutoff());
        var summary = Run();

        Assert.Equal(1, summary.Updated);
        Assert.Equal(0, summary.Unchanged);
        var all = string.Join("\n", Contents());
        foreach (var marker in new[] { "zqxstraddle1", "zqxstraddle2", "zqxstraddle3", "zqxstraddle4", "zqxstraddle5", "zqxstraddle6" })
            Assert.Contains(marker, all);
    }

    [Fact]
    public void Corpus_ReplacedOrShortened_Changed_Kept()
    {
        PlaceCorpus();
        Assert.Equal(9, Run().New);
        var before = ArchiveSnapshot.Read(Db.Path, includeIds: true);

        Home.Replace(_placed["decision"], "compaction");
        var bytes = File.ReadAllBytes(_placed["secrets"].Path);
        Home.Write(_placed["secrets"], bytes.AsSpan(0, bytes.Length / 2).ToArray());

        var summary = Run();

        Assert.Equal(2, summary.Changed);
        Assert.Equal(
            new[] { Host + ":" + _placed["decision"].Id, Host + ":" + _placed["secrets"].Id }.OrderBy(x => x, StringComparer.Ordinal),
            summary.ChangedSessions.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(0, summary.New + summary.Updated + summary.Failed);
        Assert.Equal(0, summary.ChunksNew + summary.ChunksChanged + summary.ChunksRemoved);
        IngestHarness.AssertSame(before, ArchiveSnapshot.Read(Db.Path, includeIds: true), "after replacing and shortening sources");
    }

    // ------------------------------------------------------------------
    // Worktrees and repositories
    // ------------------------------------------------------------------

    [Fact]
    public void Corpus_Worktree_MissingDirKeyFromRepo_AndRepoWithRemote()
    {
        PlaceAndRun();
        var gone = Path.Combine(_repo, ".claude", "worktrees", "gone");
        Assert.False(Directory.Exists(gone));

        Assert.Equal("owner/repo", Col("project_key", "worktree-missing"));
        Assert.Equal("remote", Col("project_key_source", "worktree-missing"));
        Assert.Equal(_repo, Col("project_root", "worktree-missing"));
        Assert.Equal(gone, Col("cwd", "worktree-missing"));

        Assert.Equal("owner/repo", Col("project_key", "worktree-repo"));
        Assert.Equal("remote", Col("project_key_source", "worktree-repo"));
        Assert.Equal(_repo, Col("project_root", "worktree-repo"));
        Assert.Contains(ContentsOf("worktree-repo"), c => c.Contains("| project owner/repo |"));
    }
}
