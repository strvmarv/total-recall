// Plan 6 Task 6.0a — MemoryExportHandler contract tests.

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

public class MemoryExportHandlerTests
{
    private static (MemoryExportHandler handler, FakeStore store) MakeHandler()
    {
        var store = new FakeStore();
        return (new MemoryExportHandler(store), store);
    }

    private static JsonElement ParseArgs(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static Entry MakeEntry(string id) =>
        new(
            id, "c-" + id,
            FSharpOption<string>.None, FSharpOption<string>.None,
            FSharpOption<SourceTool>.None, FSharpOption<string>.None,
            ListModule.OfSeq(Array.Empty<string>()),
            1L, 2L, 3L, 0, 0.5,
            FSharpOption<string>.None, FSharpOption<string>.None, "{}");

    [Fact]
    public async Task HappyPath_ExportsAllEntries()
    {
        var (handler, store) = MakeHandler();
        store.SeedList(Tier.Hot, ContentType.Memory, MakeEntry("a"), MakeEntry("b"));
        store.SeedList(Tier.Warm, ContentType.Knowledge, MakeEntry("c"));

        var result = await handler.ExecuteAsync(null, CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task TierFilter_String_Applied()
    {
        var (handler, store) = MakeHandler();
        store.SeedList(Tier.Hot, ContentType.Memory, MakeEntry("a"));
        store.SeedList(Tier.Warm, ContentType.Memory, MakeEntry("b"));
        store.SeedList(Tier.Cold, ContentType.Memory, MakeEntry("c"));

        var result = await handler.ExecuteAsync(
            ParseArgs("""{"tiers":"hot,cold"}"""), CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(2, doc.RootElement.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task TierFilter_Array_Applied()
    {
        var (handler, store) = MakeHandler();
        store.SeedList(Tier.Hot, ContentType.Memory, MakeEntry("a"));
        store.SeedList(Tier.Warm, ContentType.Memory, MakeEntry("b"));
        var result = await handler.ExecuteAsync(
            ParseArgs("""{"tiers":["warm"]}"""), CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(1, doc.RootElement.GetProperty("entries").GetArrayLength());
        Assert.Equal("b", doc.RootElement.GetProperty("entries")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task TypeFilter_Knowledge()
    {
        var (handler, store) = MakeHandler();
        store.SeedList(Tier.Hot, ContentType.Memory, MakeEntry("a"));
        store.SeedList(Tier.Hot, ContentType.Knowledge, MakeEntry("k"));
        var result = await handler.ExecuteAsync(
            ParseArgs("""{"types":["knowledge"]}"""), CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(1, doc.RootElement.GetProperty("entries").GetArrayLength());
        Assert.Equal("knowledge", doc.RootElement.GetProperty("entries")[0].GetProperty("content_type").GetString());
    }

    [Fact]
    public async Task InvalidTier_Throws()
    {
        var (handler, _) = MakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(ParseArgs("""{"tiers":"bogus"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task JsonRoundtrip_MatchesDto()
    {
        var (handler, store) = MakeHandler();
        store.SeedList(Tier.Hot, ContentType.Memory, MakeEntry("a"));
        var result = await handler.ExecuteAsync(null, CancellationToken.None);
        var dto = JsonSerializer.Deserialize(result.Content[0].Text, JsonContext.Default.MemoryExportResultDto);
        Assert.NotNull(dto);
        Assert.Single(dto!.Entries);
        Assert.Equal("a", dto.Entries[0].Id);
        Assert.Equal("hot", dto.Entries[0].Tier);
    }

    [Fact]
    public void Name_And_Schema_MatchWireContract()
    {
        var (handler, _) = MakeHandler();
        Assert.Equal("memory_export", handler.Name);
    }
}
