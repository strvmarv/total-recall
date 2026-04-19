// Plan 4 Task 4.9 — KbSearchHandler contract tests. Mirrors
// MemorySearchHandlerTests structure but asserts kb_search's narrower
// semantics: cold/knowledge-only, optional post-hoc collection filter, TS
// `top_k` field (not `topK`), and the stubbed `hierarchicalMatch` /
// `needsSummary` Plan 4 behaviour.

namespace TotalRecall.Server.Tests;

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Server.Handlers;
using TotalRecall.Server.Tests.TestSupport;
using Xunit;

public class KbSearchHandlerTests
{
    private static Entry MakeEntry(
        string id,
        string? collectionId = null,
        string? parentId = null) =>
        new(
            id,
            "content",
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
            parentId is null ? FSharpOption<string>.None : FSharpOption<string>.Some(parentId),
            collectionId is null ? FSharpOption<string>.None : FSharpOption<string>.Some(collectionId),
            "",
            EntryType.Preference,
            "{}");

    private static SearchResult MakeResult(
        string id,
        double score = 0.8,
        int rank = 1,
        string? collectionId = null,
        string? parentId = null) =>
        new(MakeEntry(id, collectionId, parentId), Tier.Cold, ContentType.Knowledge, score, rank);

    private static (KbSearchHandler handler, RecordingFakeEmbedder embed, RecordingFakeHybridSearch hybrid) NewFixture()
    {
        var embed = new RecordingFakeEmbedder();
        var hybrid = new RecordingFakeHybridSearch();
        var handler = new KbSearchHandler(embed, hybrid);
        return (handler, embed, hybrid);
    }

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task HappyPath_ReturnsResultsFromColdKnowledge()
    {
        var (handler, _, hybrid) = NewFixture();
        hybrid.NextResult = new[] { MakeResult("id-1", 0.9, 1) };

        var result = await handler.ExecuteAsync(
            Args("""{"query":"hello"}"""),
            CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("results", out var results));
        Assert.Equal(1, results.GetArrayLength());
        Assert.Equal("id-1", results[0].GetProperty("entry").GetProperty("id").GetString());
        Assert.Equal("cold", results[0].GetProperty("tier").GetString());
        Assert.Equal("knowledge", results[0].GetProperty("content_type").GetString());

        // kb_search scopes to exactly one (tier, content_type) pair.
        Assert.Single(hybrid.Calls);
        Assert.Single(hybrid.Calls[0].Tiers);
        Assert.True(hybrid.Calls[0].Tiers[0].Tier.IsCold);
        Assert.True(hybrid.Calls[0].Tiers[0].Type.IsKnowledge);
    }

    [Fact]
    public async Task HappyPath_EmptyQuery_Throws()
    {
        var (handler, _, _) = NewFixture();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(Args("""{"query":""}"""), CancellationToken.None));
    }

    [Fact]
    public async Task CollectionFilter_OnlyMatchingResults()
    {
        var (handler, _, hybrid) = NewFixture();
        hybrid.NextResult = new[]
        {
            MakeResult("a", 0.9, 1, collectionId: "coll-1"),
            MakeResult("b", 0.8, 2, collectionId: "coll-2"),
            MakeResult("c", 0.7, 3, parentId: "coll-1"),
            MakeResult("d", 0.6, 4, collectionId: "coll-other"),
        };

        var result = await handler.ExecuteAsync(
            Args("""{"query":"q","collection":"coll-1"}"""),
            CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        var results = doc.RootElement.GetProperty("results");
        Assert.Equal(2, results.GetArrayLength());
        Assert.Equal("a", results[0].GetProperty("entry").GetProperty("id").GetString());
        Assert.Equal("c", results[1].GetProperty("entry").GetProperty("id").GetString());
    }

    [Fact]
    public async Task CollectionFilter_RequestsDoubleTopKFromHybrid()
    {
        var (handler, _, hybrid) = NewFixture();

        await handler.ExecuteAsync(
            Args("""{"query":"q","collection":"coll-1","top_k":5}"""),
            CancellationToken.None);

        // With a collection filter, we request topK*2 to leave headroom.
        Assert.Equal(10, hybrid.Calls[0].Opts.TopK);
    }

    [Fact]
    public async Task DefaultsTopKTo10()
    {
        var (handler, _, hybrid) = NewFixture();

        await handler.ExecuteAsync(Args("""{"query":"q"}"""), CancellationToken.None);

        Assert.Equal(10, hybrid.Calls[0].Opts.TopK);
    }

    [Fact]
    public async Task CustomTopK_PassedThrough_WhenNoCollection()
    {
        var (handler, _, hybrid) = NewFixture();

        await handler.ExecuteAsync(Args("""{"query":"q","top_k":25}"""), CancellationToken.None);

        Assert.Equal(25, hybrid.Calls[0].Opts.TopK);
    }

    [Fact]
    public async Task InvalidTopK_OutOfRange_Throws()
    {
        var (handler, _, _) = NewFixture();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(Args("""{"query":"q","top_k":0}"""), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(Args("""{"query":"q","top_k":1001}"""), CancellationToken.None));
    }

    [Fact]
    public async Task NeedsSummary_AlwaysFalse_InPlan4()
    {
        var (handler, _, hybrid) = NewFixture();
        hybrid.NextResult = new[] { MakeResult("a", 0.9, 1, collectionId: "coll-1") };

        var result = await handler.ExecuteAsync(
            Args("""{"query":"q","collection":"coll-1"}"""),
            CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.False(doc.RootElement.GetProperty("needsSummary").GetBoolean());
    }

    [Fact]
    public async Task HierarchicalMatch_AlwaysNull_InPlan4()
    {
        var (handler, _, _) = NewFixture();

        var result = await handler.ExecuteAsync(
            Args("""{"query":"q"}"""),
            CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("hierarchicalMatch").ValueKind);
    }

    [Fact]
    public async Task MissingQuery_Throws()
    {
        var (handler, _, _) = NewFixture();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(Args("""{"top_k":5}"""), CancellationToken.None));
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
        var handler = new KbSearchHandler(embed, hybrid, remote: null, scopeDefault: "user:configured");

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
        var handler = new KbSearchHandler(embed, hybrid, remote: null, scopeDefault: "user:configured");

        await handler.ExecuteAsync(
            Args("""{"query":"q","scopes":["team:eng"]}"""),
            CancellationToken.None);

        var scopes = hybrid.Calls[0].Opts.Scopes;
        Assert.NotNull(scopes);
        Assert.Single(scopes!);
        Assert.Equal("team:eng", scopes![0]);
    }
}
