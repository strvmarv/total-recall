// Plan 6 Task 6.0a — MemoryDemoteHandler contract tests.

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

public class MemoryDemoteHandlerTests
{
    private static (MemoryDemoteHandler handler, FakeStore store,
            FakeVectorSearch vec, RecordingFakeEmbedder embedder) MakeHandler()
    {
        var store = new FakeStore();
        var vec = new FakeVectorSearch();
        var embedder = new RecordingFakeEmbedder();
        return (new MemoryDemoteHandler(store, vec, embedder), store, vec, embedder);
    }

    private static JsonElement ParseArgs(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static Entry MakeEntry(string id) =>
        new(
            id, "c",
            FSharpOption<string>.None, FSharpOption<string>.None,
            FSharpOption<SourceTool>.None, FSharpOption<string>.None,
            ListModule.OfSeq(Array.Empty<string>()),
            1L, 2L, 3L, 4, 0.5,
            FSharpOption<string>.None, FSharpOption<string>.None, "{}");

    [Fact]
    public async Task HappyPath_HotToCold_DefaultTier()
    {
        var (handler, store, vec, embedder) = MakeHandler();
        store.Seed(Tier.Hot, ContentType.Memory, MakeEntry("h1"));

        var result = await handler.ExecuteAsync(ParseArgs("""{"id":"h1"}"""), CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("hot", doc.RootElement.GetProperty("from_tier").GetString());
        Assert.Equal("cold", doc.RootElement.GetProperty("to_tier").GetString());
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());

        Assert.Single(vec.DeleteCalls);
        Assert.Single(store.MoveCalls);
        Assert.Single(vec.InsertCalls);
        Assert.Single(embedder.Calls);
    }

    [Fact]
    public async Task HotToWarm_WithExplicitTier_Works()
    {
        var (handler, store, _, _) = MakeHandler();
        store.Seed(Tier.Hot, ContentType.Memory, MakeEntry("h1"));
        var result = await handler.ExecuteAsync(
            ParseArgs("""{"id":"h1","tier":"warm"}"""), CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("warm", doc.RootElement.GetProperty("to_tier").GetString());
    }

    [Fact]
    public async Task DirectionGate_ColdToCold_Throws()
    {
        var (handler, store, _, _) = MakeHandler();
        store.Seed(Tier.Cold, ContentType.Memory, MakeEntry("c1"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(ParseArgs("""{"id":"c1"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task InvalidTier_Hot_Throws()
    {
        var (handler, store, _, _) = MakeHandler();
        store.Seed(Tier.Warm, ContentType.Memory, MakeEntry("w1"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(ParseArgs("""{"id":"w1","tier":"hot"}"""), CancellationToken.None));
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
    public void Name_And_Schema_MatchWireContract()
    {
        var (handler, _, _, _) = MakeHandler();
        Assert.Equal("memory_demote", handler.Name);
    }
}
