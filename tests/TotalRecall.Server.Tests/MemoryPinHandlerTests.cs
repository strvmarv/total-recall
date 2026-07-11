// tests/TotalRecall.Server.Tests/MemoryPinHandlerTests.cs
//
// Tier model v2 (Task 5) — MemoryPinHandler now operates as the `sticky` flag
// on the hot tier instead of moving entries to the retired pinned tier. Pin
// moves the entry into Hot (if elsewhere), sets sticky=1, and normalizes
// decay_score=1.0. Uses FakeStore, FakeVectorSearch, RecordingFakeEmbedder.

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

public class MemoryPinHandlerTests
{
    private static (MemoryPinHandler handler, FakeStore store,
            FakeVectorSearch vec, RecordingFakeEmbedder embedder) MakeHandler()
    {
        var store = new FakeStore();
        var vec = new FakeVectorSearch();
        var embedder = new RecordingFakeEmbedder();
        return (new MemoryPinHandler(store, vec, embedder), store, vec, embedder);
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

    // Seed a warm memory and return its id.
    private static string SeedWarm(FakeStore store, string content, string? project = null)
    {
        var id = "w-" + Guid.NewGuid().ToString("N");
        store.Seed(Tier.Warm, ContentType.Memory, MakeEntry(id, content, project));
        return id;
    }

    [Theory]
    [InlineData(true)]  // hot source
    [InlineData(false)] // warm source
    public async Task Pin_SetsStickyOnHot(bool fromHot)
    {
        var (handler, store, vec, _) = MakeHandler();
        var src = fromHot ? Tier.Hot : Tier.Warm;
        store.Seed(src, ContentType.Memory, MakeEntry("e1", "body"));

        var result = await handler.ExecuteAsync(ParseArgs("""{"id":"e1"}"""), CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("hot", doc.RootElement.GetProperty("to_tier").GetString());
        Assert.True(store.IsSticky(ContentType.Memory, "e1"));
        Assert.Single(store.List(Tier.Hot, ContentType.Memory,
            new ListEntriesOpts { StickyOnly = true }));
        if (fromHot)
            Assert.Empty(store.MoveCalls); // already hot — no move/re-embed
        else
        {
            Assert.Single(store.MoveCalls);
            Assert.Equal(Tier.Hot, store.MoveCalls[0].ToTier);
            Assert.Single(vec.InsertCalls);
            Assert.Equal(Tier.Hot, vec.InsertCalls[0].Tier);
        }
    }

    // Brief Step 5 — pin routes to sticky-hot, not the pinned tier.
    [Fact]
    public async Task Pin_SetsStickyOnHot_NotPinnedTier()
    {
        var (handler, store, _, _) = MakeHandler();
        var id = SeedWarm(store, "keep me");

        await handler.ExecuteAsync(ParseArgs($$"""{"id":"{{id}}"}"""), CancellationToken.None);

        Assert.Single(store.List(Tier.Hot, ContentType.Memory,
            new ListEntriesOpts { StickyOnly = true }));
        // Tier model v2 (Task 9): every move targets hot (no pinned tier).
        Assert.All(store.MoveCalls, c => Assert.True(c.ToTier.IsHot));
    }

    [Fact]
    public async Task Pin_NormalizesDecayScoreToOne()
    {
        var (handler, store, _, _) = MakeHandler();
        var id = SeedWarm(store, "body");

        await handler.ExecuteAsync(ParseArgs($$"""{"id":"{{id}}"}"""), CancellationToken.None);

        Assert.Single(store.UpdateCalls);
        Assert.Equal(Tier.Hot, store.UpdateCalls[0].Tier);
        Assert.Equal(1.0, store.UpdateCalls[0].Opts.DecayScore);
    }

    [Fact]
    public async Task Pin_AlreadyStickyHot_IsIdempotentSuccess_NoWrite()
    {
        var (handler, store, _, _) = MakeHandler();
        store.Seed(Tier.Hot, ContentType.Memory, MakeEntry("p1"));
        store.SetSticky(ContentType.Memory, "p1", true);

        var result = await handler.ExecuteAsync(ParseArgs("""{"id":"p1"}"""), CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        Assert.Empty(store.MoveCalls);   // no move, no re-embed
        Assert.Empty(store.UpdateCalls); // already sticky, no scope — no write
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("hot", doc.RootElement.GetProperty("to_tier").GetString());
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task Pin_ScopeGlobal_ClearsProject()
    {
        var (handler, store, _, _) = MakeHandler();
        store.Seed(Tier.Warm, ContentType.Memory, MakeEntry("e1", project: "proj-a"));

        await handler.ExecuteAsync(
            ParseArgs("""{"id":"e1","scope":"global"}"""), CancellationToken.None);

        Assert.Single(store.UpdateCalls);
        Assert.True(store.UpdateCalls[0].Opts.ClearProject);
    }

    [Fact]
    public async Task Pin_ScopeProject_SetsProvidedProject()
    {
        var (handler, store, _, _) = MakeHandler();
        store.Seed(Tier.Warm, ContentType.Memory, MakeEntry("e1"));

        await handler.ExecuteAsync(
            ParseArgs("""{"id":"e1","scope":"project","project":"proj-b"}"""), CancellationToken.None);

        Assert.Single(store.UpdateCalls);
        Assert.Equal("proj-b", store.UpdateCalls[0].Opts.Project);
    }

    [Fact]
    public async Task Pin_ScopeProject_NoProjectAnywhere_Throws()
    {
        var (handler, store, _, _) = MakeHandler();
        store.Seed(Tier.Warm, ContentType.Memory, MakeEntry("e1")); // entry has no project

        await Assert.ThrowsAsync<ArgumentException>(() => handler.ExecuteAsync(
            ParseArgs("""{"id":"e1","scope":"project"}"""), CancellationToken.None));
    }

    // Regression: scope validation must happen BEFORE MoveAndReEmbed so a failed
    // pin (entry has no project and no project arg) leaves the entry untouched.
    [Fact]
    public async Task Pin_ScopeProject_NoProjectAnywhere_ThrowsBeforeMove()
    {
        var (handler, store, _, _) = MakeHandler();
        store.Seed(Tier.Warm, ContentType.Memory, MakeEntry("e1")); // entry has no project

        await Assert.ThrowsAsync<ArgumentException>(() => handler.ExecuteAsync(
            ParseArgs("""{"id":"e1","scope":"project"}"""), CancellationToken.None));

        Assert.Empty(store.MoveCalls);
        Assert.False(store.IsSticky(ContentType.Memory, "e1"));
        Assert.NotNull(store.Entries.GetValueOrDefault((Tier.Warm, ContentType.Memory, "e1")));
    }

    [Fact]
    public async Task Pin_MissingEntry_Throws()
    {
        var (handler, _, _, _) = MakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(() => handler.ExecuteAsync(
            ParseArgs("""{"id":"nope"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task Pin_ContentAtLimit_Succeeds_OneOverRejected()
    {
        var (handler, store, _, _) = MakeHandler(); // default limit 500
        store.Seed(Tier.Warm, ContentType.Memory, MakeEntry("ok", new string('a', 500)));
        store.Seed(Tier.Warm, ContentType.Memory, MakeEntry("big", new string('a', 501)));

        var ok = await handler.ExecuteAsync(ParseArgs("""{"id":"ok"}"""), CancellationToken.None);
        Assert.NotEqual(true, ok.IsError);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => handler.ExecuteAsync(
            ParseArgs("""{"id":"big"}"""), CancellationToken.None));
        Assert.Contains("500", ex.Message);
        Assert.Single(store.MoveCalls); // only the ok entry moved
    }

    [Fact]
    public async Task Pin_ConfigOverride_RaisesLimit()
    {
        var store = new FakeStore();
        var handler = new MemoryPinHandler(store, new FakeVectorSearch(),
            new RecordingFakeEmbedder(), maxContentChars: 1000);
        store.Seed(Tier.Warm, ContentType.Memory, MakeEntry("big", new string('a', 800)));

        var result = await handler.ExecuteAsync(ParseArgs("""{"id":"big"}"""), CancellationToken.None);
        Assert.NotEqual(true, result.IsError);
    }

    // Already-sticky no-scope path must not call Update (I1 regression lock).
    [Fact]
    public async Task Pin_AlreadySticky_WithProject_NoScope_DoesNotCallUpdate()
    {
        var (handler, store, _, _) = MakeHandler();
        store.Seed(Tier.Hot, ContentType.Memory, MakeEntry("p2", project: "existing-proj"));
        store.SetSticky(ContentType.Memory, "p2", true);

        await handler.ExecuteAsync(ParseArgs("""{"id":"p2"}"""), CancellationToken.None);

        Assert.Empty(store.MoveCalls);
        Assert.Empty(store.UpdateCalls); // existing project preserved; no write needed
    }

    // Pin a hot knowledge entry with type:"knowledge" explicitly →
    // result DTO reflects knowledge content type; sticky set on hot knowledge.
    [Fact]
    public async Task Pin_KnowledgeEntry_TypeArgKnowledge_ResultIsKnowledge()
    {
        var (handler, store, _, _) = MakeHandler();
        store.Seed(Tier.Hot, ContentType.Knowledge, MakeEntry("k1", "some kb content"));

        var result = await handler.ExecuteAsync(
            ParseArgs("""{"id":"k1","type":"knowledge"}"""), CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("knowledge", doc.RootElement.GetProperty("to_content_type").GetString());
        Assert.Equal("hot", doc.RootElement.GetProperty("to_tier").GetString());
        Assert.True(store.IsSticky(ContentType.Knowledge, "k1"));
    }

    // ---- Local-only / sync-queue tests (pinned/sticky is local-only) ----

    [Fact]
    public async Task Pin_WithSyncQueue_DoesNotEnqueueToSyncQueue()
    {
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        var store = new FakeStore();
        var syncQueue = new SyncQueue(conn);

        store.Seed(Tier.Warm, ContentType.Memory, MakeEntry("sq-1", "sync-queue test"));

        var handler = new MemoryPinHandler(store, new FakeVectorSearch(),
            new RecordingFakeEmbedder(), syncQueue: syncQueue);

        await handler.ExecuteAsync(ParseArgs("""{"id":"sq-1"}"""), CancellationToken.None);

        var items = syncQueue.Drain(limit: 10);
        Assert.Empty(items);
    }

    [Fact]
    public async Task Pin_WithCompactionLogAndSyncQueue_LogsLocallyButDoesNotEnqueueSync()
    {
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        var store = new FakeStore();
        var compactionLog = new CompactionLog(conn);
        var syncQueue = new SyncQueue(conn);

        store.Seed(Tier.Warm, ContentType.Memory, MakeEntry("cq-1", "compaction log test"));

        var handler = new MemoryPinHandler(store, new FakeVectorSearch(),
            new RecordingFakeEmbedder(), compactionLog: compactionLog, syncQueue: syncQueue);

        await handler.ExecuteAsync(ParseArgs("""{"id":"cq-1"}"""), CancellationToken.None);

        var movements = compactionLog.GetRecentMovements(limit: 10);
        Assert.Single(movements);
        Assert.Equal("warm", movements[0].SourceTier);
        Assert.Equal("hot", movements[0].TargetTier);
        Assert.Equal("manual_pin", movements[0].Reason);

        var items = syncQueue.Drain(limit: 10);
        Assert.Empty(items);
    }
}
