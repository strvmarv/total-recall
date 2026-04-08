// Plan 4 Task 4.8 — MemoryGetHandler contract tests. Uses the
// FakeSqliteStore's Seed/Get paths to place entries in specific
// (tier, type) slots and asserts the handler finds them and returns
// the TS wire shape.

using System;
using System.Linq;
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

public class MemoryGetHandlerTests
{
    private static (MemoryGetHandler handler, FakeSqliteStore store) MakeHandler()
    {
        var store = new FakeSqliteStore();
        var handler = new MemoryGetHandler(store);
        return (handler, store);
    }

    private static JsonElement ParseArgs(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static Entry MakeEntry(string id, string content = "hello") =>
        new(
            id,
            content,
            FSharpOption<string>.None,
            FSharpOption<string>.None,
            FSharpOption<SourceTool>.None,
            FSharpOption<string>.None,
            ListModule.OfSeq(Array.Empty<string>()),
            1L, 2L, 3L, 4, 0.5,
            FSharpOption<string>.None,
            FSharpOption<string>.None,
            "{}");

    [Fact]
    public async Task HappyPath_Found_ReturnsEntryWithLocation()
    {
        var (handler, store) = MakeHandler();
        store.Seed(Tier.Hot, ContentType.Memory, MakeEntry("abc", "hi"));

        var result = await handler.ExecuteAsync(ParseArgs("""{"id":"abc"}"""), CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        Assert.Single(result.Content);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("hot", doc.RootElement.GetProperty("tier").GetString());
        Assert.Equal("memory", doc.RootElement.GetProperty("content_type").GetString());
        Assert.Equal("abc", doc.RootElement.GetProperty("entry").GetProperty("id").GetString());
        Assert.Equal("hi", doc.RootElement.GetProperty("entry").GetProperty("content").GetString());
    }

    [Fact]
    public async Task NotFound_ReturnsNull()
    {
        var (handler, _) = MakeHandler();
        var result = await handler.ExecuteAsync(ParseArgs("""{"id":"missing"}"""), CancellationToken.None);
        Assert.Equal("null", result.Content[0].Text);
    }

    [Fact]
    public async Task IteratesAllTablePairs_UntilFound()
    {
        var (handler, store) = MakeHandler();
        // Put row in Cold/Knowledge — last in the iteration order.
        store.Seed(Tier.Cold, ContentType.Knowledge, MakeEntry("zz"));

        var result = await handler.ExecuteAsync(ParseArgs("""{"id":"zz"}"""), CancellationToken.None);

        // All 6 pairs should have been tried (the match is the 6th).
        Assert.Equal(6, store.GetCalls.Count);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("cold", doc.RootElement.GetProperty("tier").GetString());
        Assert.Equal("knowledge", doc.RootElement.GetProperty("content_type").GetString());
    }

    [Fact]
    public async Task NullArguments_Throws()
    {
        var (handler, _) = MakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task MissingId_Throws()
    {
        var (handler, _) = MakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(ParseArgs("""{}"""), CancellationToken.None));
    }

    [Fact]
    public async Task EmptyId_Throws()
    {
        var (handler, _) = MakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(ParseArgs("""{"id":""}"""), CancellationToken.None));
    }

    [Fact]
    public async Task FoundInWarmKnowledge_CorrectLocationReturned()
    {
        var (handler, store) = MakeHandler();
        store.Seed(Tier.Warm, ContentType.Knowledge, MakeEntry("wk"));

        var result = await handler.ExecuteAsync(ParseArgs("""{"id":"wk"}"""), CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("warm", doc.RootElement.GetProperty("tier").GetString());
        Assert.Equal("knowledge", doc.RootElement.GetProperty("content_type").GetString());
    }

    [Fact]
    public async Task JsonShape_MatchesTsFormat()
    {
        var (handler, store) = MakeHandler();
        store.Seed(Tier.Hot, ContentType.Memory, MakeEntry("abc"));

        var result = await handler.ExecuteAsync(ParseArgs("""{"id":"abc"}"""), CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(s => s).ToArray();
        Assert.Equal(new[] { "content_type", "entry", "tier" }, keys);
    }

    [Fact]
    public void Name_And_Schema_MatchWireContract()
    {
        var (handler, _) = MakeHandler();
        Assert.Equal("memory_get", handler.Name);
        Assert.Equal("Retrieve a specific memory entry by ID", handler.Description);
        Assert.True(handler.InputSchema.TryGetProperty("properties", out var props));
        Assert.True(props.TryGetProperty("id", out _));
    }
}
