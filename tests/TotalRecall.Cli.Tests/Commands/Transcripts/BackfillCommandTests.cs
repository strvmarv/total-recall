using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using TotalRecall.Cli.Commands.Transcripts;
using TotalRecall.Infrastructure.Transcripts;
using TotalRecall.TestSupport.Transcripts;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands.Transcripts;

[Collection("ConsoleCapture")]
public sealed class BackfillCommandTests : IDisposable
{
    private readonly TranscriptsHarness _h = new();
    private readonly ClaudeFixtureHome _home = new();
    private readonly List<string> _factoryCalls = [];

    public BackfillCommandTests() => _h.Interactive = false; // backfill needs no terminal

    public void Dispose()
    {
        _h.Dispose();
        _home.Dispose();
    }

    private string Cwd => Path.Combine(_home.Root, "work", "proj"); // never created: key "proj"

    private string Output => _h.Out.ToString() + _h.Err.ToString();

    private void EnableArchive()
    {
        _h.WriteConfig("[transcripts]\nenabled = true\n");
        _h.CreateArchive();
    }

    /// <summary>A factory that records its calls and hands back <paramref name="adapter"/> (default: the real adapter over the fixture tree).</summary>
    private Func<string, ITranscriptAdapter> Factory(ITranscriptAdapter? adapter = null) => host =>
    {
        _factoryCalls.Add(host);
        return adapter ?? new ClaudeCodeAdapter(_home.ProjectsDir);
    };

    private Func<string, ITranscriptAdapter> ThrowingFactory() => Factory(new ThrowingAdapter());

    private int Backfill(Func<string, ITranscriptAdapter> factory, CancellationToken ct = default, params string[] args) =>
        _h.Run(h => h.Backfill(factory, ct), args);

    private int Backfill(params string[] args) => Backfill(Factory(), default, args);

    private int Status() => _h.Run(h => h.Status());

    private static int Number(string output, string label)
    {
        var m = Regex.Match(output, Regex.Escape(label) + @" (\d+)");
        Assert.True(m.Success, $"'{label} <n>' not found in: {output}");
        return int.Parse(m.Groups[1].Value);
    }

    // ---- the order of operations -----------------------------------------------------------------------------------

    [Fact]
    public void ArchiveOff_RefusesExit1_FactoryNeverCalled_NothingRead()
    {
        _h.CreateArchive();
        _home.Place("decision", Cwd);

        var code = Backfill(ThrowingFactory());

        Assert.Equal(1, code);
        Assert.Empty(_factoryCalls);
        Assert.Contains(
            "transcripts backfill: the transcript archive is off; run 'total-recall transcripts enable' in a terminal to turn it on",
            _h.Err.ToString());
        Assert.Equal(0, _h.Scalar("SELECT count(*) FROM tx_session"));
    }

    [Fact]
    public void Postgres_RefusesExit1_FactoryNeverCalled()
    {
        _h.WriteConfig("[transcripts]\nenabled = true\n[storage]\nmode = \"postgres\"\nconnection_string = \"Host=h\"\n");

        var code = Backfill(ThrowingFactory());

        Assert.Equal(1, code);
        Assert.Empty(_factoryCalls);
        Assert.Contains("postgres", _h.Err.ToString());
    }

    [Fact]
    public void TablesAbsent_SaysEnableCreatesThem_Exit1_FactoryNeverCalled()
    {
        _h.WriteConfig("[transcripts]\nenabled = true\n");

        var code = Backfill(ThrowingFactory());

        Assert.Equal(1, code);
        Assert.Empty(_factoryCalls);
        Assert.Contains(
            $"transcripts backfill: the archive tables do not exist at main database ({_h.DbPath}); " +
            "'total-recall transcripts enable' creates them",
            _h.Err.ToString());
        Assert.False(File.Exists(_h.DbPath));
    }

    [Fact]
    public void MalformedConfig_Exit1_FactoryNeverCalled()
    {
        var archive = _h.CreateSeparateArchive();
        _h.WriteMalformedConfig(archive);

        var code = Backfill(ThrowingFactory());

        Assert.Equal(1, code);
        Assert.Empty(_factoryCalls);
        Assert.Contains("could not read the configuration", _h.Err.ToString());
        Assert.Equal((1L, 1L), _h.ArchiveCounts(archive));
    }

    // ---- arguments -------------------------------------------------------------------------------------------------

    [Fact]
    public void UnknownHost_Exit2()
    {
        EnableArchive();

        var code = Backfill(ThrowingFactory(), default, "--host", "pi");

        Assert.Equal(2, code);
        Assert.Empty(_factoryCalls);
        Assert.Contains("Usage: total-recall transcripts backfill [--host claude-code]", _h.Err.ToString());
    }

    [Fact]
    public void HostWithoutValue_Exit2()
    {
        EnableArchive();

        Assert.Equal(2, Backfill(ThrowingFactory(), default, "--host"));
        Assert.Empty(_factoryCalls);
    }

    [Fact]
    public void RepeatedHost_Exit2()
    {
        EnableArchive();

        var code = Backfill(ThrowingFactory(), default, "--host", "claude-code", "--host", "claude-code");

        Assert.Equal(2, code);
        Assert.Empty(_factoryCalls);
        Assert.Contains("--host is given once", _h.Err.ToString());
    }

    [Fact]
    public void UnreadableArchive_SaysCouldNotRead_Exit1_FactoryNeverCalled()
    {
        _h.WriteConfig("[transcripts]\nenabled = true\n");
        Directory.CreateDirectory(_h.DataDir);
        File.WriteAllText(_h.DbPath, "this is not a database, it only has the name of one. ".PadRight(4096, 'x'));

        var code = Backfill(ThrowingFactory());

        Assert.Equal(1, code);
        Assert.Empty(_factoryCalls);
        Assert.Contains($"transcripts backfill: could not read the archive at main database ({_h.DbPath}): ", _h.Err.ToString());
    }

    [Fact]
    public void CancelKey_FirstPressStopsTheRun_SecondPressEndsTheProcess()
    {
        using var cts = new CancellationTokenSource();

        Assert.True(BackfillCommand.KeepProcessOnCancelKey(cts));    // e.Cancel = true: the process stays
        Assert.True(cts.IsCancellationRequested);
        Assert.False(BackfillCommand.KeepProcessOnCancelKey(cts));   // a second press: the process ends as usual

        var disposed = new CancellationTokenSource();
        disposed.Dispose();
        Assert.True(BackfillCommand.KeepProcessOnCancelKey(disposed));
    }

    [Fact]
    public void UnexpectedArgument_Exit2()
    {
        EnableArchive();

        Assert.Equal(2, Backfill(ThrowingFactory(), default, "everything"));
        Assert.Equal(2, Backfill(ThrowingFactory(), default, "--all"));
        Assert.Empty(_factoryCalls);
    }

    [Fact]
    public void NeedsNoTerminal()
    {
        EnableArchive();
        _h.Interactive = false;

        var code = Backfill("--host", "claude-code");

        Assert.Equal(0, code);
        Assert.Empty(_h.Prompts);
        Assert.Equal(["claude-code"], _factoryCalls);
    }

    // ---- running ---------------------------------------------------------------------------------------------------

    [Fact]
    public void NoProjectsDirectory_SaysSo_Exit0()
    {
        EnableArchive();
        var missing = Path.Combine(_home.Root, "no-such-dir");

        var code = Backfill(Factory(new ClaudeCodeAdapter(missing)));

        Assert.Equal(0, code);
        Assert.Contains($"no Claude Code sessions found at {missing}", _h.Out.ToString());
    }

    [Fact]
    public void Backfill_Corpus_StoresAndPrintsSummary_Exit0()
    {
        EnableArchive();
        _home.Place("decision", Cwd);
        _home.Place("compaction", Cwd);
        _home.Place("error", Cwd);
        _home.Place("automated-sdk", Cwd);
        _home.Place("no-kept", Cwd);

        var code = Backfill();

        Assert.Equal(0, code);
        var output = _h.Out.ToString();
        Assert.Contains("backfill claude-code: 5 sessions seen", output);
        Assert.Contains("  new 4, updated 0, unchanged 0, excluded 0, empty 1, skipped 0, changed 0, retry 0, failed 0", output);
        var chunks = _h.Scalar("SELECT count(*) FROM tx_chunk");
        Assert.True(chunks > 0);
        Assert.Contains($"  chunks: new {chunks}, changed 0, unchanged 0, removed 0", output);
        Assert.DoesNotContain("unreadable lines", output);
        Assert.DoesNotContain("changed (kept as stored)", output);
        Assert.DoesNotContain("interrupted", output);

        Assert.Equal(0, Status());
        var status = _h.Out.ToString();
        Assert.Contains("by project and kind:", status);
        Assert.Matches(@"(?m)^  proj interactive: 3 sessions, \d+ chunks$", status);
        Assert.Matches(@"(?m)^  proj automated: 1 sessions, \d+ chunks$", status);
        Assert.Contains("search: not available in this version yet", status);
    }

    [Fact]
    public void SecondRun_AllUnchanged()
    {
        EnableArchive();
        _home.Place("decision", Cwd);
        _home.Place("compaction", Cwd);
        Assert.Equal(0, Backfill());
        Assert.True(_h.Scalar("SELECT count(*) FROM tx_chunk") > 0);

        var code = Backfill();

        Assert.Equal(0, code);
        var output = _h.Out.ToString();
        Assert.Contains("backfill claude-code: 2 sessions seen", output);
        Assert.Contains("  new 0, updated 0, unchanged 2, excluded 0, empty 0, skipped 0, changed 0, retry 0, failed 0", output);
        Assert.Contains("  chunks: new 0, changed 0, unchanged 0, removed 0", output);
    }

    [Fact]
    public void Version1Archive_IsMigratedTo3()
    {
        EnableArchive();
        _home.Place("decision", Cwd);
        Assert.Equal(0, Backfill());
        Assert.Equal(3, _h.Scalar("SELECT MAX(version) FROM tx_schema_version"));

        // TranscriptMigrationRunner is internal to Infrastructure, so an old archive is rebuilt by undoing
        // migrations 3 and 2 by hand (migration 3 first).
        using (var conn = _h.Connect())
        {
            foreach (var sql in new[]
                     {
                         "DROP TRIGGER tx_chunk_vec_ad",
                         "DROP TRIGGER tx_chunk_vec_au",
                         "DROP TRIGGER tx_session_vec_ku",
                         "DROP TRIGGER tx_chunk_reset_au",
                         "DROP TRIGGER tx_chunk_fts_au",
                         "CREATE TRIGGER tx_chunk_fts_au AFTER UPDATE ON tx_chunk BEGIN " +
                         "INSERT INTO tx_chunk_fts(tx_chunk_fts, rowid, content) VALUES('delete', old.id, old.content); " +
                         "INSERT INTO tx_chunk_fts(rowid, content) VALUES (new.id, new.content); END",
                         "DROP INDEX idx_tx_chunk_unembedded",
                         "DROP TABLE tx_chunk_vec",
                         "ALTER TABLE tx_chunk DROP COLUMN embed_attempts",
                         "DELETE FROM tx_schema_version WHERE version = 3",
                         "DROP INDEX idx_tx_session_project_kind",
                         "ALTER TABLE tx_session DROP COLUMN settled_user_turns",
                         "ALTER TABLE tx_session DROP COLUMN built_under_cutoff",
                         "ALTER TABLE tx_session DROP COLUMN project_root",
                         "DELETE FROM tx_schema_version WHERE version = 2",
                     })
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Assert.Equal(1, _h.Scalar("SELECT MAX(version) FROM tx_schema_version"));

        var code = Backfill();

        Assert.Equal(0, code);
        Assert.Equal(3, _h.Scalar("SELECT MAX(version) FROM tx_schema_version"));
        Assert.Equal(0, Status());
        Assert.Contains("schema version: 3", _h.Out.ToString());
    }

    [Fact]
    public void OneFailingFile_CountedFailed_OthersStored_Exit1()
    {
        EnableArchive();
        var bad = _home.Place("decision", Cwd);
        _home.Place("compaction", Cwd);
        _home.Place("error", Cwd);
        var adapter = new HookAdapter(new ClaudeCodeAdapter(_home.ProjectsDir),
            s => { if (s.HostSessionId == bad.Id) throw new IOException("disk error"); }, everyTime: true);

        var original = Console.Error;
        int code;
        try
        {
            Console.SetError(new StringWriter());
            code = Backfill(Factory(adapter));
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Equal(1, code);
        var output = _h.Out.ToString();
        Assert.Contains("backfill claude-code: 3 sessions seen", output);
        Assert.Contains("new 2, updated 0, unchanged 0, excluded 0, empty 0, skipped 0, changed 0, retry 0, failed 1", output);
        Assert.Equal(2, _h.Scalar("SELECT count(*) FROM tx_session"));
    }

    [Fact]
    public void ReplacedFile_ListedAsChanged_ChunksKept()
    {
        EnableArchive();
        var session = _home.Place("decision", Cwd);
        Assert.Equal(0, Backfill());
        var chunks = _h.Scalar("SELECT count(*) FROM tx_chunk");
        Assert.True(chunks > 0);

        _home.Replace(session, "error");
        var code = Backfill();

        Assert.Equal(0, code);
        var output = _h.Out.ToString();
        Assert.Contains("changed 1,", output);
        Assert.Contains($"  changed (kept as stored): claude-code:{session.Id}", output);
        Assert.Equal(chunks, _h.Scalar("SELECT count(*) FROM tx_chunk"));
    }

    [Fact]
    public void Cancelled_PrintsSummarySoFar()
    {
        EnableArchive();
        _home.Place("decision", Cwd);
        _home.Place("compaction", Cwd);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var code = Backfill(Factory(), cts.Token);

        Assert.Equal(0, code);
        var output = _h.Out.ToString();
        Assert.Contains("backfill claude-code: 2 sessions seen", output);
        Assert.Contains("  interrupted: the remaining sessions were not processed", output);
        Assert.Equal(0, _h.Scalar("SELECT count(*) FROM tx_session"));
    }

    [Fact]
    public void Progress_Every50Sessions()
    {
        EnableArchive();
        for (var i = 0; i < 55; i++)
            _home.Place("no-kept", Cwd, $"progress-{i:D2}");

        var code = Backfill();

        Assert.Equal(0, code);
        var lines = _h.Out.ToString().Split('\n');
        var progress = Array.FindAll(lines, l => Regex.IsMatch(l, @"^\s+\d+/\d+ sessions\s*$"));
        Assert.Single(progress);
        Assert.Equal("  50/55 sessions", progress[0].TrimEnd());
        Assert.Contains("backfill claude-code: 55 sessions seen", _h.Out.ToString());
    }

    [Fact]
    public void TheCommand_IsInTheTranscriptsGroup_AndVisible()
    {
        var command = new BackfillCommand();
        Assert.Equal("backfill", command.Name);
        Assert.Equal("transcripts", command.Group);
        Assert.False(command.Hidden);
    }

    // ---- doubles ---------------------------------------------------------------------------------------------------

    /// <summary>Every member throws: proves the host directory is never touched.</summary>
    private sealed class ThrowingAdapter : ITranscriptAdapter
    {
        private static Exception Boom() => new InvalidOperationException("the adapter must not be used");

        public string Host => throw Boom();
        public string Root => throw Boom();
        public bool Detect() => throw Boom();
        public SessionEnumeration EnumerateSessions() => throw Boom();
        public SessionHead ReadHead(SessionSource source) => throw Boom();
        public bool IsLineStart(SessionSource source, long offset) => throw Boom();
        public IEnumerable<TranscriptRecord> ReadRecords(SessionSource source, long startOffset, ReadStats stats, CancellationToken ct) =>
            throw Boom();
    }
}
