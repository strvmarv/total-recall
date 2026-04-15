// tests/TotalRecall.Server.Tests/MigrateToRemoteHandlerTests.cs
//
// Task 15 — contract tests for MigrateToRemoteHandler.
//
// All three tests drive the handler through the internal constructor that
// accepts pre-built fakes, so no real SQLite / ONNX / Postgres is needed.
// Each test seeds FakeStore instances and asserts the handler's
// observed side-effects and return payload.

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

public sealed class MigrateToRemoteHandlerTests
{
    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static Entry MakeEntry(string id) => MakeEntry(id, ContentType.Memory);

    private static Entry MakeEntry(string id, ContentType type)
    {
        return new Entry(
            id,
            $"content of {id}",
            FSharpOption<string>.None,
            FSharpOption<string>.None,
            FSharpOption<SourceTool>.None,
            FSharpOption<string>.None,
            FSharpList<string>.Empty,
            1_000_000L,
            1_000_000L,
            1_000_000L,
            0,
            1.0,
            FSharpOption<string>.None,
            FSharpOption<string>.None,
            "",
            "{}");
    }

    private static MigrateToRemoteHandler MakeHandler(
        FakeStore source,
        FakeStore target,
        RecordingFakeEmbedder embedder,
        FakeVectorSearch vectors)
    {
        return new MigrateToRemoteHandler(source, target, embedder, vectors);
    }

    private static JsonElement? ParseArgs(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    // ------------------------------------------------------------------
    // Test 1: dry_run reports counts without writing
    // ------------------------------------------------------------------

    [Fact]
    public async Task DryRun_ReportsCountsWithoutWriting()
    {
        var source = new FakeStore();
        var target = new FakeStore();
        var embedder = new RecordingFakeEmbedder();
        var vectors = new FakeVectorSearch();

        // Seed source with two Memory entries in Hot tier.
        source.SeedList(Tier.Hot, ContentType.Memory,
            MakeEntry("id-1"),
            MakeEntry("id-2"));

        var handler = MakeHandler(source, target, embedder, vectors);
        var args = ParseArgs("""{"dry_run": true}""");

        var result = await handler.ExecuteAsync(args, CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        var root = doc.RootElement;

        // Counts match seeded source rows.
        Assert.Equal(2, root.GetProperty("migrated").GetInt32());
        Assert.Equal(0, root.GetProperty("skipped").GetInt32());
        Assert.Equal(0, root.GetProperty("errors").GetInt32());
        Assert.True(root.GetProperty("dry_run").GetBoolean());

        // No writes to target, no embed calls.
        Assert.Empty(target.InsertWithEmbeddingCalls);
        Assert.Empty(embedder.Calls);
    }

    // ------------------------------------------------------------------
    // Test 2: existing ids in target are skipped (idempotent)
    // ------------------------------------------------------------------

    [Fact]
    public async Task SkipsDuplicateIds()
    {
        var source = new FakeStore();
        var target = new FakeStore();
        var embedder = new RecordingFakeEmbedder();
        var vectors = new FakeVectorSearch();

        var entry = MakeEntry("dup-id");

        // Source has the entry.
        source.SeedList(Tier.Hot, ContentType.Memory, entry);

        // Target already has the same entry — Seed populates Get() lookup.
        target.Seed(Tier.Hot, ContentType.Memory, entry);

        var handler = MakeHandler(source, target, embedder, vectors);

        var result = await handler.ExecuteAsync(null, CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        var root = doc.RootElement;

        Assert.Equal(0, root.GetProperty("migrated").GetInt32());
        Assert.Equal(1, root.GetProperty("skipped").GetInt32());
        Assert.Equal(0, root.GetProperty("errors").GetInt32());
        Assert.False(root.GetProperty("dry_run").GetBoolean());

        // Nothing inserted.
        Assert.Empty(target.InsertWithEmbeddingCalls);
    }

    // ------------------------------------------------------------------
    // Test 3: entries are re-embedded and inserted in target
    // ------------------------------------------------------------------

    [Fact]
    public async Task MigratesEntries_ReEmbedsAndInsertsInTarget()
    {
        var source = new FakeStore();
        var target = new FakeStore();
        var embedder = new RecordingFakeEmbedder();
        var vectors = new FakeVectorSearch();

        var e1 = MakeEntry("migrate-1");
        var e2 = MakeEntry("migrate-2", ContentType.Knowledge);

        source.SeedList(Tier.Hot, ContentType.Memory, e1);
        source.SeedList(Tier.Warm, ContentType.Knowledge, e2);

        var handler = MakeHandler(source, target, embedder, vectors);

        var result = await handler.ExecuteAsync(null, CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        var root = doc.RootElement;

        Assert.Equal(2, root.GetProperty("migrated").GetInt32());
        Assert.Equal(0, root.GetProperty("skipped").GetInt32());
        Assert.Equal(0, root.GetProperty("errors").GetInt32());

        // Embedder was called once per migrated entry.
        Assert.Equal(2, embedder.Calls.Count);
        Assert.Contains("content of migrate-1", embedder.Calls);
        Assert.Contains("content of migrate-2", embedder.Calls);

        // Target received two InsertWithEmbedding calls, preserving source ids.
        Assert.Equal(2, target.InsertWithEmbeddingCalls.Count);

        var call1 = target.InsertWithEmbeddingCalls[0];
        Assert.Equal(Tier.Hot, call1.Tier);
        Assert.Equal(ContentType.Memory, call1.Type);
        Assert.Equal("migrate-1", call1.Opts.Id);
        Assert.Equal("content of migrate-1", call1.Opts.Content);

        var call2 = target.InsertWithEmbeddingCalls[1];
        Assert.Equal(Tier.Warm, call2.Tier);
        Assert.Equal(ContentType.Knowledge, call2.Type);
        Assert.Equal("migrate-2", call2.Opts.Id);
        Assert.Equal("content of migrate-2", call2.Opts.Content);
    }

    // ------------------------------------------------------------------
    // Test 4: include_knowledge=false skips Knowledge-type entries
    // ------------------------------------------------------------------

    [Fact]
    public async Task SkipsKnowledgeWhenNotIncluded()
    {
        var source = new FakeStore();
        var target = new FakeStore();
        var embedder = new RecordingFakeEmbedder();
        var vectors = new FakeVectorSearch();

        source.SeedList(Tier.Hot, ContentType.Memory, MakeEntry("m1"));
        source.SeedList(Tier.Hot, ContentType.Knowledge, MakeEntry("k1", ContentType.Knowledge));

        var handler = MakeHandler(source, target, embedder, vectors);
        var args = ParseArgs("""{"include_knowledge": false}""");

        var result = await handler.ExecuteAsync(args, CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        var root = doc.RootElement;

        // Only the Memory entry should be migrated.
        Assert.Equal(1, root.GetProperty("migrated").GetInt32());
        var call = Assert.Single(embedder.Calls);
        Assert.Equal("content of m1", call);
    }

    // ------------------------------------------------------------------
    // Test 5: Name and Description are correct
    // ------------------------------------------------------------------

    [Fact]
    public void Name_IsCorrect()
    {
        var handler = new MigrateToRemoteHandler();
        Assert.Equal("migrate_to_remote", handler.Name);
        Assert.False(string.IsNullOrEmpty(handler.Description));
        Assert.Equal(JsonValueKind.Object, handler.InputSchema.ValueKind);
    }
}
