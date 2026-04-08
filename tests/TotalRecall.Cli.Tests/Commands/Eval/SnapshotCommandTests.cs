using System;
using System.IO;
using System.Threading.Tasks;
using TotalRecall.Cli.Commands.Eval;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands.Eval;

[Collection("ConsoleCapture")]
public sealed class SnapshotCommandTests : IDisposable
{
    private readonly TextWriter _origOut;
    private readonly TextWriter _origErr;
    private readonly StringWriter _outWriter;
    private readonly StringWriter _errWriter;

    public SnapshotCommandTests()
    {
        _origOut = Console.Out;
        _origErr = Console.Error;
        _outWriter = new StringWriter();
        _errWriter = new StringWriter();
        Console.SetOut(_outWriter);
        Console.SetError(_errWriter);
    }

    public void Dispose()
    {
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
    }

    [Fact]
    public async Task RequiresName_ReturnsExit2()
    {
        var cmd = new SnapshotCommand(_ => ("id", false));
        var code = await cmd.RunAsync(Array.Empty<string>());
        Assert.Equal(2, code);
    }

    [Fact]
    public async Task PrintsId_OnSuccess()
    {
        var cmd = new SnapshotCommand(_ => ("snap-id", false));
        var code = await cmd.RunAsync(new[] { "baseline" });
        Assert.Equal(0, code);
        Assert.Contains("snap-id", _outWriter.ToString());
        Assert.DoesNotContain("deduped", _outWriter.ToString());
    }

    [Fact]
    public async Task NotesDedup_WhenLatestMatches()
    {
        var cmd = new SnapshotCommand(_ => ("dup-id", true));
        var code = await cmd.RunAsync(new[] { "baseline" });
        Assert.Equal(0, code);
        Assert.Contains("dup-id", _outWriter.ToString());
        Assert.Contains("deduped", _outWriter.ToString());
    }

    [Fact]
    public async Task ExecutorThrows_ReturnsExit1()
    {
        var cmd = new SnapshotCommand(_ => throw new InvalidOperationException("bad"));
        var code = await cmd.RunAsync(new[] { "baseline" });
        Assert.Equal(1, code);
        Assert.Contains("bad", _errWriter.ToString());
    }
}
