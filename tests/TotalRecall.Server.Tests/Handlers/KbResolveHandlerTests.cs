// tests/TotalRecall.Server.Tests/Handlers/KbResolveHandlerTests.cs
//
// Phase 3 idea 2c — TDD tests for KbResolveHandler.
// Covers: known path, unknown path, missing path arg, and duplicate-document
// tie-breaking (pick newest by CreatedAt).

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

namespace TotalRecall.Server.Tests.Handlers;

public sealed class KbResolveHandlerTests
{
    private static (KbResolveHandler handler, FakeStore store) MakeHandler()
    {
        var store = new FakeStore();
        return (new KbResolveHandler(store), store);
    }

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>
    /// Build an Entry with explicit ParentId and CollectionId for KB tree testing.
    /// Field order: id, content, summary, source, sourceTool, project, tags,
    /// createdAt, updatedAt, lastAccessedAt, accessCount, decayScore,
    /// parentId, collectionId, scope, entryType, metadataJson, timesInjected.
    /// </summary>
    private static Entry MakeEntry(
        string id,
        string content = "content",
        string? source = null,
        string? parentId = null,
        string? collectionId = null,
        long createdAt = 1000L) =>
        new(
            id,
            content,
            FSharpOption<string>.None,
            source is null ? FSharpOption<string>.None : FSharpOption<string>.Some(source),
            FSharpOption<SourceTool>.None,
            FSharpOption<string>.None,
            ListModule.OfSeq(Array.Empty<string>()),
            createdAt, createdAt, createdAt, 0, 0.5,
            parentId is null ? FSharpOption<string>.None : FSharpOption<string>.Some(parentId),
            collectionId is null ? FSharpOption<string>.None : FSharpOption<string>.Some(collectionId),
            "", EntryType.Ingested, "{}", 0);

    [Fact]
    public async Task Resolve_KnownPath_ReturnsChunks()
    {
        var (handler, store) = MakeHandler();

        // Seed: one collection row (no collectionId, no parentId), one document,
        // two chunks hanging off the document.
        var col = MakeEntry("col1", source: "/repo");
        var doc = MakeEntry("doc1", source: "/repo/notes.md", collectionId: "col1");
        var chunk1 = MakeEntry("chunk1", content: "first chunk content",
            source: "/repo/notes.md", parentId: "doc1", collectionId: "col1", createdAt: 1001L);
        var chunk2 = MakeEntry("chunk2", content: "second chunk content",
            source: "/repo/notes.md", parentId: "doc1", collectionId: "col1", createdAt: 1002L);

        store.SeedList(Tier.Cold, ContentType.Knowledge, col, doc, chunk1, chunk2);

        var result = await handler.ExecuteAsync(
            Args("""{"path":"/repo/notes.md"}"""),
            CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        using var payload = JsonDocument.Parse(result.Content[0].Text);
        var root = payload.RootElement;

        Assert.True(root.GetProperty("found").GetBoolean());
        Assert.Equal("doc1", root.GetProperty("documentId").GetString());
        Assert.Equal("col1", root.GetProperty("collectionId").GetString());
        Assert.Equal(2, root.GetProperty("chunkCount").GetInt32());
        Assert.Equal("first chunk content", root.GetProperty("chunks")[0].GetProperty("content").GetString());
        Assert.True(root.GetProperty("tokenEstimate").GetInt32() > 0);
    }

    [Fact]
    public async Task Resolve_UnknownPath_ReturnsFoundFalse()
    {
        var (handler, _) = MakeHandler();

        var result = await handler.ExecuteAsync(
            Args("""{"path":"/nope.md"}"""),
            CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        using var payload = JsonDocument.Parse(result.Content[0].Text);
        var root = payload.RootElement;

        Assert.False(root.GetProperty("found").GetBoolean());
        Assert.Equal(0, root.GetProperty("chunkCount").GetInt32());
    }

    [Fact]
    public async Task Resolve_MissingPath_Throws()
    {
        var (handler, _) = MakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(Args("""{}"""), CancellationToken.None));
    }

    [Fact]
    public async Task Resolve_DuplicateDocuments_PicksNewest()
    {
        var (handler, store) = MakeHandler();

        // Two document rows for the same source path, different createdAt.
        var docOld = MakeEntry("docOld", source: "/repo/dup.md",
            collectionId: "col1", createdAt: 1000L);
        var docNew = MakeEntry("docNew", source: "/repo/dup.md",
            collectionId: "col1", createdAt: 2000L);
        // One chunk under docNew only.
        var chunk = MakeEntry("chunk1", content: "newest chunk",
            source: "/repo/dup.md", parentId: "docNew", collectionId: "col1");

        store.SeedList(Tier.Cold, ContentType.Knowledge, docOld, docNew, chunk);

        var result = await handler.ExecuteAsync(
            Args("""{"path":"/repo/dup.md"}"""),
            CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        using var payload = JsonDocument.Parse(result.Content[0].Text);
        var root = payload.RootElement;

        Assert.True(root.GetProperty("found").GetBoolean());
        Assert.Equal("docNew", root.GetProperty("documentId").GetString());
    }
}
