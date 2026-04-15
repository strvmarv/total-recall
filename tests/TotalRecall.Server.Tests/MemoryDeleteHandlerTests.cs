// Plan 4 Task 4.8 — MemoryDeleteHandler contract tests.

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

public class MemoryDeleteHandlerTests
{
    private static (MemoryDeleteHandler handler, FakeStore store, FakeVectorSearch vector)
        MakeHandler()
    {
        var store = new FakeStore();
        var vector = new FakeVectorSearch();
        var handler = new MemoryDeleteHandler(store, vector);
        return (handler, store, vector);
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
            FSharpOption<string>.None, FSharpOption<string>.None, "", "{}");

    [Fact]
    public async Task HappyPath_Found_ReturnsDeletedTrue()
    {
        var (handler, store, vector) = MakeHandler();
        store.Seed(Tier.Hot, ContentType.Memory, MakeEntry("abc"));

        var result = await handler.ExecuteAsync(ParseArgs("""{"id":"abc"}"""), CancellationToken.None);

        Assert.Equal("{\"deleted\":true}", result.Content[0].Text);
        Assert.Single(store.DeleteCalls);
        Assert.Equal(Tier.Hot, store.DeleteCalls[0].Tier);
        Assert.Equal(ContentType.Memory, store.DeleteCalls[0].Type);
        Assert.Equal("abc", store.DeleteCalls[0].Id);

        Assert.Single(vector.DeleteCalls);
        // FakeStore assigns rowid 1 to the first Seed.
        Assert.Equal(1L, vector.DeleteCalls[0].Rowid);
    }

    [Fact]
    public async Task NotFound_ReturnsDeletedFalse()
    {
        var (handler, store, vector) = MakeHandler();

        var result = await handler.ExecuteAsync(ParseArgs("""{"id":"missing"}"""), CancellationToken.None);

        Assert.Equal("{\"deleted\":false}", result.Content[0].Text);
        Assert.Empty(store.DeleteCalls);
        Assert.Empty(vector.DeleteCalls);
    }

    [Fact]
    public async Task IteratesAllTablePairs_FoundInWarmMemory()
    {
        var (handler, store, vector) = MakeHandler();
        store.Seed(Tier.Warm, ContentType.Memory, MakeEntry("w"));

        var result = await handler.ExecuteAsync(ParseArgs("""{"id":"w"}"""), CancellationToken.None);

        Assert.Equal("{\"deleted\":true}", result.Content[0].Text);
        Assert.Equal(Tier.Warm, store.DeleteCalls[0].Tier);
        Assert.Equal(ContentType.Memory, store.DeleteCalls[0].Type);
        Assert.Equal(Tier.Warm, vector.DeleteCalls[0].Tier);
    }

    [Fact]
    public async Task NullArguments_Throws()
    {
        var (handler, _, _) = MakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task MissingId_Throws()
    {
        var (handler, _, _) = MakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(ParseArgs("""{}"""), CancellationToken.None));
    }

    [Fact]
    public async Task EmptyId_Throws()
    {
        var (handler, _, _) = MakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(ParseArgs("""{"id":""}"""), CancellationToken.None));
    }

    [Fact]
    public async Task Delete_CallsVecDeleteBeforeStoreDelete()
    {
        // Regression for the orphan-vec-row bug (2026-04-08). The previous
        // implementation called store.Delete first and then
        // vec.DeleteEmbedding, but VectorSearch.DeleteEmbedding resolves
        // the vec rowid via the *content* table — so by the time it runs,
        // the content row is gone, ResolveRowid returns null, and the vec
        // row is silently left behind as an orphan. Subsequent inserts
        // that reuse the freed rowid then crash with a UNIQUE constraint
        // violation on the vec table's primary key.
        //
        // The handler's contract is: vec.DeleteEmbedding MUST run before
        // store.Delete so the rowid lookup still succeeds.
        var (handler, store, vector) = MakeHandler();
        var log = new System.Collections.Generic.List<string>();
        store.OrderLog = log;
        vector.OrderLog = log;
        store.Seed(Tier.Hot, ContentType.Memory, MakeEntry("abc"));

        await handler.ExecuteAsync(ParseArgs("""{"id":"abc"}"""), CancellationToken.None);

        Assert.Equal(new[] { "vec.DeleteEmbedding", "store.Delete" }, log);
    }

    [Fact]
    public async Task CallsDeleteEmbedding_WhenFound()
    {
        var (handler, store, vector) = MakeHandler();
        store.Seed(Tier.Cold, ContentType.Knowledge, MakeEntry("ck"));

        await handler.ExecuteAsync(ParseArgs("""{"id":"ck"}"""), CancellationToken.None);

        Assert.Single(vector.DeleteCalls);
        Assert.Equal(Tier.Cold, vector.DeleteCalls[0].Tier);
        Assert.Equal(ContentType.Knowledge, vector.DeleteCalls[0].Type);
    }

    [Fact]
    public void Name_And_Schema_MatchWireContract()
    {
        var (handler, _, _) = MakeHandler();
        Assert.Equal("memory_delete", handler.Name);
        Assert.Equal("Delete a memory entry by ID", handler.Description);
        Assert.True(handler.InputSchema.TryGetProperty("properties", out var props));
        Assert.True(props.TryGetProperty("id", out _));
    }
}
