// Plan 4 Task 4.7 — MemorySearchHandler contract tests.
//
// The happy-path test was originally authored as a Plan 1 [Fact(Skip=...)].
// Plan 4 implements the handler and removes the skip. The remaining tests
// cover argument parsing, filter pass-through, embedder interaction,
// cancellation, and the response JSON shape.

namespace TotalRecall.Server.Tests;

using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Sync;
using TotalRecall.Infrastructure.Telemetry;
using TotalRecall.Server.Handlers;
using TotalRecall.Server.Tests.TestSupport;
using Xunit;

public class MemorySearchHandlerTests
{
    // ---------------- helpers ----------------

    private static Entry MakeEntry(string id, string content = "c") =>
        new(
            id,
            content,
            FSharpOption<string>.None,
            FSharpOption<string>.None,
            FSharpOption<SourceTool>.None,
            FSharpOption<string>.None,
            ListModule.Empty<string>(),
            0L,
            0L,
            0L,
            0,
            1.0,
            FSharpOption<string>.None,
            FSharpOption<string>.None,
            "",
            EntryType.Preference,
            "{}", 0);

    private static SearchResult MakeResult(string id, Tier tier, ContentType ct, double score, int rank) =>
        new(MakeEntry(id), tier, ct, score, rank);

    private static (MemorySearchHandler handler, RecordingFakeEmbedder embed, RecordingFakeHybridSearch hybrid) NewFixture()
    {
        var embed = new RecordingFakeEmbedder();
        var hybrid = new RecordingFakeHybridSearch();
        var handler = new MemorySearchHandler(embed, hybrid);
        return (handler, embed, hybrid);
    }

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    // ---------------- happy path (flip of Plan 1 skipped test) ----------------

    [Fact]
    public async Task HappyPath_EmptyCorpus_ReturnsEmptyContentArray()
    {
        var (handler, _, _) = NewFixture();

        var result = await handler.ExecuteAsync(
            Args("""{"query":"hello","topK":10}"""),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotEqual(true, result.IsError);
        Assert.Single(result.Content);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        var results = doc.RootElement.GetProperty("results");
        Assert.Equal(JsonValueKind.Array, results.ValueKind);
        Assert.Equal(0, results.GetArrayLength());
    }

    [Fact]
    public async Task WithResults_ReturnsFormattedResults()
    {
        var (handler, _, hybrid) = NewFixture();
        hybrid.NextResult = new[]
        {
            MakeResult("id-a", Tier.Hot, ContentType.Memory, 0.91, 1),
            MakeResult("id-b", Tier.Warm, ContentType.Knowledge, 0.42, 2),
        };

        var result = await handler.ExecuteAsync(
            Args("""{"query":"q"}"""),
            CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        var arr = doc.RootElement.GetProperty("results");
        Assert.Equal(2, arr.GetArrayLength());

        var r0 = arr[0];
        Assert.Equal("id-a", r0.GetProperty("entry").GetProperty("id").GetString());
        Assert.Equal(0.91, r0.GetProperty("score").GetDouble(), 10);
        Assert.Equal("hot", r0.GetProperty("tier").GetString());
        Assert.Equal("memory", r0.GetProperty("content_type").GetString());
        Assert.Equal(1, r0.GetProperty("rank").GetInt32());

        var r1 = arr[1];
        Assert.Equal("id-b", r1.GetProperty("entry").GetProperty("id").GetString());
        Assert.Equal("warm", r1.GetProperty("tier").GetString());
        Assert.Equal("knowledge", r1.GetProperty("content_type").GetString());
        Assert.Equal(2, r1.GetProperty("rank").GetInt32());
    }

    [Fact]
    public async Task DefaultsTopKTo10()
    {
        var (handler, _, hybrid) = NewFixture();

        await handler.ExecuteAsync(Args("""{"query":"q"}"""), CancellationToken.None);

        Assert.Single(hybrid.Calls);
        Assert.Equal(10, hybrid.Calls[0].Opts.TopK);
    }

    [Fact]
    public async Task CustomTopK_PassedThrough()
    {
        var (handler, _, hybrid) = NewFixture();

        await handler.ExecuteAsync(Args("""{"query":"q","topK":25}"""), CancellationToken.None);

        Assert.Equal(25, hybrid.Calls[0].Opts.TopK);
    }

    [Fact]
    public async Task InvalidTopK_OutOfRange_Throws()
    {
        var (handler, _, _) = NewFixture();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(Args("""{"query":"q","topK":0}"""), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(Args("""{"query":"q","topK":1001}"""), CancellationToken.None));
    }

    [Fact]
    public async Task InvalidMinScore_OutOfRange_Throws()
    {
        var (handler, _, _) = NewFixture();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(Args("""{"query":"q","minScore":-0.1}"""), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(Args("""{"query":"q","minScore":1.1}"""), CancellationToken.None));
    }

    [Fact]
    public async Task MinScore_PassedThroughToHybridOpts()
    {
        var (handler, _, hybrid) = NewFixture();

        await handler.ExecuteAsync(
            Args("""{"query":"q","minScore":0.5}"""),
            CancellationToken.None);

        Assert.Equal(0.5, hybrid.Calls[0].Opts.MinScore);
    }

    [Fact]
    public async Task TiersFilter_PassedToHybridSearch()
    {
        var (handler, _, hybrid) = NewFixture();

        await handler.ExecuteAsync(
            Args("""{"query":"q","tiers":["warm","cold"]}"""),
            CancellationToken.None);

        var tiers = hybrid.Calls[0].Tiers;
        Assert.All(tiers, t =>
            Assert.True(t.Tier.IsWarm || t.Tier.IsCold, "expected only warm/cold"));
        // 2 tiers x 2 content types = 4 pairs
        Assert.Equal(4, tiers.Count);
    }

    [Fact]
    public async Task ContentTypesFilter_PassedToHybridSearch()
    {
        var (handler, _, hybrid) = NewFixture();

        await handler.ExecuteAsync(
            Args("""{"query":"q","contentTypes":["knowledge"]}"""),
            CancellationToken.None);

        var tiers = hybrid.Calls[0].Tiers;
        Assert.All(tiers, t => Assert.True(t.Type.IsKnowledge));
        Assert.Equal(4, tiers.Count); // hot/warm/cold/pinned x knowledge
    }

    [Fact]
    public async Task DefaultFilters_IncludeAllEightTablePairs()
    {
        var (handler, _, hybrid) = NewFixture();

        await handler.ExecuteAsync(Args("""{"query":"q"}"""), CancellationToken.None);

        Assert.Equal(8, hybrid.Calls[0].Tiers.Count);
    }

    [Fact]
    public async Task MissingQuery_Throws()
    {
        var (handler, _, _) = NewFixture();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(Args("""{"topK":10}"""), CancellationToken.None));
    }

    [Fact]
    public async Task EmptyQuery_Throws()
    {
        var (handler, _, _) = NewFixture();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(Args("""{"query":""}"""), CancellationToken.None));
    }

    [Fact]
    public async Task NullArguments_Throws()
    {
        var (handler, _, _) = NewFixture();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var (handler, _, _) = NewFixture();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            handler.ExecuteAsync(Args("""{"query":"q"}"""), cts.Token));
    }

    [Fact]
    public async Task EmbedderCalled_OnceWithQuery()
    {
        var (handler, embed, _) = NewFixture();

        await handler.ExecuteAsync(
            Args("""{"query":"hello world"}"""),
            CancellationToken.None);

        Assert.Single(embed.Calls);
        Assert.Equal("hello world", embed.Calls[0]);
    }

    [Fact]
    public async Task Embedder_UsesEmbedQuery_ForAsymmetricPrefix()
    {
        var (handler, fake, _) = NewFixture();

        await handler.ExecuteAsync(
            Args("""{"query":"hello world"}"""),
            CancellationToken.None);

        // Queries must flow through EmbedQuery so the ONNX embedder prepends
        // the bge asymmetric instruction prefix (default impl stays symmetric).
        Assert.True(fake.EmbedQueryCalled);
    }

    [Fact]
    public async Task InvalidTierString_Throws()
    {
        var (handler, _, _) = NewFixture();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(
                Args("""{"query":"q","tiers":["lukewarm"]}"""),
                CancellationToken.None));
    }

    [Fact]
    public async Task InvalidContentTypeString_Throws()
    {
        var (handler, _, _) = NewFixture();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(
                Args("""{"query":"q","contentTypes":["trivia"]}"""),
                CancellationToken.None));
    }

    [Fact]
    public async Task Scopes_SingleValue_PassedToHybridOpts()
    {
        var (handler, _, hybrid) = NewFixture();

        await handler.ExecuteAsync(
            Args("""{"query":"q","scopes":["user:paul"]}"""),
            CancellationToken.None);

        var scopes = hybrid.Calls[0].Opts.Scopes;
        Assert.NotNull(scopes);
        Assert.Single(scopes);
        Assert.Equal("user:paul", scopes[0]);
    }

    [Fact]
    public async Task Scopes_MultipleValues_PassedToHybridOpts()
    {
        var (handler, _, hybrid) = NewFixture();

        await handler.ExecuteAsync(
            Args("""{"query":"q","scopes":["user:paul","global:jira"]}"""),
            CancellationToken.None);

        var scopes = hybrid.Calls[0].Opts.Scopes;
        Assert.NotNull(scopes);
        Assert.Equal(2, scopes!.Count);
        Assert.Equal("user:paul", scopes[0]);
        Assert.Equal("global:jira", scopes[1]);
    }

    [Fact]
    public async Task Scopes_WhenOmitted_NullPassedToHybridOpts()
    {
        var (handler, _, hybrid) = NewFixture();

        await handler.ExecuteAsync(
            Args("""{"query":"q"}"""),
            CancellationToken.None);

        Assert.Null(hybrid.Calls[0].Opts.Scopes);
    }

    [Fact]
    public async Task Scopes_EmptyArray_NoDefault_PassesNullToHybridOpts()
    {
        var (handler, _, hybrid) = NewFixture();

        await handler.ExecuteAsync(
            Args("""{"query":"q","scopes":[]}"""),
            CancellationToken.None);

        // Empty array with no configured default resolves to null (no scope filter).
        Assert.Null(hybrid.Calls[0].Opts.Scopes);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutScopes_UsesConfiguredDefault()
    {
        var embed = new RecordingFakeEmbedder();
        var hybrid = new RecordingFakeHybridSearch();
        var handler = new MemorySearchHandler(embed, hybrid, scopeDefault: "user:configured");

        await handler.ExecuteAsync(Args("""{"query":"q"}"""), CancellationToken.None);

        var scopes = hybrid.Calls[0].Opts.Scopes;
        Assert.NotNull(scopes);
        Assert.Single(scopes!);
        Assert.Equal("user:configured", scopes![0]);
    }

    [Fact]
    public async Task ExecuteAsync_ExplicitScopesOverrideConfiguredDefault()
    {
        var embed = new RecordingFakeEmbedder();
        var hybrid = new RecordingFakeHybridSearch();
        var handler = new MemorySearchHandler(embed, hybrid, scopeDefault: "user:configured");

        await handler.ExecuteAsync(
            Args("""{"query":"q","scopes":["team:eng"]}"""),
            CancellationToken.None);

        var scopes = hybrid.Calls[0].Opts.Scopes;
        Assert.NotNull(scopes);
        Assert.Single(scopes!);
        Assert.Equal("team:eng", scopes![0]);
    }

    // ---------------- Phase 5: retrieval telemetry ----------------

    [Fact]
    public async Task Phase5_LogsRetrievalEventAndEnqueuesSyncPayload()
    {
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        var embed = new RecordingFakeEmbedder();
        var hybrid = new RecordingFakeHybridSearch
        {
            NextResult = new[]
            {
                MakeResult("id-a", Tier.Hot, ContentType.Memory, 0.91, 1),
                MakeResult("id-b", Tier.Warm, ContentType.Knowledge, 0.42, 2),
            },
        };
        var retrievalLog = new RetrievalEventLog(conn);
        var syncQueue = new SyncQueue(conn);

        var handler = new MemorySearchHandler(
            embed, hybrid,
            scopeDefault: null,
            retrievalLog: retrievalLog,
            syncQueue: syncQueue);

        await handler.ExecuteAsync(
            Args("""{"query":"hello phase5","topK":7}"""),
            CancellationToken.None);

        // --- assert local retrieval_events row ---
        var rows = retrievalLog.GetEvents(new RetrievalEventQuery());
        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal("hello phase5", row.QueryText);
        Assert.Equal("assistant", row.QuerySource);
        Assert.Equal(2, row.ResultCount);
        Assert.Equal("unknown", row.SessionId);
        Assert.Equal("default", row.ConfigSnapshotId);

        // tiers_searched is the 8 pair names (default: all eight table pairs).
        using (var tiersDoc = JsonDocument.Parse(row.TiersSearchedJson))
        {
            Assert.Equal(JsonValueKind.Array, tiersDoc.RootElement.ValueKind);
            Assert.Equal(8, tiersDoc.RootElement.GetArrayLength());
            var names = tiersDoc.RootElement.EnumerateArray()
                .Select(e => e.GetString()).ToArray();
            Assert.Contains("hot_memory", names);
            Assert.Contains("cold_knowledge", names);
            Assert.Contains("pinned_memory", names);
            Assert.Contains("pinned_knowledge", names);
        }

        // --- assert sync queue payload ---
        var items = syncQueue.Drain(limit: 10);
        Assert.Single(items);
        var item = items[0];
        Assert.Equal("retrieval", item.EntityType);
        Assert.Equal("push", item.Operation);

        using var doc = JsonDocument.Parse(item.Payload);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(1, doc.RootElement.GetArrayLength());
        var evt = doc.RootElement[0];
        Assert.Equal("hello phase5", evt.GetProperty("query").GetString());
        Assert.Equal(7, evt.GetProperty("top_k").GetInt32());
        Assert.Equal(2, evt.GetProperty("result_count").GetInt32());
        Assert.Equal(0.91, evt.GetProperty("top_score").GetDouble(), 6);
        Assert.Equal(8, evt.GetProperty("tiers_searched").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, evt.GetProperty("outcome_signal").ValueKind);
        Assert.True(evt.GetProperty("latency_ms").GetDouble() >= 0.0);
        Assert.False(string.IsNullOrEmpty(evt.GetProperty("timestamp").GetString()));
    }

    [Fact]
    public async Task MemorySearch_ReturnsEnvelopeWithRetrievalId_AndTagsSource()
    {
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        var embed = new RecordingFakeEmbedder();
        var hybrid = new RecordingFakeHybridSearch
        {
            NextResult = new[]
            {
                MakeResult("id-a", Tier.Hot, ContentType.Memory, 0.91, 1),
                MakeResult("id-b", Tier.Warm, ContentType.Knowledge, 0.42, 2),
            },
        };
        var log = new RetrievalEventLog(conn);

        var handler = new MemorySearchHandler(
            embed, hybrid,
            scopeDefault: null,
            retrievalLog: log,
            syncQueue: null,
            querySource: "assistant");

        var result = await handler.ExecuteAsync(
            Args("""{"query":"hello phase5"}"""), CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.True(root.TryGetProperty("retrievalId", out var rid));
        Assert.False(string.IsNullOrEmpty(rid.GetString()));
        Assert.Equal(2, root.GetProperty("results").GetArrayLength());

        var row = log.GetEvents(new RetrievalEventQuery()).Single();
        Assert.Equal("assistant", row.QuerySource);
    }

    [Fact]
    public async Task Phase5_WithoutSinks_DoesNotThrow()
    {
        // Sqlite-only composition path — both sinks null. Must not throw.
        var (handler, _, _) = NewFixture();

        var result = await handler.ExecuteAsync(
            Args("""{"query":"q"}"""),
            CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
    }

    [Fact]
    public async Task Phase5_LogOnly_EnqueueSkipped()
    {
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        var embed = new RecordingFakeEmbedder();
        var hybrid = new RecordingFakeHybridSearch();
        var retrievalLog = new RetrievalEventLog(conn);
        var syncQueue = new SyncQueue(conn);

        var handler = new MemorySearchHandler(
            embed, hybrid, scopeDefault: null,
            retrievalLog: retrievalLog, syncQueue: null);

        await handler.ExecuteAsync(Args("""{"query":"q"}"""), CancellationToken.None);

        Assert.Single(retrievalLog.GetEvents(new RetrievalEventQuery()));
        Assert.Equal(0, syncQueue.PendingCount());
    }

    // ---------------- Task 7: pinned tier filter tests ----------------

    [Fact]
    public async Task Search_TiersPinnedFilter_OnlySearchesPinnedTables()
    {
        var (handler, _, hybrid) = NewFixture();

        await handler.ExecuteAsync(
            Args("""{"query":"x","tiers":["pinned"]}"""),
            CancellationToken.None);

        var tiers = hybrid.Calls[0].Tiers;
        Assert.Equal(2, tiers.Count);
        Assert.All(tiers, t => Assert.True(t.Tier.IsPinned, "expected only pinned tier"));
        Assert.Contains(tiers, t => t.Type.IsMemory);
        Assert.Contains(tiers, t => t.Type.IsKnowledge);
    }

    [Fact]
    public async Task Search_DefaultTiers_IncludePinned()
    {
        var (handler, _, hybrid) = NewFixture();

        await handler.ExecuteAsync(
            Args("""{"query":"x"}"""),
            CancellationToken.None);

        var tiers = hybrid.Calls[0].Tiers;
        Assert.Equal(8, tiers.Count);
        Assert.Contains(tiers, t => t.Tier.IsPinned && t.Type.IsMemory);
        Assert.Contains(tiers, t => t.Tier.IsPinned && t.Type.IsKnowledge);
    }

    [Fact]
    public async Task Search_TiersPinnedString_Accepted_NotThrows()
    {
        // Regression guard: "pinned" was previously rejected as invalid tier.
        var (handler, _, _) = NewFixture();

        var ex = await Record.ExceptionAsync(() =>
            handler.ExecuteAsync(
                Args("""{"query":"x","tiers":["pinned"]}"""),
                CancellationToken.None));

        Assert.Null(ex);
    }
}
