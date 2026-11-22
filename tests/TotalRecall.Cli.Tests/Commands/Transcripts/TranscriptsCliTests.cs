using System;
using System.IO;
using System.Threading.Tasks;
using TotalRecall.Cli;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands.Transcripts;

/// <summary>The <c>transcripts</c> group through <see cref="CliApp.RunAsync"/> with the production commands.</summary>
[Collection("ConsoleCapture")]
public sealed class TranscriptsCliTests : IDisposable
{
    private static readonly string[] Verbs = ["enable", "disable", "status", "backfill", "embed", "forget", "unforget"];

    private readonly TranscriptsHarness _h = new();
    private readonly TextWriter _origOut = Console.Out;
    private readonly TextWriter _origErr = Console.Error;

    public void Dispose()
    {
        CliApp.SetRegistryForTestsInternal(null);
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
        _h.Dispose();
    }

    private static async Task<(int Code, string Out, string Err)> Capture(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        Console.SetOut(o);
        Console.SetError(e);
        var code = await CliApp.RunAsync(args);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public async Task TranscriptsAppearsInTheTopLevelHelp()
    {
        var help = await Capture("--help");
        Assert.Equal(0, help.Code);
        Assert.Contains("transcripts", help.Out);
    }

    [Fact]
    public async Task TheGroupHelp_ListsTheSevenVerbs_AndEachAnswersItsOwnHelp()
    {
        var group = await Capture("transcripts", "--help");
        Assert.Equal(0, group.Code);
        foreach (var verb in Verbs)
            Assert.Contains(verb, group.Out);

        foreach (var verb in Verbs)
        {
            var help = await Capture("transcripts", verb, "--help");
            Assert.Equal(0, help.Code);
            Assert.Contains($"total-recall transcripts {verb}", help.Out);
        }
    }

    [Fact]
    public async Task ProductionStatus_ReadsTheRedirectedDataDirectory_AndCreatesNothing()
    {
        var result = await Capture("transcripts", "status");

        Assert.Equal(0, result.Code);
        Assert.Contains("off, nothing stored", result.Out);
        Assert.DoesNotContain("search: not available in this version yet", result.Out);
        Assert.DoesNotContain("search:", result.Out);
        Assert.Contains(_h.DbPath, result.Out);
        Assert.False(Directory.Exists(_h.DataDir));
    }

    [Fact]
    public async Task ProductionDisable_WritesTheRedirectedConfig()
    {
        var result = await Capture("transcripts", "disable");

        Assert.Equal(0, result.Code);
        Assert.Contains("enabled = false", _h.ConfigText());
    }
}
