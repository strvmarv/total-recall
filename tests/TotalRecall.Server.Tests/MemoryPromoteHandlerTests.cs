// Plan 6 Task 6.0a — MemoryPromoteHandler contract tests.

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

public class MemoryPromoteHandlerTests
{
    private static (MemoryPromoteHandler handler, FakeStore store,
            FakeVectorSearch vec, RecordingFakeEmbedder embedder) MakeHandler()
    {
        var store = new FakeStore();
        var vec = new FakeVectorSearch();
        var embedder = new RecordingFakeEmbedder();
        return (new MemoryPromoteHandler(store, vec, embedder), store, vec, embedder);
    }

    private static JsonElement ParseArgs(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static Entry MakeEntry(string id, string content = "hello") =>
        new(
            id, content,
            FSharpOption<string>.None, FSharpOption<string>.None,
            FSharpOption<SourceTool>.None, FSharpOption<string>.None,
            ListModule.OfSeq(Array.Empty<string>()),
            1L, 2L, 3L, 4, 0.5,
            FSharpOption<string>.None, FSharpOption<string>.None, "{}");

    [Fact]
    public async Task HappyPath_WarmToHot_MovesAndReEmbeds()
    {
        var (handler, store, vec, embedder) = MakeHandler();
        store.Seed(Tier.Warm, ContentType.Memory, MakeEntry("w1", "body"));

        var result = await handler.ExecuteAsync(ParseArgs("""{"id":"w1"}"""), CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("w1", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("warm", doc.RootElement.GetProperty("from_tier").GetString());
        Assert.Equal("memory", doc.RootElement.GetProperty("from_content_type").GetString());
        Assert.Equal("hot", doc.RootElement.GetProperty("to_tier").GetString());
        Assert.Equal("memory", doc.RootElement.GetProperty("to_content_type").GetString());
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());

        Assert.Single(vec.DeleteCalls);
        Assert.Equal(Tier.Warm, vec.DeleteCalls[0].Tier);
        Assert.Single(store.MoveCalls);
        Assert.Equal(Tier.Warm, store.MoveCalls[0].FromTier);
        Assert.Equal(Tier.Hot, store.MoveCalls[0].ToTier);
        Assert.Single(vec.InsertCalls);
        Assert.Equal(Tier.Hot, vec.InsertCalls[0].Tier);
        Assert.Single(embedder.Calls);
        Assert.Equal("body", embedder.Calls[0]);
    }

    [Fact]
    public async Task ExplicitWarmTarget_ColdToWarm_Works()
    {
        var (handler, store, _, _) = MakeHandler();
        store.Seed(Tier.Cold, ContentType.Knowledge, MakeEntry("c1"));

        var result = await handler.ExecuteAsync(
            ParseArgs("""{"id":"c1","tier":"warm"}"""), CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("cold", doc.RootElement.GetProperty("from_tier").GetString());
        Assert.Equal("warm", doc.RootElement.GetProperty("to_tier").GetString());
        Assert.Equal("knowledge", doc.RootElement.GetProperty("to_content_type").GetString());
    }

    [Fact]
    public async Task NotFound_Throws()
    {
        var (handler, _, _, _) = MakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(ParseArgs("""{"id":"missing"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task MissingId_Throws()
    {
        var (handler, _, _, _) = MakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(ParseArgs("""{}"""), CancellationToken.None));
    }

    [Fact]
    public async Task InvalidTier_Throws()
    {
        var (handler, store, _, _) = MakeHandler();
        store.Seed(Tier.Warm, ContentType.Memory, MakeEntry("w1"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(ParseArgs("""{"id":"w1","tier":"cold"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task DirectionGate_HotToHot_Throws()
    {
        var (handler, store, _, _) = MakeHandler();
        store.Seed(Tier.Hot, ContentType.Memory, MakeEntry("h1"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(ParseArgs("""{"id":"h1","tier":"hot"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task JsonRoundtrip_MatchesDto()
    {
        var (handler, store, _, _) = MakeHandler();
        store.Seed(Tier.Warm, ContentType.Memory, MakeEntry("w1"));
        var result = await handler.ExecuteAsync(ParseArgs("""{"id":"w1"}"""), CancellationToken.None);
        var dto = JsonSerializer.Deserialize(result.Content[0].Text, JsonContext.Default.MemoryMoveResultDto);
        Assert.NotNull(dto);
        Assert.Equal("w1", dto!.Id);
        Assert.True(dto.Success);
    }

    [Fact]
    public void Name_And_Schema_MatchWireContract()
    {
        var (handler, _, _, _) = MakeHandler();
        Assert.Equal("memory_promote", handler.Name);
        Assert.True(handler.InputSchema.TryGetProperty("properties", out var props));
        Assert.True(props.TryGetProperty("id", out _));
    }
}
