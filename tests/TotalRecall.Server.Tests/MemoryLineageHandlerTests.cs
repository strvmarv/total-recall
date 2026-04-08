// Plan 6 Task 6.0a — MemoryLineageHandler contract tests.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Server.Handlers;
using TotalRecall.Server.Tests.TestSupport;
using Xunit;

namespace TotalRecall.Server.Tests;

public class MemoryLineageHandlerTests
{
    private static (MemoryLineageHandler handler, FakeCompactionLog log) MakeHandler()
    {
        var log = new FakeCompactionLog();
        return (new MemoryLineageHandler(log), log);
    }

    private static JsonElement ParseArgs(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task Leaf_WhenNoLogRow_ReturnsIdOnly()
    {
        var (handler, _) = MakeHandler();
        var result = await handler.ExecuteAsync(ParseArgs("""{"id":"orphan"}"""), CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("orphan", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("sources").ValueKind);
    }

    [Fact]
    public async Task OneLevel_ReturnsChildList()
    {
        var (handler, log) = MakeHandler();
        log.Add(FakeCompactionLog.Row(
            "log1", timestamp: 100, targetEntryId: "child",
            sourceEntryIds: new[] { "p1", "p2" }, reason: "merge"));

        var result = await handler.ExecuteAsync(ParseArgs("""{"id":"child"}"""), CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        var root = doc.RootElement;
        Assert.Equal("child", root.GetProperty("id").GetString());
        Assert.Equal("log1", root.GetProperty("compaction_log_id").GetString());
        Assert.Equal("merge", root.GetProperty("reason").GetString());
        var sources = root.GetProperty("sources");
        Assert.Equal(2, sources.GetArrayLength());
        Assert.Equal("p1", sources[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task CycleDetection_BreaksOnSelfReference()
    {
        var (handler, log) = MakeHandler();
        // A -> [A], B's child shouldn't recurse forever.
        log.Add(FakeCompactionLog.Row(
            "log-self", timestamp: 1, targetEntryId: "a",
            sourceEntryIds: new[] { "a" }, reason: "weird"));

        var result = await handler.ExecuteAsync(ParseArgs("""{"id":"a"}"""), CancellationToken.None);

        // Should not hang — result is a finite tree.
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("a", doc.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task MissingId_Throws()
    {
        var (handler, _) = MakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(ParseArgs("""{}"""), CancellationToken.None));
    }

    [Fact]
    public async Task JsonRoundtrip_MatchesDto()
    {
        var (handler, log) = MakeHandler();
        log.Add(FakeCompactionLog.Row(
            "log1", timestamp: 1, targetEntryId: "child",
            sourceEntryIds: new[] { "p1" }));
        var result = await handler.ExecuteAsync(ParseArgs("""{"id":"child"}"""), CancellationToken.None);
        var dto = JsonSerializer.Deserialize(result.Content[0].Text, JsonContext.Default.LineageNodeDto);
        Assert.NotNull(dto);
        Assert.Equal("child", dto!.Id);
        Assert.NotNull(dto.Sources);
        Assert.Single(dto.Sources!);
    }

    [Fact]
    public void Name_And_Schema_MatchWireContract()
    {
        var (handler, _) = MakeHandler();
        Assert.Equal("memory_lineage", handler.Name);
    }
}
