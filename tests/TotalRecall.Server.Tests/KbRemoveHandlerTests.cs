// Plan 6 Task 6.0b — KbRemoveHandler contract tests.

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

public class KbRemoveHandlerTests
{
    private static (KbRemoveHandler handler, FakeStore store, FakeVectorSearch vec) MakeHandler()
    {
        var store = new FakeStore();
        var vec = new FakeVectorSearch();
        return (new KbRemoveHandler(store, vec), store, vec);
    }

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static Entry MakeEntry(
        string id,
        string? parentId = null,
        string? collectionId = null) =>
        new(
            id, "content-" + id,
            FSharpOption<string>.None, FSharpOption<string>.None,
            FSharpOption<SourceTool>.None, FSharpOption<string>.None,
            ListModule.OfSeq(Array.Empty<string>()),
            100L, 200L, 300L, 0, 0.5,
            parentId is null ? FSharpOption<string>.None : FSharpOption<string>.Some(parentId),
            collectionId is null ? FSharpOption<string>.None : FSharpOption<string>.Some(collectionId),
            "{}");

    [Fact]
    public async Task HappyPath_Cascade_DeletesChildrenAndRoot()
    {
        var (handler, store, vec) = MakeHandler();
        var root = MakeEntry("root", collectionId: "root");
        var doc1 = MakeEntry("doc1", parentId: "root", collectionId: "root");
        var doc2 = MakeEntry("doc2", parentId: "root", collectionId: "root");
        var unrelated = MakeEntry("other", collectionId: "other");
        store.Seed(Tier.Cold, ContentType.Knowledge, root);
        store.SeedList(Tier.Cold, ContentType.Knowledge, root, doc1, doc2, unrelated);

        var result = await handler.ExecuteAsync(
            Args("""{"id":"root"}"""),
            CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("root", doc.RootElement.GetProperty("id").GetString());
        Assert.True(doc.RootElement.GetProperty("removed").GetBoolean());
        Assert.Equal(2, doc.RootElement.GetProperty("cascaded_count").GetInt32());

        // Both children + root deleted; unrelated row left alone.
        var deletedIds = store.DeleteCalls.Select(c => c.Id).ToList();
        Assert.Contains("doc1", deletedIds);
        Assert.Contains("doc2", deletedIds);
        Assert.Contains("root", deletedIds);
        Assert.DoesNotContain("other", deletedIds);

        // Three vec deletes (root + 2 children); synthetic rowid ordering
        // depends on Seed/SeedList interleaving so don't pin specific values.
        Assert.Equal(3, vec.DeleteCalls.Count);
    }

    [Fact]
    public async Task LeafEntry_CascadeCountZero()
    {
        var (handler, store, _) = MakeHandler();
        // Single chunk with a parent that doesn't exist as a separate row in
        // the store. Removing it should not cascade anything.
        var leaf = MakeEntry("leaf", parentId: "ghost", collectionId: "ghost");
        store.Seed(Tier.Cold, ContentType.Knowledge, leaf);
        store.SeedList(Tier.Cold, ContentType.Knowledge, leaf);

        var result = await handler.ExecuteAsync(
            Args("""{"id":"leaf"}"""),
            CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(0, doc.RootElement.GetProperty("cascaded_count").GetInt32());
    }

    [Fact]
    public async Task MissingId_Throws()
    {
        var (handler, _, _) = MakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(Args("""{}"""), CancellationToken.None));
    }

    [Fact]
    public async Task NotFound_Throws()
    {
        var (handler, _, _) = MakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(Args("""{"id":"nope"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task JsonRoundtrip_MatchesDto()
    {
        var (handler, store, _) = MakeHandler();
        var leaf = MakeEntry("x");
        store.Seed(Tier.Cold, ContentType.Knowledge, leaf);
        store.SeedList(Tier.Cold, ContentType.Knowledge, leaf);

        var result = await handler.ExecuteAsync(
            Args("""{"id":"x"}"""),
            CancellationToken.None);

        var dto = JsonSerializer.Deserialize(result.Content[0].Text, JsonContext.Default.KbRemoveResultDto);
        Assert.NotNull(dto);
        Assert.Equal("x", dto!.Id);
        Assert.True(dto.Removed);
        Assert.Equal(0, dto.CascadedCount);
    }

    [Fact]
    public void Name_MatchesWireContract()
    {
        var (handler, _, _) = MakeHandler();
        Assert.Equal("kb_remove", handler.Name);
    }
}
