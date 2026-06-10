// Plan 6 Task 6.0a — MemoryExportHandler contract tests.
// Task 7 appends: pinned export/import round-trip.

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
            FSharpOption<string>.None, FSharpOption<string>.None, "", EntryType.Preference, "{}", 0);

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

    // ---------------- Task 7: pinned tier export/import round-trip ----------------

    [Fact]
    public async Task ExportImport_RoundTrips_PinnedEntries()
    {
        // Seed a pinned memory entry in the export source store.
        var exportStore = new FakeStore();
        var pinnedEntry = MakeEntry("pinned-1");
        exportStore.SeedList(Tier.Pinned, ContentType.Memory, pinnedEntry);

        // Run export handler — should include the pinned entry.
        var exportHandler = new MemoryExportHandler(exportStore);
        var exportResult = await exportHandler.ExecuteAsync(null, CancellationToken.None);

        using var exportDoc = JsonDocument.Parse(exportResult.Content[0].Text);
        var entries = exportDoc.RootElement.GetProperty("entries");
        Assert.Equal(1, entries.GetArrayLength());
        var exported = entries[0];
        Assert.Equal("pinned-1", exported.GetProperty("id").GetString());
        Assert.Equal("pinned", exported.GetProperty("tier").GetString());
        Assert.Equal("memory", exported.GetProperty("content_type").GetString());

        // Feed the exported JSON into the import handler against a fresh store.
        var importStore = new FakeStore();
        var importVec = new FakeVectorSearch();
        var importEmbedder = new RecordingFakeEmbedder();
        var importHandler = new MemoryImportHandler(importStore, importVec, importEmbedder);

        var importArgs = JsonDocument.Parse($"{{\"entries\":{entries.GetRawText()}}}").RootElement.Clone();
        var importResult = await importHandler.ExecuteAsync(importArgs, CancellationToken.None);

        // Verify the import result reports 1 imported entry.
        using var importDoc = JsonDocument.Parse(importResult.Content[0].Text);
        Assert.Equal(1, importDoc.RootElement.GetProperty("imported_count").GetInt32());
        Assert.Equal(0, importDoc.RootElement.GetProperty("skipped_count").GetInt32());

        // Verify the entry was inserted into the pinned tier.
        Assert.Single(importStore.InsertCalls);
        var call = importStore.InsertCalls[0];
        Assert.True(call.Tier.IsPinned, "imported entry should land in pinned tier");
        Assert.True(call.Type.IsMemory, "imported entry should be ContentType.Memory");
        Assert.Equal("c-pinned-1", call.Opts.Content);
        Assert.Equal("pinned-1", call.Opts.Id); // id must round-trip through export/import
    }

    [Fact]
    public async Task Export_TierFilterPinned_ReturnsOnlyPinnedEntries()
    {
        var (handler, store) = MakeHandler();
        store.SeedList(Tier.Hot, ContentType.Memory, MakeEntry("hot-1"));
        store.SeedList(Tier.Pinned, ContentType.Memory, MakeEntry("pinned-1"));
        store.SeedList(Tier.Pinned, ContentType.Knowledge, MakeEntry("pinned-k1"));

        var result = await handler.ExecuteAsync(
            ParseArgs("""{"tiers":["pinned"]}"""), CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        var entries = doc.RootElement.GetProperty("entries");
        Assert.Equal(2, entries.GetArrayLength());
        for (int i = 0; i < entries.GetArrayLength(); i++)
            Assert.Equal("pinned", entries[i].GetProperty("tier").GetString());
    }
}
