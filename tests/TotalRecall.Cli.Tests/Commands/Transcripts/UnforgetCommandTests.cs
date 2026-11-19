using System;
using System.IO;
using TotalRecall.Cli.Commands.Transcripts;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands.Transcripts;

[Collection("ConsoleCapture")]
public sealed class UnforgetCommandTests : IDisposable
{
    private readonly TranscriptsHarness _h = new();

    public void Dispose() => _h.Dispose();

    private int Unforget(params string[] args) => _h.Run(h => h.Unforget(), args);

    private void SeedRecords()
    {
        _h.CreateArchive();
        using (var store = _h.OpenArchive())
        {
            store.ForgetSession("claude-code", "s1");
            store.ForgetProject("owner/one");
            store.ForgetAll(DateTimeOffset.UtcNow);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Theory]
    [InlineData]
    [InlineData("--all")]
    [InlineData("--cutoff", "--project", "owner/one")]
    [InlineData("--session", "nocolon")]
    [InlineData("--session", ":s1")]
    [InlineData("--session", "claude-code:")]
    [InlineData("--project")]
    [InlineData("--project", "")]
    public void SelectorErrors_ExitTwo(params string[] args)
    {
        SeedRecords();

        var code = Unforget(args);

        Assert.Equal(2, code);
        Assert.Contains("Usage: total-recall transcripts unforget", _h.Err.ToString());
        Assert.Empty(_h.Prompts);
        Assert.Equal(2, _h.Scalar("SELECT count(*) FROM tx_forgotten"));
    }

    [Fact]
    public void WithoutATerminal_ExitsTwo_AndChangesNothing()
    {
        SeedRecords();
        _h.Interactive = false;

        var code = Unforget("--cutoff");

        Assert.Equal(2, code);
        Assert.Contains("must be run in a terminal", _h.Err.ToString());
        Assert.Equal(1, _h.Scalar("SELECT count(*) FROM tx_state"));
    }

    [Fact]
    public void RemovesASessionRecord_AndSaysSo_ThenSaysThereWasNone()
    {
        SeedRecords();

        Assert.Equal(0, Unforget("--session", "claude-code:s1"));
        Assert.Single(_h.Prompts);
        Assert.Contains("removed", _h.Out.ToString());
        Assert.Equal(0, _h.Scalar("SELECT count(*) FROM tx_forgotten WHERE kind = 'session'"));

        Assert.Equal(0, Unforget("--session", "claude-code:s1"));
        Assert.Contains("no exclusion record", _h.Out.ToString());
    }

    [Fact]
    public void RemovesAProjectRecord()
    {
        SeedRecords();

        Assert.Equal(0, Unforget("--project", "owner/one"));
        Assert.Contains("removed", _h.Out.ToString());
        Assert.Equal(0, _h.Scalar("SELECT count(*) FROM tx_forgotten WHERE kind = 'project'"));
    }

    [Fact]
    public void ClearsTheCutoff_AndSaysWhenThereIsNone()
    {
        SeedRecords();

        Assert.Equal(0, Unforget("--cutoff"));
        Assert.Contains("removed", _h.Out.ToString());
        Assert.Equal(0, _h.Scalar("SELECT count(*) FROM tx_state"));

        Assert.Equal(0, Unforget("--cutoff"));
        Assert.Contains("no cutoff", _h.Out.ToString());
    }

    [Fact]
    public void ADecline_ChangesNothing()
    {
        SeedRecords();
        _h.Answer = false;

        Assert.Equal(0, Unforget("--project", "owner/one"));
        Assert.Equal(1, _h.Scalar("SELECT count(*) FROM tx_forgotten WHERE kind = 'project'"));
    }

    [Fact]
    public void WithNoArchiveTables_SaysNothingIsStored_AndCreatesNothing()
    {
        var code = Unforget("--cutoff");

        Assert.Equal(0, code);
        Assert.Contains("nothing is stored", _h.Out.ToString());
        Assert.False(File.Exists(_h.DbPath));
    }

    [Fact]
    public void AMalformedConfig_ExitsOne_DoesNotSayNothingIsStored_AndLeavesASeparateArchiveIntact()
    {
        var archive = _h.CreateSeparateArchive();
        _h.WriteMalformedConfig(archive);

        var code = Unforget("--cutoff");

        Assert.Equal(1, code);
        Assert.Contains("could not read the configuration", _h.Err.ToString());
        Assert.DoesNotContain("nothing is stored", _h.Out.ToString() + _h.Err.ToString());
        Assert.Empty(_h.Prompts);
        Assert.Equal((1L, 1L), _h.ArchiveCounts(archive));
    }

    [Fact]
    public void TheCommand_IsInTheTranscriptsGroup_AndVisible()
    {
        var command = new UnforgetCommand();
        Assert.Equal("unforget", command.Name);
        Assert.Equal("transcripts", command.Group);
        Assert.False(command.Hidden);
    }
}
