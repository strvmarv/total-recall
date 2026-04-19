// Plan 4 Task 4.8 — MemoryUpdateHandler contract tests.

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

public class MemoryUpdateHandlerTests
{
    private static (MemoryUpdateHandler handler, FakeStore store, RecordingFakeEmbedder embedder, FakeVectorSearch vector)
        MakeHandler()
    {
        var store = new FakeStore();
        var embedder = new RecordingFakeEmbedder();
        var vector = new FakeVectorSearch();
        var handler = new MemoryUpdateHandler(store, embedder, vector);
        return (handler, store, embedder, vector);
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
            FSharpOption<string>.None, FSharpOption<string>.None, "", EntryType.Preference, "{}");

    [Fact]
    public async Task HappyPath_ContentAndFields_UpdatedAndReEmbedded()
    {
        var (handler, store, embedder, vector) = MakeHandler();
        store.Seed(Tier.Hot, ContentType.Memory, MakeEntry("abc", "old"));

        var result = await handler.ExecuteAsync(
            ParseArgs("""{"id":"abc","content":"new","project":"p","tags":["t1"]}"""),
            CancellationToken.None);

        Assert.Equal("{\"updated\":true}", result.Content[0].Text);
        Assert.Single(store.UpdateCalls);
        var call = store.UpdateCalls[0];
        Assert.Equal(Tier.Hot, call.Tier);
        Assert.Equal(ContentType.Memory, call.Type);
        Assert.Equal("abc", call.Id);
        Assert.Equal("new", call.Opts.Content);
        Assert.Equal("p", call.Opts.Project);
        Assert.NotNull(call.Opts.Tags);

        // Re-embed path.
        Assert.Single(embedder.Calls);
        Assert.Equal("new", embedder.Calls[0]);
        Assert.Single(vector.DeleteCalls);
        Assert.Single(vector.InsertCalls);
        // abc is seeded first → synthetic rowid 1.
        Assert.Equal(1L, vector.DeleteCalls[0].Rowid);
        Assert.Equal("abc", vector.InsertCalls[0].EntryId);
    }

    [Fact]
    public async Task WithoutContent_OnlyNonEmbedFieldsUpdated_NoReEmbed()
    {
        var (handler, store, embedder, vector) = MakeHandler();
        store.Seed(Tier.Hot, ContentType.Memory, MakeEntry("abc"));

        var result = await handler.ExecuteAsync(
            ParseArgs("""{"id":"abc","summary":"s","project":"p"}"""),
            CancellationToken.None);

        Assert.Equal("{\"updated\":true}", result.Content[0].Text);
        Assert.Single(store.UpdateCalls);
        Assert.Null(store.UpdateCalls[0].Opts.Content);
        Assert.Equal("s", store.UpdateCalls[0].Opts.Summary);

        Assert.Empty(embedder.Calls);
        Assert.Empty(vector.InsertCalls);
        Assert.Empty(vector.DeleteCalls);
    }

    [Fact]
    public async Task NotFound_ReturnsUpdatedFalse()
    {
        var (handler, store, embedder, vector) = MakeHandler();

        var result = await handler.ExecuteAsync(
            ParseArgs("""{"id":"missing","content":"x"}"""),
            CancellationToken.None);

        Assert.Equal("{\"updated\":false}", result.Content[0].Text);
        Assert.Empty(store.UpdateCalls);
        Assert.Empty(embedder.Calls);
        Assert.Empty(vector.InsertCalls);
    }

    [Fact]
    public async Task NullArguments_Throws()
    {
        var (handler, _, _, _) = MakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task MissingId_Throws()
    {
        var (handler, _, _, _) = MakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(ParseArgs("""{"content":"x"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task EmptyContent_Throws()
    {
        var (handler, _, _, _) = MakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(ParseArgs("""{"id":"a","content":""}"""), CancellationToken.None));
    }

    [Fact]
    public async Task OversizeContent_Throws()
    {
        var (handler, _, _, _) = MakeHandler();
        var huge = new string('x', 100_001);
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(ParseArgs($$"""{"id":"a","content":"{{huge}}"}"""), CancellationToken.None));
        Assert.Contains("100000", ex.Message);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var (handler, store, _, _) = MakeHandler();
        store.Seed(Tier.Hot, ContentType.Memory, MakeEntry("abc"));
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => handler.ExecuteAsync(ParseArgs("""{"id":"abc","content":"new"}"""), cts.Token));
        Assert.Empty(store.UpdateCalls);
    }

    [Fact]
    public void Name_And_Schema_MatchWireContract()
    {
        var (handler, _, _, _) = MakeHandler();
        Assert.Equal("memory_update", handler.Name);
        Assert.Equal("Update an existing memory entry", handler.Description);
        Assert.True(handler.InputSchema.TryGetProperty("properties", out var props));
        Assert.True(props.TryGetProperty("id", out _));
        Assert.True(props.TryGetProperty("content", out _));
    }
}
