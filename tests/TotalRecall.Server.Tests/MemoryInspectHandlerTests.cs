// Plan 6 Task 6.0a — MemoryInspectHandler contract tests.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Server.Handlers;
using TotalRecall.Server.Tests.TestSupport;
using Xunit;

namespace TotalRecall.Server.Tests;

public class MemoryInspectHandlerTests
{
    private static (MemoryInspectHandler handler, FakeStore store, FakeCompactionLog log) MakeHandler()
    {
        var store = new FakeStore();
        var log = new FakeCompactionLog();
        return (new MemoryInspectHandler(store, log), store, log);
    }

    private static JsonElement ParseArgs(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static Entry MakeEntry(string id, string content = "hello") =>
        new(
            id, content,
            FSharpOption<string>.Some("sum"), FSharpOption<string>.Some("src"),
            FSharpOption<SourceTool>.Some(SourceTool.ClaudeCode), FSharpOption<string>.Some("proj"),
            ListModule.OfSeq(new[] { "a", "b" }),
            100L, 200L, 300L, 4, 0.75,
            FSharpOption<string>.None, FSharpOption<string>.None, "{\"key\":\"val\"}");

    [Fact]
    public async Task HappyPath_Found_ReturnsFullDetails()
    {
        var (handler, store, _) = MakeHandler();
        store.Seed(Tier.Warm, ContentType.Memory, MakeEntry("w1"));

        var result = await handler.ExecuteAsync(ParseArgs("""{"id":"w1"}"""), CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        var root = doc.RootElement;
        Assert.Equal("w1", root.GetProperty("id").GetString());
        Assert.Equal("warm", root.GetProperty("tier").GetString());
        Assert.Equal("memory", root.GetProperty("content_type").GetString());
        Assert.Equal("hello", root.GetProperty("content").GetString());
        Assert.Equal("sum", root.GetProperty("summary").GetString());
        Assert.Equal("src", root.GetProperty("source").GetString());
        Assert.Equal("claude-code", root.GetProperty("source_tool").GetString());
        Assert.Equal("proj", root.GetProperty("project").GetString());
        Assert.Equal(2, root.GetProperty("tags").GetArrayLength());
        Assert.Equal(100L, root.GetProperty("created_at").GetInt64());
        Assert.Equal(4, root.GetProperty("access_count").GetInt32());
        Assert.Equal(0.75, root.GetProperty("decay_score").GetDouble(), 6);
        Assert.Equal("{\"key\":\"val\"}", root.GetProperty("metadata").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("compaction_history").ValueKind);
    }

    [Fact]
    public async Task CompactionHistory_Attached_WhenLogHasRow()
    {
        var (handler, store, log) = MakeHandler();
        store.Seed(Tier.Warm, ContentType.Memory, MakeEntry("w1"));
        log.Add(FakeCompactionLog.Row(
            "log-1", timestamp: 12345L, targetEntryId: "w1",
            sourceEntryIds: new[] { "s1", "s2" }, reason: "decay"));

        var result = await handler.ExecuteAsync(ParseArgs("""{"id":"w1"}"""), CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        var hist = doc.RootElement.GetProperty("compaction_history");
        Assert.Equal(JsonValueKind.Object, hist.ValueKind);
        Assert.Equal("log-1", hist.GetProperty("id").GetString());
        Assert.Equal(12345L, hist.GetProperty("timestamp").GetInt64());
        Assert.Equal("decay", hist.GetProperty("reason").GetString());
        Assert.Equal(2, hist.GetProperty("source_entry_ids").GetArrayLength());
    }

    [Fact]
    public async Task NotFound_ReturnsNull()
    {
        var (handler, _, _) = MakeHandler();
        var result = await handler.ExecuteAsync(ParseArgs("""{"id":"missing"}"""), CancellationToken.None);
        Assert.Equal("null", result.Content[0].Text);
    }

    [Fact]
    public async Task MissingId_Throws()
    {
        var (handler, _, _) = MakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(ParseArgs("""{}"""), CancellationToken.None));
    }

    [Fact]
    public async Task JsonRoundtrip_MatchesDto()
    {
        var (handler, store, _) = MakeHandler();
        store.Seed(Tier.Hot, ContentType.Knowledge, MakeEntry("h1"));
        var result = await handler.ExecuteAsync(ParseArgs("""{"id":"h1"}"""), CancellationToken.None);
        var dto = JsonSerializer.Deserialize(result.Content[0].Text, JsonContext.Default.MemoryInspectResultDto);
        Assert.NotNull(dto);
        Assert.Equal("h1", dto!.Id);
        Assert.Equal("knowledge", dto.ContentType);
    }

    [Fact]
    public void Name_And_Schema_MatchWireContract()
    {
        var (handler, _, _) = MakeHandler();
        Assert.Equal("memory_inspect", handler.Name);
    }
}
