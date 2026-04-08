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
public sealed class LineageCommandTests : IDisposable
{
    private readonly TextWriter _origOut;
    private readonly TextWriter _origErr;
    private readonly StringWriter _outWriter = new();
    private readonly StringWriter _errWriter = new();

    public LineageCommandTests()
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
    public void Help_ReturnsZero()
    {
        var code = CliApp.Run(new[] { "memory", "lineage", "--help" });
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task MissingId_ReturnsExit2()
    {
        var cmd = new LineageCommand(new FakeCompactionLog());
        var code = await cmd.RunAsync(Array.Empty<string>());
        Assert.Equal(2, code);
        Assert.Contains("<id>", _errWriter.ToString());
    }

    [Fact]
    public async Task Leaf_Json_ReturnsIdOnly()
    {
        var cmd = new LineageCommand(new FakeCompactionLog());
        var code = await cmd.RunAsync(new[] { "orphan", "--json" });
        Assert.Equal(0, code);

        using var doc = JsonDocument.Parse(_outWriter.ToString());
        Assert.Equal("orphan", doc.RootElement.GetProperty("id").GetString());
        // Leaf nodes omit the "sources" field entirely.
        Assert.False(doc.RootElement.TryGetProperty("sources", out _));
        Assert.False(doc.RootElement.TryGetProperty("compaction_log_id", out _));
    }

    [Fact]
    public async Task TwoLevelTree_Json_ReturnsRootWithTwoSources()
    {
        // A is the target of compaction from [B, C].
        var fake = new FakeCompactionLog();
        fake.Add(FakeCompactionLog.Row(
            id: "log-A",
            timestamp: 100,
            targetEntryId: "A",
            sourceEntryIds: new[] { "B", "C" }));

        var cmd = new LineageCommand(fake);
        var code = await cmd.RunAsync(new[] { "A", "--json" });
        Assert.Equal(0, code);

        using var doc = JsonDocument.Parse(_outWriter.ToString());
        var root = doc.RootElement;
        Assert.Equal("A", root.GetProperty("id").GetString());
        Assert.Equal("log-A", root.GetProperty("compaction_log_id").GetString());
        Assert.Equal("decay", root.GetProperty("reason").GetString());
        Assert.Equal(100L, root.GetProperty("timestamp").GetInt64());
        var sources = root.GetProperty("sources");
        Assert.Equal(2, sources.GetArrayLength());
        Assert.Equal("B", sources[0].GetProperty("id").GetString());
        Assert.Equal("C", sources[1].GetProperty("id").GetString());
        // B and C are leaves — no sources field.
        Assert.False(sources[0].TryGetProperty("sources", out _));
        Assert.False(sources[1].TryGetProperty("sources", out _));
    }

    [Fact]
    public async Task ThreeLevelChain_Json_WalksFullChain()
    {
        // A <- [B] <- [C]. A is target of compaction from [B]; B is target
        // of an earlier compaction from [C].
        var fake = new FakeCompactionLog();
        fake.Add(FakeCompactionLog.Row(
            id: "log-A",
            timestamp: 200,
            targetEntryId: "A",
            sourceEntryIds: new[] { "B" }));
        fake.Add(FakeCompactionLog.Row(
            id: "log-B",
            timestamp: 100,
            targetEntryId: "B",
            sourceEntryIds: new[] { "C" }));

        var cmd = new LineageCommand(fake);
        var code = await cmd.RunAsync(new[] { "A", "--json" });
        Assert.Equal(0, code);

        using var doc = JsonDocument.Parse(_outWriter.ToString());
        var root = doc.RootElement;
        Assert.Equal("A", root.GetProperty("id").GetString());
        var b = root.GetProperty("sources")[0];
        Assert.Equal("B", b.GetProperty("id").GetString());
        Assert.Equal("log-B", b.GetProperty("compaction_log_id").GetString());
        var c = b.GetProperty("sources")[0];
        Assert.Equal("C", c.GetProperty("id").GetString());
        Assert.False(c.TryGetProperty("sources", out _));
    }

    [Fact]
    public async Task DepthCap_StopsAtTen()
    {
        // Chain of 12: N0 <- N1 <- ... <- N11. Walking from N0 should cap
        // at depth 10 (matching TS buildLineage at extra-tools.ts:134).
        var fake = new FakeCompactionLog();
        for (int i = 0; i < 12; i++)
        {
            fake.Add(FakeCompactionLog.Row(
                id: $"log-{i}",
                timestamp: 1000 - i,
                targetEntryId: $"N{i}",
                sourceEntryIds: new[] { $"N{i + 1}" }));
        }

        var cmd = new LineageCommand(fake);
        var code = await cmd.RunAsync(new[] { "N0", "--json" });
        Assert.Equal(0, code);

        using var doc = JsonDocument.Parse(_outWriter.ToString());
        // Walk down 10 levels — at depth 10 BuildLineage bails out and
        // returns the id with an empty sources array (no compaction_log_id,
        // because we never inspected the row at that depth).
        var node = doc.RootElement;
        for (int depth = 0; depth < 10; depth++)
        {
            Assert.Equal($"N{depth}", node.GetProperty("id").GetString());
            Assert.True(node.TryGetProperty("sources", out var srcs));
            Assert.Equal(1, srcs.GetArrayLength());
            node = srcs[0];
        }
        // Depth 10: bail-out node — id only, empty sources, no log id.
        Assert.Equal("N10", node.GetProperty("id").GetString());
        Assert.False(node.TryGetProperty("compaction_log_id", out _));
        Assert.Equal(0, node.GetProperty("sources").GetArrayLength());
    }

    [Fact]
    public async Task Cycle_AB_BreaksBeforeDepthCap()
    {
        // Task 5.10 item 7: A <- [B] and B <- [A]. BuildLineage must
        // short-circuit the second A via the visited set instead of
        // exhausting the depth cap. We assert the walk is finite AND
        // that we do NOT end up 10 levels deep.
        var fake = new FakeCompactionLog();
        fake.Add(FakeCompactionLog.Row(
            id: "log-A",
            timestamp: 200,
            targetEntryId: "A",
            sourceEntryIds: new[] { "B" }));
        fake.Add(FakeCompactionLog.Row(
            id: "log-B",
            timestamp: 100,
            targetEntryId: "B",
            sourceEntryIds: new[] { "A" }));

        var cmd = new LineageCommand(fake);
        var code = await cmd.RunAsync(new[] { "A", "--json" });
        Assert.Equal(0, code);

        using var doc = JsonDocument.Parse(_outWriter.ToString());
        var root = doc.RootElement;
        Assert.Equal("A", root.GetProperty("id").GetString());
        // Depth 1: B (compacted node).
        var b = root.GetProperty("sources")[0];
        Assert.Equal("B", b.GetProperty("id").GetString());
        Assert.Equal("log-B", b.GetProperty("compaction_log_id").GetString());
        // Depth 2: A again — cycle break should emit it as a leaf
        // (id-only, no compaction_log_id, no sources).
        var aAgain = b.GetProperty("sources")[0];
        Assert.Equal("A", aAgain.GetProperty("id").GetString());
        Assert.False(aAgain.TryGetProperty("compaction_log_id", out _));
        Assert.False(aAgain.TryGetProperty("sources", out _));
    }

    [Fact]
    public async Task Json_SpecialCharsInReason_RoundTrips()
    {
        var fake = new FakeCompactionLog();
        var nasty = "has \"quote\" and \\backslash\nnewline";
        fake.Add(FakeCompactionLog.Row(
            id: "log-A",
            timestamp: 100,
            targetEntryId: "A",
            sourceEntryIds: new[] { "B" },
            reason: nasty));

        var cmd = new LineageCommand(fake);
        var code = await cmd.RunAsync(new[] { "A", "--json" });
        Assert.Equal(0, code);

        using var doc = JsonDocument.Parse(_outWriter.ToString());
        Assert.Equal(nasty, doc.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task DefaultRender_ReturnsZero()
    {
        var fake = new FakeCompactionLog();
        fake.Add(FakeCompactionLog.Row(
            id: "log-A",
            timestamp: 100,
            targetEntryId: "A",
            sourceEntryIds: new[] { "B" }));

        var cmd = new LineageCommand(fake);
        var code = await cmd.RunAsync(new[] { "A" });
        // Spectre Tree renders via AnsiConsole which bypasses Console.SetOut.
        Assert.Equal(0, code);
        Assert.Equal("", _errWriter.ToString());
    }
}
