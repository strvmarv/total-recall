using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Cli.Commands.Memory;
using TotalRecall.Cli.Tests.TestSupport;
using TotalRecall.Core;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands.Memory;

[Collection("ConsoleCapture")]
public sealed class RecentCommandTests : IDisposable
{
    private readonly TextWriter _origOut;
    private readonly TextWriter _origErr;
    private readonly StringWriter _outWriter = new();
    private readonly StringWriter _errWriter = new();

    public RecentCommandTests()
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

    private static Entry MakeEntry(string id, long ts, EntryType type) =>
        new Entry(
            id, "c-" + id,
            FSharpOption<string>.None, FSharpOption<string>.None,
            FSharpOption<SourceTool>.None, FSharpOption<string>.None,
            ListModule.OfSeq(Array.Empty<string>()),
            ts, ts + 1, ts + 2, 0, 0.5,
            FSharpOption<string>.None, FSharpOption<string>.None, "user:local", type, "{}");

    [Fact]
    public async Task Json_MergesAndSortsDesc()
    {
        var store = new FakeStore();
        store.Seed(Tier.Hot, ContentType.Memory, MakeEntry("h", 100, EntryType.Preference));
        store.Seed(Tier.Warm, ContentType.Memory, MakeEntry("w", 300, EntryType.Preference));
        var cmd = new RecentCommand(store);

        var code = await cmd.RunAsync(new[] { "--json" });
        Assert.Equal(0, code);

        using var doc = JsonDocument.Parse(_outWriter.ToString());
        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("w", doc.RootElement.GetProperty("entries")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Json_TypeFilter()
    {
        var store = new FakeStore();
        store.Seed(Tier.Hot, ContentType.Memory, MakeEntry("corr", 100, EntryType.Correction));
        store.Seed(Tier.Hot, ContentType.Memory, MakeEntry("pref", 200, EntryType.Preference));
        var cmd = new RecentCommand(store);

        var code = await cmd.RunAsync(new[] { "--type", "correction", "--json" });
        Assert.Equal(0, code);

        using var doc = JsonDocument.Parse(_outWriter.ToString());
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("corr", doc.RootElement.GetProperty("entries")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Limit_Invalid_ReturnsExit2()
    {
        var cmd = new RecentCommand(new FakeStore());
        var code = await cmd.RunAsync(new[] { "--limit", "0" });
        Assert.Equal(2, code);
        Assert.Contains("1 and 200", _errWriter.ToString());
    }

    [Fact]
    public async Task Tier_Invalid_ReturnsExit2()
    {
        var cmd = new RecentCommand(new FakeStore());
        var code = await cmd.RunAsync(new[] { "--tier", "bogus" });
        Assert.Equal(2, code);
        Assert.Contains("hot, warm, or cold", _errWriter.ToString());
    }

    [Fact]
    public async Task Type_Invalid_ReturnsExit2()
    {
        var cmd = new RecentCommand(new FakeStore());
        var code = await cmd.RunAsync(new[] { "--type", "bogus" });
        Assert.Equal(2, code);
    }

    [Fact]
    public async Task NonJson_Empty_PrintsNoMemories()
    {
        var cmd = new RecentCommand(new FakeStore());
        var code = await cmd.RunAsync(Array.Empty<string>());
        Assert.Equal(0, code);
        Assert.Contains("(no memories yet)", _outWriter.ToString());
    }

    [Fact]
    public async Task NonJson_Render_DoesNotThrow()
    {
        var store = new FakeStore();
        store.Seed(Tier.Hot, ContentType.Memory, MakeEntry("h", 100, EntryType.Preference));
        var cmd = new RecentCommand(store);
        // Default (non-JSON) render goes through the Spectre table path. Spectre may
        // write to its own cached console rather than the redirected Console.Out, so we
        // only assert the path executes successfully (exit 0), not its captured text.
        var code = await cmd.RunAsync(Array.Empty<string>());
        Assert.Equal(0, code);
    }
}
