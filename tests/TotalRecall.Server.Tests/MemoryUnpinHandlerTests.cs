// tests/TotalRecall.Server.Tests/MemoryUnpinHandlerTests.cs
//
// Tier model v2 (Task 5) — MemoryUnpinHandler now clears the `sticky` flag on
// the hot tier; the entry STAYS in hot (an earned resident) and resumes decay.
// No move to warm. Only sticky-hot entries may be unpinned.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Sync;
using TotalRecall.Infrastructure.Telemetry;
using TotalRecall.Server.Handlers;
using TotalRecall.Server.Tests.TestSupport;
using Xunit;

namespace TotalRecall.Server.Tests;

public class MemoryUnpinHandlerTests
{
    private static (MemoryUnpinHandler handler, FakeStore store,
            FakeVectorSearch vec, RecordingFakeEmbedder embedder) MakeHandler()
    {
        var store = new FakeStore();
        var vec = new FakeVectorSearch();
        var embedder = new RecordingFakeEmbedder();
        return (new MemoryUnpinHandler(store, vec, embedder), store, vec, embedder);
    }

    private static JsonElement ParseArgs(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static Entry MakeEntry(string id, string content = "hello", string? project = null) =>
        new(
            id, content,
            FSharpOption<string>.None, FSharpOption<string>.None,
            FSharpOption<SourceTool>.None,
            project is null ? FSharpOption<string>.None : FSharpOption<string>.Some(project),
            ListModule.OfSeq(Array.Empty<string>()),
            1L, 2L, 3L, 4, 0.5,
            FSharpOption<string>.None, FSharpOption<string>.None, "", EntryType.Preference, "{}", 0);

    private static string SeedStickyHot(FakeStore store, string content) =>
        SeedStickyHot(store, content, ContentType.Memory);

    private static string SeedStickyHot(FakeStore store, string content, ContentType type)
    {
        var id = "h-" + Guid.NewGuid().ToString("N");
        store.Seed(Tier.Hot, type, MakeEntry(id, content));
        store.SetSticky(type, id, true);
        return id;
    }

    [Fact]
    public async Task Unpin_ClearsSticky_KeepsInHot()
    {
        var (handler, store, vec, _) = MakeHandler();
        var id = SeedStickyHot(store, "was pinned");

        var result = await handler.ExecuteAsync(ParseArgs($$"""{"id":"{{id}}"}"""), CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("hot", doc.RootElement.GetProperty("from_tier").GetString());
        Assert.Equal("hot", doc.RootElement.GetProperty("to_tier").GetString());
        // Entry stays in hot — no move, no re-embed.
        Assert.Empty(store.MoveCalls);
        Assert.Empty(vec.InsertCalls);
        Assert.Equal(1, store.Count(Tier.Hot, ContentType.Memory));
        Assert.False(store.IsSticky(ContentType.Memory, id));
        Assert.Empty(store.List(Tier.Hot, ContentType.Memory,
            new ListEntriesOpts { StickyOnly = true }));
    }

    [Fact]
    public async Task Unpin_NotSticky_ThrowsNotPinned()
    {
        var (handler, store, _, _) = MakeHandler();
        store.Seed(Tier.Hot, ContentType.Memory, MakeEntry("h1")); // hot but not sticky

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => handler.ExecuteAsync(
            ParseArgs("""{"id":"h1"}"""), CancellationToken.None));
        Assert.Contains("not pinned", ex.Message);
        Assert.Empty(store.MoveCalls);
    }

    [Fact]
    public async Task Unpin_WarmEntry_ThrowsNotPinned()
    {
        var (handler, store, _, _) = MakeHandler();
        store.Seed(Tier.Warm, ContentType.Memory, MakeEntry("w1"));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => handler.ExecuteAsync(
            ParseArgs("""{"id":"w1"}"""), CancellationToken.None));
        Assert.Contains("not pinned", ex.Message);
    }

    [Fact]
    public async Task Unpin_MissingEntry_Throws()
    {
        var (handler, _, _, _) = MakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(() => handler.ExecuteAsync(
            ParseArgs("""{"id":"nope"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task Unpin_KnowledgeEntry_ClearsStickyStaysHotKnowledge()
    {
        var (handler, store, _, _) = MakeHandler();
        var id = SeedStickyHot(store, "some kb content", ContentType.Knowledge);

        var result = await handler.ExecuteAsync(
            ParseArgs($$"""{"id":"{{id}}","type":"knowledge"}"""), CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("hot", doc.RootElement.GetProperty("from_tier").GetString());
        Assert.Equal("hot", doc.RootElement.GetProperty("to_tier").GetString());
        Assert.Equal("knowledge", doc.RootElement.GetProperty("to_content_type").GetString());
        Assert.False(store.IsSticky(ContentType.Knowledge, id));
        Assert.Equal(1, store.Count(Tier.Hot, ContentType.Knowledge));
    }

    // ---- Local-only / sync-queue tests ----

    [Fact]
    public async Task Unpin_WithSyncQueue_DoesNotEnqueueToSyncQueue()
    {
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        var store = new FakeStore();
        var syncQueue = new SyncQueue(conn);

        var id = SeedStickyHot(store, "sync-queue unpin test");

        var handler = new MemoryUnpinHandler(store, new FakeVectorSearch(),
            new RecordingFakeEmbedder(), syncQueue: syncQueue);

        await handler.ExecuteAsync(ParseArgs($$"""{"id":"{{id}}"}"""), CancellationToken.None);

        var items = syncQueue.Drain(limit: 10);
        Assert.Empty(items);
    }

    [Fact]
    public async Task Unpin_WithCompactionLogAndSyncQueue_LogsLocallyButDoesNotEnqueueSync()
    {
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        var store = new FakeStore();
        var compactionLog = new CompactionLog(conn);
        var syncQueue = new SyncQueue(conn);

        var id = SeedStickyHot(store, "compaction unpin test");

        var handler = new MemoryUnpinHandler(store, new FakeVectorSearch(),
            new RecordingFakeEmbedder(), compactionLog: compactionLog, syncQueue: syncQueue);

        await handler.ExecuteAsync(ParseArgs($$"""{"id":"{{id}}"}"""), CancellationToken.None);

        var movements = compactionLog.GetRecentMovements(limit: 10);
        Assert.Single(movements);
        Assert.Equal("hot", movements[0].SourceTier);
        Assert.Equal("hot", movements[0].TargetTier);
        Assert.Equal("manual_unpin", movements[0].Reason);

        var items = syncQueue.Drain(limit: 10);
        Assert.Empty(items);
    }
}
