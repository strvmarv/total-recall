// tests/TotalRecall.Server.Tests/StatusHandlerTests.cs
//
// Plan 4 Task 4.11 — unit tests for StatusHandler. Uses FakeSqliteStore's
// Counts and ListByMetadataSlots seeding (added alongside Task 4.11) to
// drop pre-built tier sizes and collection rows. Exercises the JSON wire
// shape described in src-ts/tools/system-tools.ts, scoped to the Plan 4
// subset (tierSizes, knowledgeBase, db, embedding, activity stub,
// lastCompaction stub).

namespace TotalRecall.Server.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Server.Handlers;
using TotalRecall.Server.Tests.TestSupport;
using Xunit;

public sealed class StatusHandlerTests
{
    private const string DefaultDbPath = "/tmp/total-recall-status-tests-nonexistent.db";
    private const string DefaultModel = "all-MiniLM-L6-v2";
    private const int DefaultDims = 384;

    private static readonly Dictionary<string, string> CollectionFilter =
        new(StringComparer.Ordinal) { ["type"] = "collection" };

    private static StatusOptions Options(
        string? dbPath = null,
        string? model = null,
        int? dims = null) => new(
            DbPath: dbPath ?? DefaultDbPath,
            EmbeddingModel: model ?? DefaultModel,
            EmbeddingDimensions: dims ?? DefaultDims);

    private static Entry MakeCollectionEntry(string id, string name)
    {
        var metadata =
            "{\"type\":\"collection\",\"name\":\"" + name + "\",\"source_path\":\"/x\"}";
        return new Entry(
            id,
            "Collection: " + name,
            FSharpOption<string>.None,
            FSharpOption<string>.None,
            FSharpOption<SourceTool>.None,
            FSharpOption<string>.None,
            ListModule.OfSeq(Array.Empty<string>()),
            0, 0, 0, 0, 0.0,
            FSharpOption<string>.None,
            FSharpOption<string>.None,
            metadata);
    }

    private static async Task<JsonElement> RunAsync(
        FakeSqliteStore store,
        FakeSessionLifecycle lifecycle,
        StatusOptions options)
    {
        var handler = new StatusHandler(store, lifecycle, options);
        var result = await handler.ExecuteAsync(null, CancellationToken.None);
        Assert.False(result.IsError ?? false);
        return JsonDocument.Parse(result.Content[0].Text).RootElement.Clone();
    }

    [Fact]
    public async Task HappyPath_ReturnsStatusShape()
    {
        var store = new FakeSqliteStore();
        var lifecycle = new FakeSessionLifecycle();

        var root = await RunAsync(store, lifecycle, Options());

        Assert.Equal(JsonValueKind.Object, root.GetProperty("tierSizes").ValueKind);
        Assert.Equal(JsonValueKind.Object, root.GetProperty("knowledgeBase").ValueKind);
        Assert.Equal(JsonValueKind.Object, root.GetProperty("db").ValueKind);
        Assert.Equal(JsonValueKind.Object, root.GetProperty("embedding").ValueKind);
        Assert.Equal(JsonValueKind.Object, root.GetProperty("activity").ValueKind);
        // lastCompaction is stubbed to null; under Task 4.12's
        // DefaultIgnoreCondition=Never the property is emitted as a literal
        // JSON null (matching TS JSON.stringify on a null field).
        Assert.Equal(JsonValueKind.Null, root.GetProperty("lastCompaction").ValueKind);
    }

    [Fact]
    public async Task TierSizes_CountsFromStore()
    {
        var store = new FakeSqliteStore();
        store.SeedCount(Tier.Hot, ContentType.Memory, 11);
        store.SeedCount(Tier.Hot, ContentType.Knowledge, 22);
        store.SeedCount(Tier.Warm, ContentType.Memory, 33);
        store.SeedCount(Tier.Warm, ContentType.Knowledge, 44);
        store.SeedCount(Tier.Cold, ContentType.Memory, 55);
        store.SeedCount(Tier.Cold, ContentType.Knowledge, 66);

        var root = await RunAsync(store, new FakeSessionLifecycle(), Options());

        var ts = root.GetProperty("tierSizes");
        Assert.Equal(11, ts.GetProperty("hot_memories").GetInt32());
        Assert.Equal(22, ts.GetProperty("hot_knowledge").GetInt32());
        Assert.Equal(33, ts.GetProperty("warm_memories").GetInt32());
        Assert.Equal(44, ts.GetProperty("warm_knowledge").GetInt32());
        Assert.Equal(55, ts.GetProperty("cold_memories").GetInt32());
        Assert.Equal(66, ts.GetProperty("cold_knowledge").GetInt32());
    }

    [Fact]
    public async Task KnowledgeBase_TotalChunks_ExcludesCollections()
    {
        var store = new FakeSqliteStore();
        // 10 total cold_knowledge rows, of which 2 are collections. The
        // remaining 8 are treated as chunks (documents + leaf chunks).
        store.SeedCount(Tier.Cold, ContentType.Knowledge, 10);
        store.SeedListByMetadata(
            Tier.Cold,
            ContentType.Knowledge,
            CollectionFilter,
            MakeCollectionEntry("col-1", "First"),
            MakeCollectionEntry("col-2", "Second"));

        var root = await RunAsync(store, new FakeSessionLifecycle(), Options());

        var kb = root.GetProperty("knowledgeBase");
        var cols = kb.GetProperty("collections");
        Assert.Equal(2, cols.GetArrayLength());
        Assert.Equal("col-1", cols[0].GetProperty("id").GetString());
        Assert.Equal("First", cols[0].GetProperty("name").GetString());
        Assert.Equal("col-2", cols[1].GetProperty("id").GetString());
        Assert.Equal("Second", cols[1].GetProperty("name").GetString());
        Assert.Equal(8, kb.GetProperty("totalChunks").GetInt32());
    }

    [Fact]
    public async Task KnowledgeBase_EmptyCollections_StillReturnsTotalChunks()
    {
        var store = new FakeSqliteStore();
        store.SeedCount(Tier.Cold, ContentType.Knowledge, 5);
        // No ListByMetadata seeding -> empty collection list.

        var root = await RunAsync(store, new FakeSessionLifecycle(), Options());

        var kb = root.GetProperty("knowledgeBase");
        Assert.Equal(0, kb.GetProperty("collections").GetArrayLength());
        Assert.Equal(5, kb.GetProperty("totalChunks").GetInt32());
    }

    [Fact]
    public async Task Db_SizeBytes_Null_WhenFileNotFound()
    {
        var missing = Path.Combine(
            Path.GetTempPath(),
            "total-recall-status-does-not-exist-" + Guid.NewGuid() + ".db");
        var store = new FakeSqliteStore();

        var root = await RunAsync(
            store,
            new FakeSessionLifecycle(),
            Options(dbPath: missing));

        var db = root.GetProperty("db");
        Assert.Equal(missing, db.GetProperty("path").GetString());
        // sizeBytes is null -> emitted as literal JSON null (Task 4.12).
        Assert.Equal(JsonValueKind.Null, db.GetProperty("sizeBytes").ValueKind);
    }

    [Fact]
    public async Task Db_SizeBytes_MatchesFileLength()
    {
        var tmp = Path.Combine(
            Path.GetTempPath(),
            "total-recall-status-" + Guid.NewGuid() + ".db");
        try
        {
            var bytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
            File.WriteAllBytes(tmp, bytes);
            var store = new FakeSqliteStore();

            var root = await RunAsync(
                store,
                new FakeSessionLifecycle(),
                Options(dbPath: tmp));

            var db = root.GetProperty("db");
            Assert.Equal(tmp, db.GetProperty("path").GetString());
            Assert.Equal(bytes.Length, db.GetProperty("sizeBytes").GetInt64());
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public async Task Db_SessionId_FromLifecycle()
    {
        var store = new FakeSqliteStore();
        var lifecycle = new FakeSessionLifecycle { SessionIdValue = "sess-status-xyz" };

        var root = await RunAsync(store, lifecycle, Options());

        Assert.Equal(
            "sess-status-xyz",
            root.GetProperty("db").GetProperty("sessionId").GetString());
        // status must NOT force init just to read the session id.
        Assert.Equal(0, lifecycle.EnsureInitializedCallCount);
    }

    [Fact]
    public async Task Embedding_ModelAndDimensions_FromOptions()
    {
        var store = new FakeSqliteStore();
        var lifecycle = new FakeSessionLifecycle();

        var root = await RunAsync(
            store,
            lifecycle,
            Options(model: "custom-model", dims: 768));

        var emb = root.GetProperty("embedding");
        Assert.Equal("custom-model", emb.GetProperty("model").GetString());
        Assert.Equal(768, emb.GetProperty("dimensions").GetInt32());
    }

    [Fact]
    public async Task Activity_Stubbed_ReturnsZeros()
    {
        var store = new FakeSqliteStore();

        var root = await RunAsync(store, new FakeSessionLifecycle(), Options());

        var a = root.GetProperty("activity");
        Assert.Equal(0, a.GetProperty("retrievals7d").GetInt32());
        // avgTopScore7d is null -> emitted as literal JSON null (Task 4.12).
        Assert.Equal(JsonValueKind.Null, a.GetProperty("avgTopScore7d").ValueKind);
        Assert.Equal(0, a.GetProperty("positiveOutcomes7d").GetInt32());
        Assert.Equal(0, a.GetProperty("negativeOutcomes7d").GetInt32());
    }

    [Fact]
    public async Task LastCompaction_Stubbed_ReturnsNull()
    {
        var store = new FakeSqliteStore();

        var root = await RunAsync(store, new FakeSessionLifecycle(), Options());

        // Stubbed to null -> emitted as literal JSON null (Task 4.12).
        Assert.Equal(JsonValueKind.Null, root.GetProperty("lastCompaction").ValueKind);
    }

    [Fact]
    public async Task NullArguments_DoesNotThrow()
    {
        var store = new FakeSqliteStore();
        var handler = new StatusHandler(store, new FakeSessionLifecycle(), Options());

        var result = await handler.ExecuteAsync(null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.IsError ?? false);
    }

    [Fact]
    public async Task EmptyObjectArguments_DoesNotThrow()
    {
        var store = new FakeSqliteStore();
        var handler = new StatusHandler(store, new FakeSessionLifecycle(), Options());

        using var doc = JsonDocument.Parse("{}");
        var result = await handler.ExecuteAsync(doc.RootElement, CancellationToken.None);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task JsonShape_MatchesExpected()
    {
        var store = new FakeSqliteStore();
        store.SeedCount(Tier.Hot, ContentType.Memory, 1);
        store.SeedCount(Tier.Hot, ContentType.Knowledge, 2);
        store.SeedCount(Tier.Warm, ContentType.Memory, 3);
        store.SeedCount(Tier.Warm, ContentType.Knowledge, 4);
        store.SeedCount(Tier.Cold, ContentType.Memory, 5);
        store.SeedCount(Tier.Cold, ContentType.Knowledge, 6);
        store.SeedListByMetadata(
            Tier.Cold,
            ContentType.Knowledge,
            CollectionFilter,
            MakeCollectionEntry("col-a", "Alpha"));
        var lifecycle = new FakeSessionLifecycle { SessionIdValue = "sess-shape" };

        var root = await RunAsync(store, lifecycle, Options());

        // Top-level keys present.
        Assert.True(root.TryGetProperty("tierSizes", out _));
        Assert.True(root.TryGetProperty("knowledgeBase", out _));
        Assert.True(root.TryGetProperty("db", out _));
        Assert.True(root.TryGetProperty("embedding", out _));
        Assert.True(root.TryGetProperty("activity", out _));
        // lastCompaction emitted as literal null (Task 4.12 Never default).
        Assert.Equal(JsonValueKind.Null, root.GetProperty("lastCompaction").ValueKind);

        // Spot-check nested shape.
        Assert.Equal(6, root.GetProperty("tierSizes").GetProperty("cold_knowledge").GetInt32());
        var kb = root.GetProperty("knowledgeBase");
        Assert.Equal(1, kb.GetProperty("collections").GetArrayLength());
        Assert.Equal("Alpha", kb.GetProperty("collections")[0].GetProperty("name").GetString());
        Assert.Equal(5, kb.GetProperty("totalChunks").GetInt32());
        Assert.Equal("sess-shape", root.GetProperty("db").GetProperty("sessionId").GetString());
    }

    [Fact]
    public void Metadata_NameAndDescription()
    {
        var handler = new StatusHandler(
            new FakeSqliteStore(),
            new FakeSessionLifecycle(),
            Options());

        Assert.Equal("status", handler.Name);
        Assert.Contains("status", handler.Description);
        Assert.Equal(JsonValueKind.Object, handler.InputSchema.ValueKind);
        Assert.Equal(JsonValueKind.Object, handler.InputSchema.GetProperty("properties").ValueKind);
    }
}
