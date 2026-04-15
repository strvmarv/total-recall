// Plan 4 Task 4.9 — KbIngestFileHandler contract tests.

namespace TotalRecall.Server.Tests;

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Ingestion;
using TotalRecall.Server.Handlers;
using TotalRecall.Server.Tests.TestSupport;
using Xunit;

public class KbIngestFileHandlerTests : IDisposable
{
    private readonly string _tmpFile;

    public KbIngestFileHandlerTests()
    {
        _tmpFile = Path.Combine(Path.GetTempPath(), $"kb-ingest-file-{Guid.NewGuid():N}.md");
        File.WriteAllText(_tmpFile, "# sample\n\nbody text");
    }

    public void Dispose()
    {
        try { if (File.Exists(_tmpFile)) File.Delete(_tmpFile); } catch { }
    }

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static (KbIngestFileHandler handler, RecordingFakeFileIngester fake) NewFixture()
    {
        var fake = new RecordingFakeFileIngester();
        return (new KbIngestFileHandler(fake), fake);
    }

    [Fact]
    public async Task HappyPath_CallsFileIngester_WithPath()
    {
        var (handler, fake) = NewFixture();

        var result = await handler.ExecuteAsync(
            Args($$"""{"path":"{{_tmpFile.Replace("\\", "\\\\")}}"}"""),
            CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        Assert.Single(fake.FileCalls);
        Assert.Equal(_tmpFile, fake.FileCalls[0].FilePath);
        Assert.Null(fake.FileCalls[0].CollectionId);
    }

    [Fact]
    public async Task PathNotProvided_Throws()
    {
        var (handler, _) = NewFixture();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(Args("""{}"""), CancellationToken.None));
    }

    [Fact]
    public async Task EmptyPath_Throws()
    {
        var (handler, _) = NewFixture();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(Args("""{"path":""}"""), CancellationToken.None));
    }

    [Fact]
    public async Task NullArguments_Throws()
    {
        var (handler, _) = NewFixture();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task OptionalCollection_PassedThrough()
    {
        var (handler, fake) = NewFixture();

        await handler.ExecuteAsync(
            Args($$"""{"path":"{{_tmpFile.Replace("\\", "\\\\")}}","collection":"coll-42"}"""),
            CancellationToken.None);

        Assert.Equal("coll-42", fake.FileCalls[0].CollectionId);
    }

    [Fact]
    public async Task FileNotFound_Throws()
    {
        var (handler, fake) = NewFixture();
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.md");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(
                Args($$"""{"path":"{{missing.Replace("\\", "\\\\")}}"}"""),
                CancellationToken.None));

        // Must short-circuit before hitting the ingester.
        Assert.Empty(fake.FileCalls);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var (handler, _) = NewFixture();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            handler.ExecuteAsync(
                Args($$"""{"path":"{{_tmpFile.Replace("\\", "\\\\")}}"}"""),
                cts.Token));
    }

    [Fact]
    public async Task JsonShape_IncludesDocumentIdAndChunkCount()
    {
        var (handler, fake) = NewFixture();
        fake.NextFileResult = new IngestFileResult(
            DocumentId: "doc-xyz",
            ChunkCount: 7,
            ValidationPassed: true,
            Validation: new ValidationResult(true, new[]
            {
                new ProbeResult(0, 0.87, true),
                new ProbeResult(2, 0.55, true),
            }));

        var result = await handler.ExecuteAsync(
            Args($$"""{"path":"{{_tmpFile.Replace("\\", "\\\\")}}"}"""),
            CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        var root = doc.RootElement;
        Assert.Equal("doc-xyz", root.GetProperty("document_id").GetString());
        Assert.Equal(7, root.GetProperty("chunk_count").GetInt32());
        Assert.True(root.GetProperty("validation_passed").GetBoolean());
        var probes = root.GetProperty("validation").GetProperty("probes");
        Assert.Equal(2, probes.GetArrayLength());
        Assert.Equal(0, probes[0].GetProperty("chunk_index").GetInt32());
        Assert.Equal(0.87, probes[0].GetProperty("score").GetDouble(), 10);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutScope_UsesConfiguredDefault()
    {
        var fake = new RecordingFakeFileIngester();
        var handler = new KbIngestFileHandler(fake, scopeDefault: "user:configured");

        await handler.ExecuteAsync(
            Args($$"""{"path":"{{_tmpFile.Replace("\\", "\\\\")}}"}"""),
            CancellationToken.None);

        Assert.Single(fake.FileCalls);
        Assert.Equal("user:configured", fake.FileCalls[0].Scope);
    }

    [Fact]
    public async Task ExecuteAsync_ExplicitScopeOverridesConfiguredDefault()
    {
        var fake = new RecordingFakeFileIngester();
        var handler = new KbIngestFileHandler(fake, scopeDefault: "user:configured");

        await handler.ExecuteAsync(
            Args($$"""{"path":"{{_tmpFile.Replace("\\", "\\\\")}}","scope":"team:docs"}"""),
            CancellationToken.None);

        Assert.Single(fake.FileCalls);
        Assert.Equal("team:docs", fake.FileCalls[0].Scope);
    }
}
