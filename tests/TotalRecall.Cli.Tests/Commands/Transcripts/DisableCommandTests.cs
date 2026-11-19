using System;
using System.IO;
using TotalRecall.Cli.Commands.Transcripts;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands.Transcripts;

[Collection("ConsoleCapture")]
public sealed class DisableCommandTests : IDisposable
{
    private readonly TranscriptsHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void NeedsNoTerminal_WritesFalse_RemovesTheSentinel_AndKeepsTheData()
    {
        _h.CreateArchive();
        var id = _h.AddSession("claude-code", "s1");
        _h.AddChunk(id, "hello");
        _h.WriteConfig("[transcripts]\nenabled = true\n");
        _h.Sentinel.Write();
        _h.Interactive = false;

        var code = _h.Run(h => h.Disable());

        Assert.Equal(0, code);
        Assert.Empty(_h.Prompts);
        Assert.False(_h.ReadSettings().Enabled);
        Assert.Contains("enabled = false", _h.ConfigText());
        Assert.False(File.Exists(_h.Sentinel.Path));
        Assert.Equal(1, _h.Scalar("SELECT count(*) FROM tx_session"));
        Assert.Equal(1, _h.Scalar("SELECT count(*) FROM tx_chunk"));
        Assert.Contains("disabled", _h.Out.ToString());
    }

    [Fact]
    public void WithNothingThere_StillWritesFalse()
    {
        var code = _h.Run(h => h.Disable());

        Assert.Equal(0, code);
        Assert.Contains("enabled = false", _h.ConfigText());
        Assert.False(File.Exists(_h.Sentinel.Path));
        Assert.False(File.Exists(_h.DbPath));
    }

    [Fact]
    public void ExtraArguments_AreAUsageError()
    {
        var code = _h.Run(h => h.Disable(), "--now");

        Assert.Equal(2, code);
        Assert.Null(_h.ConfigText());
    }

    [Fact]
    public void TheCommand_IsInTheTranscriptsGroup_AndVisible()
    {
        var command = new DisableCommand();
        Assert.Equal("disable", command.Name);
        Assert.Equal("transcripts", command.Group);
        Assert.False(command.Hidden);
    }
}
