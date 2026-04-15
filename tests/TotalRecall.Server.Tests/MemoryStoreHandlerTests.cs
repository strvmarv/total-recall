// Plan 4 Task 4.6 — MemoryStoreHandler contract tests. The Plan 1 seed
// test originally lived here as a single [Fact(Skip = ...)] placeholder;
// that skip is removed now that the handler exists. These tests isolate
// the handler from real SQLite / ONNX / vec0 by using the lightweight
// fakes in TestSupport/.

using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Core;
using TotalRecall.Server.Handlers;
using TotalRecall.Server.Tests.TestSupport;
using Xunit;

namespace TotalRecall.Server.Tests;

public class MemoryStoreHandlerTests
{
    private static (MemoryStoreHandler handler, FakeStore store, RecordingFakeEmbedder embedder, FakeVectorSearch vector)
        MakeHandler(string? id = null)
    {
        var store = new FakeStore();
        if (id is not null) store.NextInsertId = id;
        var embedder = new RecordingFakeEmbedder();
        var vector = new FakeVectorSearch();
        var handler = new MemoryStoreHandler(store, embedder, vector);
        return (handler, store, embedder, vector);
    }

    private static JsonElement ParseArgs(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task HappyPath_ReturnsSuccessResponseWithEntryId()
    {
        var (handler, store, embedder, vector) = MakeHandler("entry-123");
        var args = ParseArgs("""{"content":"hello world","tier":"hot"}""");

        var result = await handler.ExecuteAsync(args, CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        Assert.NotNull(result.Content);
        Assert.Single(result.Content);
        Assert.Equal("text", result.Content[0].Type);
        Assert.Contains("entry-123", result.Content[0].Text);
        Assert.Equal("{\"id\":\"entry-123\"}", result.Content[0].Text);

        Assert.Single(store.InsertWithEmbeddingCalls);
        var call = store.InsertWithEmbeddingCalls[0];
        Assert.Equal(Tier.Hot, call.Tier);
        Assert.Equal(ContentType.Memory, call.Type);
        Assert.Equal("hello world", call.Opts.Content);
        Assert.Equal(384, call.Embedding.Length);

        Assert.Single(embedder.Calls);
        Assert.Equal("hello world", embedder.Calls[0]);

        // Transactional insert path: store.Insert is NOT called, and
        // vec.InsertEmbedding is NOT called — both happen inside
        // store.InsertWithEmbedding.
        Assert.Empty(store.InsertCalls);
        Assert.Empty(vector.InsertCalls);
    }

    [Fact]
    public async Task ExecuteAsync_WithAllOptionalFields_PopulatesInsertOpts()
    {
        var (handler, store, _, _) = MakeHandler("entry-all");
        var args = ParseArgs("""
            {
              "content": "full payload",
              "tier": "warm",
              "contentType": "knowledge",
              "entryType": "correction",
              "project": "foo",
              "tags": ["a","b"],
              "source": "manual"
            }
            """);

        await handler.ExecuteAsync(args, CancellationToken.None);

        var call = Assert.Single(store.InsertWithEmbeddingCalls);
        Assert.Equal(Tier.Warm, call.Tier);
        Assert.Equal(ContentType.Knowledge, call.Type);
        Assert.Equal("full payload", call.Opts.Content);
        Assert.Equal("foo", call.Opts.Project);
        Assert.Equal("manual", call.Opts.Source);
        Assert.NotNull(call.Opts.Tags);
        Assert.Equal(new[] { "a", "b" }, call.Opts.Tags!.ToArray());
        Assert.Equal("{\"entry_type\":\"correction\"}", call.Opts.MetadataJson);
    }

    [Fact]
    public async Task ExecuteAsync_DefaultsTierToHot_TypeToMemory()
    {
        var (handler, store, _, _) = MakeHandler();
        var args = ParseArgs("""{"content":"no tier no type"}""");

        await handler.ExecuteAsync(args, CancellationToken.None);

        var call = Assert.Single(store.InsertWithEmbeddingCalls);
        Assert.Equal(Tier.Hot, call.Tier);
        Assert.Equal(ContentType.Memory, call.Type);
        Assert.Null(call.Opts.MetadataJson);
    }

    [Fact]
    public async Task ExecuteAsync_NullArguments_Throws()
    {
        var (handler, _, _, _) = MakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_EmptyContent_Throws()
    {
        var (handler, _, _, _) = MakeHandler();
        var args = ParseArgs("""{"content":""}""");
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(args, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_InvalidTier_Throws()
    {
        var (handler, _, _, _) = MakeHandler();
        var args = ParseArgs("""{"content":"x","tier":"lukewarm"}""");
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(args, CancellationToken.None));
        Assert.Contains("lukewarm", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidContentType_Throws()
    {
        var (handler, _, _, _) = MakeHandler();
        var args = ParseArgs("""{"content":"x","contentType":"knowledgebase"}""");
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(args, CancellationToken.None));
        Assert.Contains("knowledgebase", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidEntryType_Throws()
    {
        var (handler, _, _, _) = MakeHandler();
        var args = ParseArgs("""{"content":"x","entryType":"opinion"}""");
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(args, CancellationToken.None));
        Assert.Contains("opinion", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_EmbedCalledWithContent()
    {
        var (handler, _, embedder, _) = MakeHandler();
        var args = ParseArgs("""{"content":"exact content string"}""");

        await handler.ExecuteAsync(args, CancellationToken.None);

        var call = Assert.Single(embedder.Calls);
        Assert.Equal("exact content string", call);
    }

    [Fact]
    public async Task ExecuteAsync_Cancellation_Propagates()
    {
        var (handler, store, _, _) = MakeHandler();
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var args = ParseArgs("""{"content":"x"}""");

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => handler.ExecuteAsync(args, cts.Token));
        Assert.Empty(store.InsertCalls);
        Assert.Empty(store.InsertWithEmbeddingCalls);
    }

    [Fact]
    public async Task ExecuteAsync_ContentTooLong_Throws()
    {
        var (handler, _, _, _) = MakeHandler();
        var huge = new string('x', 100_001);
        var args = ParseArgs($$"""{"content":"{{huge}}"}""");
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(args, CancellationToken.None));
        Assert.Contains("100000", ex.Message);
    }

    [Fact]
    public void Name_And_Schema_MatchWireContract()
    {
        var (handler, _, _, _) = MakeHandler();
        Assert.Equal("memory_store", handler.Name);
        Assert.Equal("Store a new memory or knowledge entry", handler.Description);
        Assert.Equal(JsonValueKind.Object, handler.InputSchema.ValueKind);
        Assert.True(handler.InputSchema.TryGetProperty("properties", out var props));
        Assert.True(props.TryGetProperty("content", out _));
    }

    [Fact]
    public async Task Visibility_Private_NotStoredInMetadata()
    {
        var (handler, store, _, _) = MakeHandler();
        var args = ParseArgs("""{"content":"x","visibility":"private"}""");

        await handler.ExecuteAsync(args, CancellationToken.None);

        var call = Assert.Single(store.InsertWithEmbeddingCalls);
        // "private" is the default — omitted from metadata to keep it lean.
        Assert.Null(call.Opts.MetadataJson);
    }

    [Fact]
    public async Task Visibility_Team_AppearsInMetadata()
    {
        var (handler, store, _, _) = MakeHandler();
        var args = ParseArgs("""{"content":"x","visibility":"team"}""");

        await handler.ExecuteAsync(args, CancellationToken.None);

        var call = Assert.Single(store.InsertWithEmbeddingCalls);
        Assert.NotNull(call.Opts.MetadataJson);
        using var doc = JsonDocument.Parse(call.Opts.MetadataJson!);
        Assert.Equal("team", doc.RootElement.GetProperty("visibility").GetString());
    }

    [Fact]
    public async Task Visibility_Public_AppearsInMetadata()
    {
        var (handler, store, _, _) = MakeHandler();
        var args = ParseArgs("""{"content":"x","visibility":"public"}""");

        await handler.ExecuteAsync(args, CancellationToken.None);

        var call = Assert.Single(store.InsertWithEmbeddingCalls);
        Assert.NotNull(call.Opts.MetadataJson);
        using var doc = JsonDocument.Parse(call.Opts.MetadataJson!);
        Assert.Equal("public", doc.RootElement.GetProperty("visibility").GetString());
    }

    [Fact]
    public async Task Visibility_InvalidValue_Throws()
    {
        var (handler, _, _, _) = MakeHandler();
        var args = ParseArgs("""{"content":"x","visibility":"secret"}""");

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(args, CancellationToken.None));
        Assert.Contains("secret", ex.Message);
    }

    [Fact]
    public async Task Visibility_AndEntryType_BothStoredInMetadata()
    {
        var (handler, store, _, _) = MakeHandler();
        var args = ParseArgs("""{"content":"x","entryType":"preference","visibility":"team"}""");

        await handler.ExecuteAsync(args, CancellationToken.None);

        var call = Assert.Single(store.InsertWithEmbeddingCalls);
        Assert.NotNull(call.Opts.MetadataJson);
        using var doc = JsonDocument.Parse(call.Opts.MetadataJson!);
        Assert.Equal("preference", doc.RootElement.GetProperty("entry_type").GetString());
        Assert.Equal("team", doc.RootElement.GetProperty("visibility").GetString());
    }

    [Fact]
    public async Task Visibility_OmittedDefaults_MetadataNull_WhenNoEntryType()
    {
        var (handler, store, _, _) = MakeHandler();
        var args = ParseArgs("""{"content":"x"}""");

        await handler.ExecuteAsync(args, CancellationToken.None);

        var call = Assert.Single(store.InsertWithEmbeddingCalls);
        Assert.Null(call.Opts.MetadataJson);
    }

    [Fact]
    public async Task ExecuteAsync_WithScope_PassesScopeToInsertOpts()
    {
        var (handler, store, _, _) = MakeHandler("entry-scoped");
        var args = ParseArgs("""{"content":"scoped","tier":"hot","scope":"service:bot"}""");

        await handler.ExecuteAsync(args, CancellationToken.None);

        var call = store.InsertWithEmbeddingCalls.Single();
        Assert.Equal("service:bot", call.Opts.Scope);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutScope_ScopeIsNull()
    {
        var (handler, store, _, _) = MakeHandler("entry-default");
        var args = ParseArgs("""{"content":"no scope","tier":"hot"}""");

        await handler.ExecuteAsync(args, CancellationToken.None);

        var call = store.InsertWithEmbeddingCalls.Single();
        Assert.Null(call.Opts.Scope);
    }
}
