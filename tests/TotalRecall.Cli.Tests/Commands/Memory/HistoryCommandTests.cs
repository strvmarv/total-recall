using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using TotalRecall.Cli;
using TotalRecall.Cli.Commands.Memory;
using TotalRecall.Cli.Tests.TestSupport;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands.Memory;

[Collection("ConsoleCapture")]
public sealed class HistoryCommandTests : IDisposable
{
    private readonly TextWriter _origOut;
    private readonly TextWriter _origErr;
    private readonly StringWriter _outWriter = new();
    private readonly StringWriter _errWriter = new();

    public HistoryCommandTests()
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
    public async Task Help_ReturnsZero()
    {
        // --help goes through CliApp's dispatcher, not the command directly.
        // HistoryCommand on its own doesn't parse --help (the dispatcher
        // intercepts it before RunAsync is called). We simulate by asking
        // CliApp.Run directly.
        var code = await Task.FromResult(CliApp.Run(new[] { "memory", "history", "--help" }));
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task Empty_DefaultRender_PrintsPlaceholder()
    {
        var cmd = new HistoryCommand(new FakeCompactionLog());
        var code = await cmd.RunAsync(Array.Empty<string>());
        Assert.Equal(0, code);
        Assert.Contains("(no compactions yet)", _outWriter.ToString());
    }

    [Fact]
    public async Task HappyPath_Json_RoundTripsTwoMovements()
    {
        var fake = new FakeCompactionLog();
        fake.Add(FakeCompactionLog.Row(id: "m1", timestamp: 100, targetEntryId: "t1",
            sourceEntryIds: new[] { "a", "b" }));
        fake.Add(FakeCompactionLog.Row(id: "m2", timestamp: 200, targetEntryId: "t2",
            sourceEntryIds: new[] { "c" }));
        var cmd = new HistoryCommand(fake);

        var code = await cmd.RunAsync(new[] { "--json" });
        Assert.Equal(0, code);

        using var doc = JsonDocument.Parse(_outWriter.ToString());
        var root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("count").GetInt32());
        var movements = root.GetProperty("movements");
        Assert.Equal(2, movements.GetArrayLength());
        // Ordered DESC (timestamp 200 first).
        Assert.Equal("m2", movements[0].GetProperty("id").GetString());
        Assert.Equal(200L, movements[0].GetProperty("timestamp").GetInt64());
        Assert.Equal("t2", movements[0].GetProperty("target_entry_id").GetString());
        Assert.Equal("m1", movements[1].GetProperty("id").GetString());
        var srcIds = movements[1].GetProperty("source_entry_ids");
        Assert.Equal(2, srcIds.GetArrayLength());
        Assert.Equal("a", srcIds[0].GetString());
        Assert.Equal("b", srcIds[1].GetString());
    }

    [Fact]
    public async Task HappyPath_DefaultRender_ReturnsZero()
    {
        var fake = new FakeCompactionLog();
        fake.Add(FakeCompactionLog.Row(id: "m1", timestamp: 100, targetEntryId: "t1"));
        var cmd = new HistoryCommand(fake);

        var code = await cmd.RunAsync(Array.Empty<string>());
        // Default (Spectre table) render — assert exit only; AnsiConsole
        // bypasses Console.SetOut (see CliApp.cs comment).
        Assert.Equal(0, code);
        Assert.Equal("", _errWriter.ToString());
    }

    [Fact]
    public async Task Limit_Caps_MovementsList()
    {
        var fake = new FakeCompactionLog();
        fake.Add(FakeCompactionLog.Row(id: "m1", timestamp: 100));
        fake.Add(FakeCompactionLog.Row(id: "m2", timestamp: 200));
        fake.Add(FakeCompactionLog.Row(id: "m3", timestamp: 300));
        var cmd = new HistoryCommand(fake);

        var code = await cmd.RunAsync(new[] { "--limit", "1", "--json" });
        Assert.Equal(0, code);

        using var doc = JsonDocument.Parse(_outWriter.ToString());
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("m3", doc.RootElement.GetProperty("movements")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Limit_Zero_ReturnsExit2()
    {
        var cmd = new HistoryCommand(new FakeCompactionLog());
        var code = await cmd.RunAsync(new[] { "--limit", "0" });
        Assert.Equal(2, code);
        Assert.Contains("1 and 1000", _errWriter.ToString());
    }

    [Fact]
    public async Task Json_SpecialCharsInReason_RoundTrips()
    {
        var fake = new FakeCompactionLog();
        var nasty = "has \"quote\" and \\backslash\nnewline";
        fake.Add(FakeCompactionLog.Row(id: "m1", timestamp: 100, reason: nasty));
        var cmd = new HistoryCommand(fake);

        var code = await cmd.RunAsync(new[] { "--json", "--limit", "10" });
        Assert.Equal(0, code);

        using var doc = JsonDocument.Parse(_outWriter.ToString());
        var movement = doc.RootElement.GetProperty("movements")[0];
        Assert.Equal(nasty, movement.GetProperty("reason").GetString());
    }
}
