// Plan 6 Task 6.0b — KbSummarizeHandler contract tests.
// Covers the parity port of src-ts/tools/kb-tools.ts:298-309.

using System;
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

public class KbSummarizeHandlerTests
{
    private static (KbSummarizeHandler handler, FakeStore store) MakeHandler()
    {
        var store = new FakeStore();
        return (new KbSummarizeHandler(store), store);
    }

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static Entry MakeEntry(string id, string? summary = null) =>
        new(
            id, "content-" + id,
            summary is null ? FSharpOption<string>.None : FSharpOption<string>.Some(summary),
            FSharpOption<string>.None,
            FSharpOption<SourceTool>.None, FSharpOption<string>.None,
            ListModule.OfSeq(Array.Empty<string>()),
            100L, 200L, 300L, 0, 0.5,
            FSharpOption<string>.None, FSharpOption<string>.None, "", "{}");

    [Fact]
    public async Task HappyPath_NoPriorSummary_SetsIt()
    {
        var (handler, store) = MakeHandler();
        store.Seed(Tier.Cold, ContentType.Knowledge, MakeEntry("c1"));

        var result = await handler.ExecuteAsync(
            Args("""{"collection":"c1","summary":"the new summary"}"""),
            CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("c1", doc.RootElement.GetProperty("collection").GetString());
        Assert.True(doc.RootElement.GetProperty("summarized").GetBoolean());

        Assert.Single(store.UpdateCalls);
        var call = store.UpdateCalls[0];
        Assert.Equal(Tier.Cold, call.Tier);
        Assert.Equal(ContentType.Knowledge, call.Type);
        Assert.Equal("c1", call.Id);
        Assert.Equal("the new summary", call.Opts.Summary);
    }

    [Fact]
    public async Task HappyPath_OverwritesExistingSummary()
    {
        var (handler, store) = MakeHandler();
        store.Seed(Tier.Cold, ContentType.Knowledge, MakeEntry("c2", summary: "old summary"));

        var result = await handler.ExecuteAsync(
            Args("""{"collection":"c2","summary":"replacement"}"""),
            CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.True(doc.RootElement.GetProperty("summarized").GetBoolean());
        Assert.Equal("replacement", store.UpdateCalls[0].Opts.Summary);
    }

    [Fact]
    public async Task NotFound_ReturnsErrorEnvelope_TsCompatible()
    {
        var (handler, store) = MakeHandler();

        var result = await handler.ExecuteAsync(
            Args("""{"collection":"nope","summary":"x"}"""),
            CancellationToken.None);

        // No update should have happened.
        Assert.Empty(store.UpdateCalls);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(
            "Collection not found: nope",
            doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task MissingCollection_Throws()
    {
        var (handler, _) = MakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(Args("""{"summary":"x"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task MissingSummary_Throws()
    {
        var (handler, _) = MakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(Args("""{"collection":"c"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task NullArguments_Throws()
    {
        var (handler, _) = MakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task JsonRoundtrip_MatchesDto()
    {
        var (handler, store) = MakeHandler();
        store.Seed(Tier.Cold, ContentType.Knowledge, MakeEntry("c3"));
        var result = await handler.ExecuteAsync(
            Args("""{"collection":"c3","summary":"s"}"""),
            CancellationToken.None);
        var dto = JsonSerializer.Deserialize(result.Content[0].Text, JsonContext.Default.KbSummarizeResultDto);
        Assert.NotNull(dto);
        Assert.Equal("c3", dto!.Collection);
        Assert.True(dto.Summarized);
    }

    [Fact]
    public void Name_MatchesWireContract()
    {
        var (handler, _) = MakeHandler();
        Assert.Equal("kb_summarize", handler.Name);
    }
}
