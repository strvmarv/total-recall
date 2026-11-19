using System;
using System.IO;
using System.Linq;
using TotalRecall.Cli.Commands.Transcripts;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands.Transcripts;

[Collection("ConsoleCapture")]
public sealed class ForgetCommandTests : IDisposable
{
    private readonly TranscriptsHarness _h = new();

    public void Dispose() => _h.Dispose();

    private int Forget(params string[] args) => _h.Run(h => h.Forget(), args);

    private void Seed()
    {
        _h.CreateArchive();
        var a = _h.AddSession("claude-code", "s1", projectKey: "owner/one", cwd: "/work/one");
        var b = _h.AddSession("claude-code", "s2", projectKey: "owner/one", cwd: "/work/one");
        var c = _h.AddSession("pi", "s3", projectKey: "owner/two");
        _h.AddChunk(a, "alpha");
        _h.AddChunk(a, "beta", exchangeStart: 1);
        _h.AddChunk(b, "gamma");
        _h.AddChunk(c, "delta");
    }

    [Theory]
    [InlineData]
    [InlineData("--all", "--project", "owner/one")]
    [InlineData("--session", "pi:s3", "--all")]
    [InlineData("--project", "a", "--project", "b")]
    [InlineData("--session")]
    [InlineData("--project")]
    [InlineData("--session", "nocolon")]
    [InlineData("--session", ":id")]
    [InlineData("--session", "host:")]
    [InlineData("--project", "")]
    [InlineData("--project", "  ")]
    [InlineData("--everything")]
    [InlineData("stray")]
    public void SelectorErrors_ExitTwo_AndChangeNothing(params string[] args)
    {
        Seed();

        var code = Forget(args);

        Assert.Equal(2, code);
        Assert.Contains("Usage: total-recall transcripts forget", _h.Err.ToString());
        Assert.Empty(_h.Prompts);
        Assert.Equal(3, _h.Scalar("SELECT count(*) FROM tx_session"));
    }

    [Fact]
    public void WithoutATerminal_ExitsTwo_AndChangesNothing()
    {
        Seed();
        _h.Interactive = false;

        var code = Forget("--all");

        Assert.Equal(2, code);
        Assert.Contains("must be run in a terminal", _h.Err.ToString());
        Assert.Empty(_h.Prompts);
        Assert.Equal(3, _h.Scalar("SELECT count(*) FROM tx_session"));
    }

    [Fact]
    public void ASessionSelector_SplitsAtTheFirstColon()
    {
        _h.CreateArchive();
        var id = _h.AddSession("claude-code", "a:b");
        _h.AddChunk(id, "x");

        var code = Forget("--session", "claude-code:a:b");

        Assert.Equal(0, code);
        Assert.Equal(0, _h.Scalar("SELECT count(*) FROM tx_session"));
        using var store = _h.OpenArchive();
        var forgotten = Assert.Single(store.Status().ForgottenSessions);
        Assert.Equal("claude-code", forgotten.Host);
        Assert.Equal("a:b", forgotten.HostSessionId);
    }

    [Fact]
    public void ShowsTheCountsBeforeItAsks_AndADeclineChangesNothing()
    {
        Seed();
        _h.Answer = false;

        var code = Forget("--project", "owner/one");

        Assert.Equal(0, code);
        var shown = Assert.Single(_h.OutputAtPrompt);
        Assert.Contains("2 sessions", shown);
        Assert.Contains("3 chunks", shown);
        Assert.Equal(3, _h.Scalar("SELECT count(*) FROM tx_session"));
        Assert.Equal(0, _h.Scalar("SELECT count(*) FROM tx_forgotten"));
    }

    [Fact]
    public void ForgetsAProject_AndPrintsTheCounts()
    {
        Seed();

        var code = Forget("--project", "owner/one");

        Assert.Equal(0, code);
        Assert.Equal(1, _h.Scalar("SELECT count(*) FROM tx_session"));
        Assert.Equal(1, _h.Scalar("SELECT count(*) FROM tx_chunk"));
        Assert.Equal(1, _h.Scalar("SELECT count(*) FROM tx_forgotten WHERE kind = 'project' AND project_key = 'owner/one'"));
        var output = _h.Out.ToString();
        Assert.Contains("2 sessions", output.Split('\n').Last(l => l.Length > 0));
    }

    [Fact]
    public void ForgetsEverything_AndSetsTheCutoff()
    {
        Seed();

        var code = Forget("--all");

        Assert.Equal(0, code);
        Assert.Equal(0, _h.Scalar("SELECT count(*) FROM tx_session"));
        Assert.Equal(0, _h.Scalar("SELECT count(*) FROM tx_chunk"));
        Assert.Equal(1, _h.Scalar("SELECT count(*) FROM tx_state WHERE key = 'ingest_not_before'"));
        Assert.Contains("3 sessions", _h.OutputAtPrompt.Single());
        Assert.Contains("4 chunks", _h.OutputAtPrompt.Single());
    }

    [Fact]
    public void WhenTheLogCannotBeTruncated_PrintsTheWarning_AndExitsOne()
    {
        Seed();
        using var reader = _h.Connect();
        using var tx = reader.BeginTransaction(deferred: true);
        using (var cmd = reader.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT count(*) FROM tx_chunk";
            cmd.ExecuteScalar(); // a read snapshot that blocks the truncating checkpoint
        }

        var code = Forget("--session", "pi:s3");

        Assert.Equal(1, code);
        Assert.Contains("write-ahead log", _h.Err.ToString());
        tx.Rollback();
        Assert.Equal(2, _h.Scalar("SELECT count(*) FROM tx_session"));
    }

    [Fact]
    public void WithNoArchiveTables_SaysNothingIsStored_AndCreatesNothing()
    {
        var code = Forget("--all");

        Assert.Equal(0, code);
        Assert.Contains("nothing is stored", _h.Out.ToString());
        Assert.Empty(_h.Prompts);
        Assert.False(File.Exists(_h.DbPath));
        Assert.False(Directory.Exists(_h.DataDir));
    }

    [Fact]
    public void TheCommand_IsInTheTranscriptsGroup_AndVisible()
    {
        var command = new ForgetCommand();
        Assert.Equal("forget", command.Name);
        Assert.Equal("transcripts", command.Group);
        Assert.False(command.Hidden);
    }
}
