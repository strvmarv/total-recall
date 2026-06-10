// tests/TotalRecall.Server.Tests/MemoryUnpinHandlerTests.cs
//
// Task 5 (pinned-tier plan 2026-06-09) — MemoryUnpinHandler contract tests.
// Same conventions as MemoryPinHandlerTests: FakeStore, FakeVectorSearch,
// RecordingFakeEmbedder from TestSupport.

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

    [Fact]
    public async Task Unpin_MovesPinnedToWarm()
    {
        var (handler, store, vec, _) = MakeHandler();
        store.Seed(Tier.Pinned, ContentType.Memory, MakeEntry("p1", "body"));

        var result = await handler.ExecuteAsync(ParseArgs("""{"id":"p1"}"""), CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("pinned", doc.RootElement.GetProperty("from_tier").GetString());
        Assert.Equal("warm", doc.RootElement.GetProperty("to_tier").GetString());
        Assert.Single(store.MoveCalls);
        Assert.Equal(Tier.Pinned, store.MoveCalls[0].FromTier);
        Assert.Equal(Tier.Warm, store.MoveCalls[0].ToTier);
        Assert.Single(vec.InsertCalls);
        Assert.Equal(Tier.Warm, vec.InsertCalls[0].Tier);
    }

    [Fact]
    public async Task Unpin_NotPinned_ThrowsWithActualTier()
    {
        var (handler, store, _, _) = MakeHandler();
        store.Seed(Tier.Hot, ContentType.Memory, MakeEntry("h1"));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => handler.ExecuteAsync(
            ParseArgs("""{"id":"h1"}"""), CancellationToken.None));
        Assert.Contains("not pinned", ex.Message);
        Assert.Contains("hot", ex.Message);
        Assert.Empty(store.MoveCalls);
    }

    [Fact]
    public async Task Unpin_MissingEntry_Throws()
    {
        var (handler, _, _, _) = MakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(() => handler.ExecuteAsync(
            ParseArgs("""{"id":"nope"}"""), CancellationToken.None));
    }

    // Parity with Pin_KnowledgeEntry_TypeArgKnowledge_ResultIsKnowledge: a pinned
    // KNOWLEDGE entry unpinned with type:"knowledge" moves to (Warm,Knowledge) and
    // the result DTO reflects the knowledge content type end-to-end.
    [Fact]
    public async Task Unpin_KnowledgeEntry_MovesToWarmKnowledge()
    {
        var (handler, store, vec, _) = MakeHandler();
        store.Seed(Tier.Pinned, ContentType.Knowledge, MakeEntry("k1", "some kb content"));

        var result = await handler.ExecuteAsync(
            ParseArgs("""{"id":"k1","type":"knowledge"}"""), CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("pinned", doc.RootElement.GetProperty("from_tier").GetString());
        Assert.Equal("warm", doc.RootElement.GetProperty("to_tier").GetString());
        Assert.Equal("knowledge", doc.RootElement.GetProperty("to_content_type").GetString());
        Assert.Single(store.MoveCalls);
        Assert.Equal(Tier.Pinned, store.MoveCalls[0].FromTier);
        Assert.Equal(Tier.Warm, store.MoveCalls[0].ToTier);
        Assert.Equal(ContentType.Knowledge, store.MoveCalls[0].ToType);
    }

    // ---- Local-only / sync-queue tests (user decision 2026-06-09) ----

    [Fact]
    public async Task Unpin_WithSyncQueue_DoesNotEnqueueToSyncQueue()
    {
        // Unpin moves pinned→warm. The movement itself is local-only.
        // Even when a SyncQueue is provided, the unpin handler must NOT
        // enqueue anything to it (warm re-entry is handled by RoutingStore).
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        var store = new FakeStore();
        var syncQueue = new SyncQueue(conn);

        store.Seed(Tier.Pinned, ContentType.Memory, MakeEntry("sq-u1", "sync-queue unpin test"));

        var handler = new MemoryUnpinHandler(store, new FakeVectorSearch(),
            new RecordingFakeEmbedder(), syncQueue: syncQueue);

        await handler.ExecuteAsync(ParseArgs("""{"id":"sq-u1"}"""), CancellationToken.None);

        // The sync queue must remain empty — the unpin handler does not push
        // compaction telemetry (local-only pinned tier; Cortex has no pin support).
        var items = syncQueue.Drain(limit: 10);
        Assert.Empty(items);
    }

    [Fact]
    public async Task Unpin_WithCompactionLogAndSyncQueue_LogsLocallyButDoesNotEnqueueSync()
    {
        // Local compaction log must still record the unpin movement even though
        // the sync queue must NOT receive an enqueue.
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        var store = new FakeStore();
        var compactionLog = new CompactionLog(conn);
        var syncQueue = new SyncQueue(conn);

        store.Seed(Tier.Pinned, ContentType.Memory, MakeEntry("cq-u1", "compaction unpin test"));

        var handler = new MemoryUnpinHandler(store, new FakeVectorSearch(),
            new RecordingFakeEmbedder(), compactionLog: compactionLog, syncQueue: syncQueue);

        await handler.ExecuteAsync(ParseArgs("""{"id":"cq-u1"}"""), CancellationToken.None);

        // Local compaction log should record the unpin.
        var movements = compactionLog.GetRecentMovements(limit: 10);
        Assert.Single(movements);
        Assert.Equal("pinned", movements[0].SourceTier);
        Assert.Equal("warm", movements[0].TargetTier);
        Assert.Equal("manual_unpin", movements[0].Reason);

        // Sync queue must be empty — no outbound Cortex push for pinned.
        var items = syncQueue.Drain(limit: 10);
        Assert.Empty(items);
    }
}
