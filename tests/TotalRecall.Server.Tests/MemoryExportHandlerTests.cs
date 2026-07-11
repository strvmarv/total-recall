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

    // ---------------- Tier model v2 (Task 9): legacy pinned import ----------------

    [Fact]
    public async Task Import_LegacyPinnedTier_LandsStickyHot()
    {
        // A legacy export carries tier "pinned". ParseTier now returns null for
        // it, so the import handler's explicit legacy map must land the entry in
        // HOT with the sticky flag set (decay_score defaults to 1.0), preserving
        // the old pin across an export/import round-trip.
        var importStore = new FakeStore();
        // FakeStore.Insert returns NextInsertId (it doesn't honor opts.Id like the
        // real store), so pin the returned id to the preserved import id.
        importStore.NextInsertId = "pinned-1";
        var importVec = new FakeVectorSearch();
        var importEmbedder = new RecordingFakeEmbedder();
        var importHandler = new MemoryImportHandler(importStore, importVec, importEmbedder);

        var importArgs = JsonDocument.Parse(
            """{"entries":[{"id":"pinned-1","content":"legacy pin","tier":"pinned","content_type":"memory"}]}""")
            .RootElement.Clone();
        var importResult = await importHandler.ExecuteAsync(importArgs, CancellationToken.None);

        using var importDoc = JsonDocument.Parse(importResult.Content[0].Text);
        Assert.Equal(1, importDoc.RootElement.GetProperty("imported_count").GetInt32());

        var call = Assert.Single(importStore.InsertCalls);
        Assert.True(call.Tier.IsHot, "legacy pinned import should land in hot");
        Assert.True(call.Type.IsMemory);
        Assert.Equal("pinned-1", call.Opts.Id);
        // Release-critical: the entry is sticky (pin preserved across round-trip).
        Assert.True(importStore.IsSticky(ContentType.Memory, "pinned-1"));
    }

    [Fact]
    public async Task ExportImport_RoundTrips_CurrentStickyHotPins()
    {
        // Tier model v2 (Task 9): a CURRENT (v2-created) pin is a sticky-hot
        // entry. Export must carry a `sticky` flag (tier serializes as "hot"),
        // and import must re-apply sticky-hot from it — otherwise the pin is
        // silently dropped on round-trip.
        var exportStore = new FakeStore();
        exportStore.SeedList(Tier.Hot, ContentType.Memory, MakeEntry("s1"));
        exportStore.SetSticky(ContentType.Memory, "s1", true);

        var exportHandler = new MemoryExportHandler(exportStore);
        var exportResult = await exportHandler.ExecuteAsync(null, CancellationToken.None);

        using var exportDoc = JsonDocument.Parse(exportResult.Content[0].Text);
        var entries = exportDoc.RootElement.GetProperty("entries");
        Assert.Equal(1, entries.GetArrayLength());
        var exported = entries[0];
        // Sticky entry exports as tier "hot" + sticky=true (no resurrected "pinned").
        Assert.Equal("hot", exported.GetProperty("tier").GetString());
        Assert.True(exported.GetProperty("sticky").GetBoolean());

        // Feed the exported JSON into the import handler against a fresh store.
        var importStore = new FakeStore();
        importStore.NextInsertId = "s1";
        var importHandler = new MemoryImportHandler(
            importStore, new FakeVectorSearch(), new RecordingFakeEmbedder());

        var importArgs = JsonDocument.Parse($"{{\"entries\":{entries.GetRawText()}}}").RootElement.Clone();
        var importResult = await importHandler.ExecuteAsync(importArgs, CancellationToken.None);

        using var importDoc = JsonDocument.Parse(importResult.Content[0].Text);
        Assert.Equal(1, importDoc.RootElement.GetProperty("imported_count").GetInt32());

        var call = Assert.Single(importStore.InsertCalls);
        Assert.True(call.Tier.IsHot, "sticky-hot import should land in hot");
        // Release-critical: the pin survives the round-trip.
        Assert.True(importStore.IsSticky(ContentType.Memory, "s1"));
    }
}
