// Plan 6 Task 6.0a — MemoryImportHandler contract tests.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Core;
using TotalRecall.Server.Handlers;
using TotalRecall.Server.Tests.TestSupport;
using Xunit;

namespace TotalRecall.Server.Tests;

public class MemoryImportHandlerTests
{
    private static (MemoryImportHandler handler, FakeStore store,
            FakeVectorSearch vec, RecordingFakeEmbedder embedder) MakeHandler()
    {
        var store = new FakeStore();
        var vec = new FakeVectorSearch();
        var embedder = new RecordingFakeEmbedder();
        return (new MemoryImportHandler(store, vec, embedder), store, vec, embedder);
    }

    private static JsonElement ParseArgs(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task HappyPath_InsertsAndEmbeds()
    {
        var (handler, store, vec, embedder) = MakeHandler();
        var args = ParseArgs("""
            {"entries":[
                {"id":"e1","content":"first","tier":"hot","content_type":"memory"},
                {"id":"e2","content":"second","tier":"warm","content_type":"knowledge"}
            ]}
            """);

        var result = await handler.ExecuteAsync(args, CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(2, doc.RootElement.GetProperty("imported_count").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("skipped_count").GetInt32());

        Assert.Equal(2, store.InsertCalls.Count);
        Assert.Equal(2, vec.InsertCalls.Count);
        Assert.Equal(2, embedder.Calls.Count);
        Assert.Equal(Tier.Hot, store.InsertCalls[0].Tier);
        Assert.Equal(Tier.Warm, store.InsertCalls[1].Tier);
        Assert.Equal(ContentType.Knowledge, store.InsertCalls[1].Type);
    }

    [Fact]
    public async Task Dedup_ByContent_WithinBatch()
    {
        var (handler, _, _, _) = MakeHandler();
        var args = ParseArgs("""
            {"entries":[
                {"content":"same"},
                {"content":"same"}
            ]}
            """);
        var result = await handler.ExecuteAsync(args, CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(1, doc.RootElement.GetProperty("imported_count").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("skipped_count").GetInt32());
    }

    [Fact]
    public async Task SkipsMalformed_ReportsErrors()
    {
        var (handler, _, _, _) = MakeHandler();
        var args = ParseArgs("""
            {"entries":[
                {"nope":"wrong"},
                "not-an-object",
                {"content":""},
                {"content":"ok"}
            ]}
            """);
        var result = await handler.ExecuteAsync(args, CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(1, doc.RootElement.GetProperty("imported_count").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("skipped_count").GetInt32());
        Assert.True(doc.RootElement.GetProperty("errors").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task MissingEntries_Throws()
    {
        var (handler, _, _, _) = MakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(ParseArgs("""{}"""), CancellationToken.None));
    }

    [Fact]
    public async Task JsonRoundtrip_MatchesDto()
    {
        var (handler, _, _, _) = MakeHandler();
        var result = await handler.ExecuteAsync(
            ParseArgs("""{"entries":[{"content":"x"}]}"""), CancellationToken.None);
        var dto = JsonSerializer.Deserialize(result.Content[0].Text, JsonContext.Default.MemoryImportResultDto);
        Assert.NotNull(dto);
        Assert.Equal(1, dto!.ImportedCount);
        Assert.Equal(0, dto.SkippedCount);
    }

    [Fact]
    public void Name_And_Schema_MatchWireContract()
    {
        var (handler, _, _, _) = MakeHandler();
        Assert.Equal("memory_import", handler.Name);
    }
}
