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
            "{}");

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
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(0, doc.RootElement.GetArrayLength());
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
        var arr = doc.RootElement;
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
        Assert.Equal(3, tiers.Count); // hot/warm/cold x knowledge
    }

    [Fact]
    public async Task DefaultFilters_IncludeAllSixTablePairs()
    {
        var (handler, _, hybrid) = NewFixture();

        await handler.ExecuteAsync(Args("""{"query":"q"}"""), CancellationToken.None);

        Assert.Equal(6, hybrid.Calls[0].Tiers.Count);
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
    public async Task Scopes_EmptyArray_PassedAsEmptyToHybridOpts()
    {
        var (handler, _, hybrid) = NewFixture();

        await handler.ExecuteAsync(
            Args("""{"query":"q","scopes":[]}"""),
            CancellationToken.None);

        // Empty array is parsed; handler passes the empty list through (not null).
        var scopes = hybrid.Calls[0].Opts.Scopes;
        Assert.NotNull(scopes);
        Assert.Empty(scopes!);
    }
}
