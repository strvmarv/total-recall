// Plan 6 Task 6.0b — KbListCollectionsHandler contract tests.

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

public class KbListCollectionsHandlerTests
{
    private static (KbListCollectionsHandler handler, FakeStore store) MakeHandler()
    {
        var store = new FakeStore();
        return (new KbListCollectionsHandler(store), store);
    }

    private static Entry MakeEntry(
        string id,
        string? metadataJson = "{}",
        string? parentId = null,
        string? collectionId = null,
        string? summary = null) =>
        new(
            id, "content-" + id,
            summary is null ? FSharpOption<string>.None : FSharpOption<string>.Some(summary),
            FSharpOption<string>.None,
            FSharpOption<SourceTool>.None,
            FSharpOption<string>.None,
            ListModule.OfSeq(Array.Empty<string>()),
            100L, 200L, 300L, 0, 0.5,
            parentId is null ? FSharpOption<string>.None : FSharpOption<string>.Some(parentId),
            collectionId is null ? FSharpOption<string>.None : FSharpOption<string>.Some(collectionId),
            "",
            EntryType.Preference,
            metadataJson ?? "{}");

    [Fact]
    public async Task HappyPath_ReturnsCollectionsWithCounts()
    {
        var (handler, store) = MakeHandler();

        // Two collection roots, each with chunks + a doc.
        var c1 = MakeEntry(
            "coll-1",
            metadataJson: """{"type":"collection","name":"docs","source_path":"/tmp/docs"}""",
            collectionId: "coll-1",
            summary: "the docs collection");
        var c2 = MakeEntry(
            "coll-2",
            metadataJson: """{"type":"collection","name":"notes"}""",
            collectionId: "coll-2");

        store.SeedListByMetadata(
            Tier.Cold,
            ContentType.Knowledge,
            new System.Collections.Generic.Dictionary<string, string> { ["type"] = "collection" },
            c1, c2);

        // Documents (no parent) and chunks (with parent) for c1; one document for c2.
        var doc1 = MakeEntry("doc-1", collectionId: "coll-1");
        var chunk1 = MakeEntry("chunk-1", parentId: "doc-1", collectionId: "coll-1");
        var chunk2 = MakeEntry("chunk-2", parentId: "doc-1", collectionId: "coll-1");
        var doc2 = MakeEntry("doc-2", collectionId: "coll-2");
        store.SeedList(Tier.Cold, ContentType.Knowledge, c1, c2, doc1, chunk1, chunk2, doc2);

        var result = await handler.ExecuteAsync(null, CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Content[0].Text);

        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
        var collections = doc.RootElement.GetProperty("collections");
        Assert.Equal(2, collections.GetArrayLength());

        // c1: doc1 is a doc (no parent), chunks 1+2 are chunks. The collection
        // root c1 itself has CollectionId == self and ParentId == null, so it
        // counts as a doc too — matches CLI semantics (one extra "document").
        var first = collections[0];
        Assert.Equal("coll-1", first.GetProperty("id").GetString());
        Assert.Equal("docs", first.GetProperty("name").GetString());
        Assert.Equal(2, first.GetProperty("document_count").GetInt32());
        Assert.Equal(2, first.GetProperty("chunk_count").GetInt32());
        Assert.Equal("/tmp/docs", first.GetProperty("source_path").GetString());
        Assert.Equal("the docs collection", first.GetProperty("summary").GetString());

        var second = collections[1];
        Assert.Equal("coll-2", second.GetProperty("id").GetString());
        Assert.Equal("notes", second.GetProperty("name").GetString());
        Assert.Equal(2, second.GetProperty("document_count").GetInt32());
        Assert.Equal(0, second.GetProperty("chunk_count").GetInt32());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("source_path").ValueKind);
    }

    [Fact]
    public async Task EmptyStore_ReturnsZero()
    {
        var (handler, _) = MakeHandler();
        var result = await handler.ExecuteAsync(null, CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(0, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("collections").GetArrayLength());
    }

    [Fact]
    public async Task UnnamedCollection_FallsBackToPlaceholder()
    {
        var (handler, store) = MakeHandler();
        var c = MakeEntry("c1", metadataJson: """{"type":"collection"}""", collectionId: "c1");
        store.SeedListByMetadata(
            Tier.Cold,
            ContentType.Knowledge,
            new System.Collections.Generic.Dictionary<string, string> { ["type"] = "collection" },
            c);
        store.SeedList(Tier.Cold, ContentType.Knowledge, c);

        var result = await handler.ExecuteAsync(null, CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("(unnamed)", doc.RootElement.GetProperty("collections")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task JsonRoundtrip_MatchesDto()
    {
        var (handler, store) = MakeHandler();
        var c = MakeEntry("cX", metadataJson: """{"type":"collection","name":"alpha"}""", collectionId: "cX");
        store.SeedListByMetadata(
            Tier.Cold,
            ContentType.Knowledge,
            new System.Collections.Generic.Dictionary<string, string> { ["type"] = "collection" },
            c);
        store.SeedList(Tier.Cold, ContentType.Knowledge, c);

        var result = await handler.ExecuteAsync(null, CancellationToken.None);
        var dto = JsonSerializer.Deserialize(result.Content[0].Text, JsonContext.Default.KbListCollectionsResultDto);
        Assert.NotNull(dto);
        Assert.Equal(1, dto!.Count);
        Assert.Equal("cX", dto.Collections[0].Id);
        Assert.Equal("alpha", dto.Collections[0].Name);
    }

    [Fact]
    public void Name_MatchesWireContract()
    {
        var (handler, _) = MakeHandler();
        Assert.Equal("kb_list_collections", handler.Name);
    }
}
