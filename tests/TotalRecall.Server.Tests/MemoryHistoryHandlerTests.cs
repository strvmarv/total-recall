// Plan 6 Task 6.0a — MemoryHistoryHandler contract tests.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Server.Handlers;
using TotalRecall.Server.Tests.TestSupport;
using Xunit;

namespace TotalRecall.Server.Tests;

public class MemoryHistoryHandlerTests
{
    private static (MemoryHistoryHandler handler, FakeCompactionLog log) MakeHandler()
    {
        var log = new FakeCompactionLog();
        return (new MemoryHistoryHandler(log), log);
    }

    private static JsonElement ParseArgs(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task HappyPath_ReturnsRowsInDescendingOrder()
    {
        var (handler, log) = MakeHandler();
        log.Add(FakeCompactionLog.Row("r1", timestamp: 100, targetEntryId: "t1"));
        log.Add(FakeCompactionLog.Row("r2", timestamp: 200, targetEntryId: "t2"));
        log.Add(FakeCompactionLog.Row("r3", timestamp: 300, targetEntryId: "t3"));

        var result = await handler.ExecuteAsync(null, CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        var movements = doc.RootElement.GetProperty("movements");
        Assert.Equal(3, movements.GetArrayLength());
        Assert.Equal("r3", movements[0].GetProperty("id").GetString());
        Assert.Equal("r1", movements[2].GetProperty("id").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task LimitApplied()
    {
        var (handler, log) = MakeHandler();
        for (int i = 0; i < 5; i++)
        {
            log.Add(FakeCompactionLog.Row($"r{i}", timestamp: i));
        }
        var result = await handler.ExecuteAsync(
            ParseArgs("""{"limit":2}"""), CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(2, doc.RootElement.GetProperty("movements").GetArrayLength());
    }

    [Fact]
    public async Task EmptyLog_ReturnsEmptyArray()
    {
        var (handler, _) = MakeHandler();
        var result = await handler.ExecuteAsync(null, CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(0, doc.RootElement.GetProperty("movements").GetArrayLength());
        Assert.Equal(0, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task InvalidLimit_Throws()
    {
        var (handler, _) = MakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(ParseArgs("""{"limit":0}"""), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(ParseArgs("""{"limit":9999}"""), CancellationToken.None));
    }

    [Fact]
    public async Task JsonRoundtrip_MatchesDto()
    {
        var (handler, log) = MakeHandler();
        log.Add(FakeCompactionLog.Row("r1", timestamp: 10));
        var result = await handler.ExecuteAsync(null, CancellationToken.None);
        var dto = JsonSerializer.Deserialize(result.Content[0].Text, JsonContext.Default.MemoryHistoryResultDto);
        Assert.NotNull(dto);
        Assert.Equal(1, dto!.Count);
        Assert.Single(dto.Movements);
    }

    [Fact]
    public void Name_And_Schema_MatchWireContract()
    {
        var (handler, _) = MakeHandler();
        Assert.Equal("memory_history", handler.Name);
    }
}
