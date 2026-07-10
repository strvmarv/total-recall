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
        MakeHandler(string? id = null, int? hotMaxContentChars = null)
    {
        var store = new FakeStore();
        if (id is not null) store.NextInsertId = id;
        var embedder = new RecordingFakeEmbedder();
        var vector = new FakeVectorSearch();
        var handler = hotMaxContentChars is int hotCap
            ? new MemoryStoreHandler(store, embedder, vector, hotMaxContentChars: hotCap)
            : new MemoryStoreHandler(store, embedder, vector);
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
    public async Task ExecuteAsync_DefaultsTierToWarm_TypeToMemory()
    {
        // Behavior change (tier-model-v2 Task 4): memory_store with no explicit
        // tier now defaults to Warm, not Hot. Previously asserted Tier.Hot.
        var (handler, store, _, _) = MakeHandler();
        var args = ParseArgs("""{"content":"no tier no type"}""");

        await handler.ExecuteAsync(args, CancellationToken.None);

        var call = Assert.Single(store.InsertWithEmbeddingCalls);
        Assert.Equal(Tier.Warm, call.Tier);
        Assert.Equal(ContentType.Memory, call.Type);
        Assert.Null(call.Opts.MetadataJson);
    }

    [Fact]
    public async Task Store_WithNoTier_LandsInWarm()
    {
        var (handler, store, _, _) = MakeHandler();
        await handler.ExecuteAsync(ParseArgs("""{"content":"a new note"}"""), CancellationToken.None);

        var call = Assert.Single(store.InsertWithEmbeddingCalls);
        Assert.Equal(Tier.Warm, call.Tier);
    }

    [Fact]
    public async Task Store_HotOverCap_IsRejected()
    {
        var (handler, _, _, _) = MakeHandler(hotMaxContentChars: 1200);
        var big = new string('x', 1201);
        await Assert.ThrowsAsync<ArgumentException>(() => handler.ExecuteAsync(
            ParseArgs($$"""{"content":"{{big}}","tier":"hot"}"""), CancellationToken.None));
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

    [Fact]
    public async Task ExecuteAsync_WithoutScope_UsesConfiguredDefault()
    {
        var store = new FakeStore();
        store.NextInsertId = "entry-configured";
        var embedder = new RecordingFakeEmbedder();
        var vector = new FakeVectorSearch();
        var handler = new MemoryStoreHandler(store, embedder, vector, scopeDefault: "user:configured");

        var args = ParseArgs("""{"content":"uses default scope","tier":"hot"}""");
        await handler.ExecuteAsync(args, CancellationToken.None);

        var call = store.InsertWithEmbeddingCalls.Single();
        Assert.Equal("user:configured", call.Opts.Scope);
    }

    [Fact]
    public async Task ExecuteAsync_ExplicitScopeOverridesConfiguredDefault()
    {
        var store = new FakeStore();
        var embedder = new RecordingFakeEmbedder();
        var vector = new FakeVectorSearch();
        var handler = new MemoryStoreHandler(store, embedder, vector, scopeDefault: "user:configured");

        var args = ParseArgs("""{"content":"explicit scope","tier":"hot","scope":"team:eng"}""");
        await handler.ExecuteAsync(args, CancellationToken.None);

        var call = store.InsertWithEmbeddingCalls.Single();
        Assert.Equal("team:eng", call.Opts.Scope);
    }

    // Tier model v2 (Task 5): pinned:true now stores into HOT with sticky set.
    [Fact]
    public async Task Store_PinnedTrue_InsertsIntoStickyHot()
    {
        var (handler, store, _, _) = MakeHandler("pin-1");
        var result = await handler.ExecuteAsync(
            ParseArgs("""{"content":"never forget","pinned":true}"""), CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        Assert.Single(store.InsertWithEmbeddingCalls);
        Assert.Equal(Tier.Hot, store.InsertWithEmbeddingCalls[0].Tier);
        Assert.True(store.IsSticky(ContentType.Memory, "pin-1"));
        // decay_score normalized to 1.0 on pin.
        Assert.Contains(store.UpdateCalls, u => u.Id == "pin-1" && u.Opts.DecayScore == 1.0);
    }

    [Fact]
    public async Task Store_PinnedTrueWithTier_Throws()
    {
        var (handler, _, _, _) = MakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(() => handler.ExecuteAsync(
            ParseArgs("""{"content":"x","pinned":true,"tier":"warm"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task Store_TierPinnedString_DirectsToPinnedFlag()
    {
        var (handler, _, _, _) = MakeHandler();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => handler.ExecuteAsync(
            ParseArgs("""{"content":"x","tier":"pinned"}"""), CancellationToken.None));
        Assert.Contains("pinned", ex.Message);
    }

    // Tier model v2 (Task 5): pinned:true now lands in hot, so the HOT cap
    // (1200) governs it — not the retired pinned cap.
    [Fact]
    public async Task Store_PinnedOverHotLimit_Rejected_NonPinnedUnaffected()
    {
        var (handler, store, _, _) = MakeHandler(); // default hot cap 1200
        var big = new string('a', 1201);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => handler.ExecuteAsync(
            ParseArgs($$"""{"content":"{{big}}","pinned":true}"""), CancellationToken.None));
        Assert.Contains("1200", ex.Message);

        // Same content WITHOUT pinned stores fine in warm (only the 100k global cap applies).
        var ok = await handler.ExecuteAsync(
            ParseArgs($$"""{"content":"{{big}}"}"""), CancellationToken.None);
        Assert.NotEqual(true, ok.IsError);
    }

    // M1: Regression anchor — pinned:false must not route to the pinned tier.
    // Behavior change (tier-model-v2 Task 4): with no explicit tier, the
    // default is now Warm (was Hot) — updated expectation accordingly.
    [Fact]
    public async Task Store_PinnedFalse_StoresInWarmTier()
    {
        var (handler, store, _, _) = MakeHandler();
        var args = ParseArgs("""{"content":"x","pinned":false}""");

        await handler.ExecuteAsync(args, CancellationToken.None);

        var call = Assert.Single(store.InsertWithEmbeddingCalls);
        Assert.Equal(Tier.Warm, call.Tier);
    }

    // M2: pinned:true with contentType:knowledge lands in (Tier.Hot,
    // ContentType.Knowledge) as a sticky row (tier model v2, Task 5).
    [Fact]
    public async Task Store_PinnedTrue_Knowledge_InsertsIntoStickyHotKnowledge()
    {
        var (handler, store, _, _) = MakeHandler("pk-1");
        var args = ParseArgs("""{"content":"x","pinned":true,"contentType":"knowledge"}""");

        await handler.ExecuteAsync(args, CancellationToken.None);

        var call = Assert.Single(store.InsertWithEmbeddingCalls);
        Assert.Equal(Tier.Hot, call.Tier);
        Assert.Equal(ContentType.Knowledge, call.Type);
        Assert.True(store.IsSticky(ContentType.Knowledge, "pk-1"));
    }
}
