using System;
using System.IO;
using System.Threading.Tasks;
using TotalRecall.Cli.Commands;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands;

[Collection("ConsoleCapture")]
public sealed class CompactCommandTests : IDisposable
{
    private readonly TextWriter _origOut;
    private readonly TextWriter _origErr;
    private readonly StringWriter _outWriter = new();
    private readonly StringWriter _errWriter = new();

    public CompactCommandTests()
    {
        _origOut = Console.Out;
        _origErr = Console.Error;
        Console.SetOut(_outWriter);
        Console.SetError(_errWriter);
    }

    public void Dispose()
    {
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
    }

    [Fact]
    public async Task HelpFlag_ReturnsZero()
    {
        var injected = new StringWriter();
        var cmd = new CompactCommand(injected);

        var code = await cmd.RunAsync(new[] { "--help" });

        Assert.Equal(0, code);
    }

    [Fact]
    public async Task NoArgs_PrintsExplanation_ReturnsZero()
    {
        var injected = new StringWriter();
        var cmd = new CompactCommand(injected);

        var code = await cmd.RunAsync(Array.Empty<string>());

        Assert.Equal(0, code);
        var text = injected.ToString();
        Assert.Contains("host tool", text);
        Assert.Contains("session_context", text);
        Assert.Contains("memory history", text);
    }

    [Fact]
    public async Task UnknownArg_ReturnsExit2()
    {
        var injected = new StringWriter();
        var cmd = new CompactCommand(injected);

        var code = await cmd.RunAsync(new[] { "--bogus" });

        Assert.Equal(2, code);
    }
}
