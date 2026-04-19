// Plan 6 Task 6.0b — KbRefreshHandler contract tests.

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Ingestion;
using TotalRecall.Server.Handlers;
using TotalRecall.Server.Tests.TestSupport;
using Xunit;

namespace TotalRecall.Server.Tests;

public class KbRefreshHandlerTests : IDisposable
{
    private readonly string _tmpFile;
    private readonly string _tmpDir;

    public KbRefreshHandlerTests()
    {
        _tmpFile = Path.Combine(Path.GetTempPath(), $"kb-refresh-{Guid.NewGuid():N}.md");
        File.WriteAllText(_tmpFile, "# stub\n");
        _tmpDir = Path.Combine(Path.GetTempPath(), $"kb-refresh-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { if (File.Exists(_tmpFile)) File.Delete(_tmpFile); } catch { }
        try { if (Directory.Exists(_tmpDir)) Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    private static (KbRefreshHandler handler, FakeStore store, FakeVectorSearch vec, RecordingFakeFileIngester ingester) MakeHandler()
    {
        var store = new FakeStore();
        var vec = new FakeVectorSearch();
        var ingester = new RecordingFakeFileIngester();
        return (new KbRefreshHandler(store, vec, ingester), store, vec, ingester);
    }

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static Entry MakeEntry(
        string id,
        string? metadataJson = "{}",
        string? parentId = null,
        string? collectionId = null) =>
        new(
            id, "content-" + id,
            FSharpOption<string>.None, FSharpOption<string>.None,
            FSharpOption<SourceTool>.None, FSharpOption<string>.None,
            ListModule.OfSeq(Array.Empty<string>()),
            100L, 200L, 300L, 0, 0.5,
            parentId is null ? FSharpOption<string>.None : FSharpOption<string>.Some(parentId),
            collectionId is null ? FSharpOption<string>.None : FSharpOption<string>.Some(collectionId),
            "",
            EntryType.Preference,
            metadataJson ?? "{}");

    [Fact]
    public async Task HappyPath_File_DeletesAndReingests()
    {
        var (handler, store, vec, ingester) = MakeHandler();
        var meta = $$"""{"type":"collection","source_path":"{{_tmpFile.Replace("\\", "\\\\")}}"}""";
        var root = MakeEntry("coll-1", metadataJson: meta, collectionId: "coll-1");
        var child = MakeEntry("doc-1", parentId: "coll-1", collectionId: "coll-1");
        store.Seed(Tier.Cold, ContentType.Knowledge, root);
        store.SeedList(Tier.Cold, ContentType.Knowledge, root, child);

        ingester.NextFileResult = new IngestFileResult(
            DocumentId: "doc-new",
            ChunkCount: 7,
            ValidationPassed: true,
            Validation: new ValidationResult(true, Array.Empty<ProbeResult>()));

        var result = await handler.ExecuteAsync(
            Args("""{"collection":"coll-1"}"""),
            CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("coll-1", doc.RootElement.GetProperty("collection_id").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("files").GetInt32());
        Assert.Equal(7, doc.RootElement.GetProperty("chunks").GetInt32());
        Assert.True(doc.RootElement.GetProperty("refreshed").GetBoolean());

        // Child + root were both deleted from the store and from vec.
        Assert.Contains(store.DeleteCalls, c => c.Id == "doc-1");
        Assert.Contains(store.DeleteCalls, c => c.Id == "coll-1");
        // Both entries were vec-deleted. Don't pin specific rowids
        // because the synthetic rowid allocation order depends on
        // Seed + SeedList interaction.
        Assert.Equal(2, vec.DeleteCalls.Count);

        // Re-ingest used the file path, not the directory path.
        Assert.Single(ingester.FileCalls);
        Assert.Equal(_tmpFile, ingester.FileCalls[0].FilePath);
        Assert.Empty(ingester.DirCalls);
    }

    [Fact]
    public async Task HappyPath_Directory_DispatchesIngestDirectory()
    {
        var (handler, store, _, ingester) = MakeHandler();
        var meta = $$"""{"type":"collection","source_path":"{{_tmpDir.Replace("\\", "\\\\")}}"}""";
        var root = MakeEntry("coll-d", metadataJson: meta, collectionId: "coll-d");
        store.Seed(Tier.Cold, ContentType.Knowledge, root);
        store.SeedList(Tier.Cold, ContentType.Knowledge, root);

        ingester.NextDirResult = new IngestDirectoryResult(
            CollectionId: "coll-d",
            DocumentCount: 4,
            TotalChunks: 12,
            Errors: Array.Empty<string>(),
            ValidationPassed: true,
            ValidationFailures: Array.Empty<string>());

        var result = await handler.ExecuteAsync(
            Args("""{"collection":"coll-d"}"""),
            CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(4, doc.RootElement.GetProperty("files").GetInt32());
        Assert.Equal(12, doc.RootElement.GetProperty("chunks").GetInt32());

        Assert.Single(ingester.DirCalls);
        Assert.Equal(_tmpDir, ingester.DirCalls[0].DirPath);
        Assert.Empty(ingester.FileCalls);
    }

    [Fact]
    public async Task MissingCollection_Throws()
    {
        var (handler, _, _, _) = MakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(Args("""{"collection":"nope"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task MissingArgs_Throws()
    {
        var (handler, _, _, _) = MakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(Args("""{}"""), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task NoSourcePath_Throws()
    {
        var (handler, store, _, _) = MakeHandler();
        var root = MakeEntry("c", metadataJson: """{"type":"collection"}""", collectionId: "c");
        store.Seed(Tier.Cold, ContentType.Knowledge, root);
        store.SeedList(Tier.Cold, ContentType.Knowledge, root);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.ExecuteAsync(Args("""{"collection":"c"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task SourcePathDoesNotExist_Throws()
    {
        var (handler, store, _, _) = MakeHandler();
        var root = MakeEntry(
            "c",
            metadataJson: """{"type":"collection","source_path":"/definitely/does/not/exist/xyz"}""",
            collectionId: "c");
        store.Seed(Tier.Cold, ContentType.Knowledge, root);
        store.SeedList(Tier.Cold, ContentType.Knowledge, root);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.ExecuteAsync(Args("""{"collection":"c"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task JsonRoundtrip_MatchesDto()
    {
        var (handler, store, _, _) = MakeHandler();
        var meta = $$"""{"type":"collection","source_path":"{{_tmpFile.Replace("\\", "\\\\")}}"}""";
        var root = MakeEntry("c", metadataJson: meta, collectionId: "c");
        store.Seed(Tier.Cold, ContentType.Knowledge, root);
        store.SeedList(Tier.Cold, ContentType.Knowledge, root);

        var result = await handler.ExecuteAsync(
            Args("""{"collection":"c"}"""),
            CancellationToken.None);

        var dto = JsonSerializer.Deserialize(result.Content[0].Text, JsonContext.Default.KbRefreshResultDto);
        Assert.NotNull(dto);
        Assert.Equal("c", dto!.CollectionId);
        Assert.True(dto.Refreshed);
    }

    [Fact]
    public void Name_MatchesWireContract()
    {
        var (handler, _, _, _) = MakeHandler();
        Assert.Equal("kb_refresh", handler.Name);
    }
}
