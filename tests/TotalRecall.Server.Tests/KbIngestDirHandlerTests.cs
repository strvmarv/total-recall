// Plan 4 Task 4.9 — KbIngestDirHandler contract tests.

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

public class KbIngestDirHandlerTests : IDisposable
{
    private readonly string _tmpDir;

    public KbIngestDirHandlerTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"kb-ingest-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tmpDir)) Directory.Delete(_tmpDir, true); } catch { }
    }

    private string PathJson => _tmpDir.Replace("\\", "\\\\");

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static (KbIngestDirHandler handler, RecordingFakeFileIngester fake) NewFixture()
    {
        var fake = new RecordingFakeFileIngester();
        return (new KbIngestDirHandler(fake), fake);
    }

    [Fact]
    public async Task HappyPath_CallsFileIngester_WithDirPath()
    {
        var (handler, fake) = NewFixture();

        var result = await handler.ExecuteAsync(
            Args($$"""{"path":"{{PathJson}}"}"""),
            CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        Assert.Single(fake.DirCalls);
        Assert.Equal(_tmpDir, fake.DirCalls[0].DirPath);
        Assert.Null(fake.DirCalls[0].Glob);
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
    public async Task GlobPassedThroughToFakeIngester()
    {
        var (handler, fake) = NewFixture();

        await handler.ExecuteAsync(
            Args($$"""{"path":"{{PathJson}}","glob":"*.md"}"""),
            CancellationToken.None);

        Assert.Equal("*.md", fake.DirCalls[0].Glob);
    }

    [Fact]
    public async Task DirectoryNotFound_Throws()
    {
        var (handler, fake) = NewFixture();
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(
                Args($$"""{"path":"{{missing.Replace("\\", "\\\\")}}"}"""),
                CancellationToken.None));
        Assert.Empty(fake.DirCalls);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var (handler, _) = NewFixture();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            handler.ExecuteAsync(
                Args($$"""{"path":"{{PathJson}}"}"""),
                cts.Token));
    }

    [Fact]
    public async Task JsonShape_IncludesCollectionIdAndCounts()
    {
        var (handler, fake) = NewFixture();
        fake.NextDirResult = new IngestDirectoryResult(
            CollectionId: "coll-abc",
            DocumentCount: 3,
            TotalChunks: 17,
            Errors: new[] { "foo.md: boom" },
            ValidationPassed: false,
            ValidationFailures: new[] { "bar.md" });

        var result = await handler.ExecuteAsync(
            Args($$"""{"path":"{{PathJson}}"}"""),
            CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        var root = doc.RootElement;
        Assert.Equal("coll-abc", root.GetProperty("collection_id").GetString());
        Assert.Equal(3, root.GetProperty("document_count").GetInt32());
        Assert.Equal(17, root.GetProperty("total_chunks").GetInt32());
        Assert.False(root.GetProperty("validation_passed").GetBoolean());
        Assert.Equal(1, root.GetProperty("errors").GetArrayLength());
        Assert.Equal("foo.md: boom", root.GetProperty("errors")[0].GetString());
        Assert.Equal("bar.md", root.GetProperty("validation_failures")[0].GetString());
    }

    [Fact]
    public async Task OptionalCollectionField_IgnoredButAccepted()
    {
        // TS schema has `collection` but ignores it; we match.
        var (handler, fake) = NewFixture();

        await handler.ExecuteAsync(
            Args($$"""{"path":"{{PathJson}}","collection":"coll-override"}"""),
            CancellationToken.None);

        Assert.Single(fake.DirCalls);
        // No assertion on a collection field since IngestDirectory doesn't take one.
    }

    [Fact]
    public async Task ExecuteAsync_WithoutScope_UsesConfiguredDefault()
    {
        var fake = new RecordingFakeFileIngester();
        var handler = new KbIngestDirHandler(fake, scopeDefault: "user:configured");

        await handler.ExecuteAsync(
            Args($$"""{"path":"{{PathJson}}"}"""),
            CancellationToken.None);

        Assert.Single(fake.DirCalls);
        Assert.Equal("user:configured", fake.DirCalls[0].Scope);
    }

    [Fact]
    public async Task ExecuteAsync_ExplicitScopeOverridesConfiguredDefault()
    {
        var fake = new RecordingFakeFileIngester();
        var handler = new KbIngestDirHandler(fake, scopeDefault: "user:configured");

        await handler.ExecuteAsync(
            Args($$"""{"path":"{{PathJson}}","scope":"team:docs"}"""),
            CancellationToken.None);

        Assert.Single(fake.DirCalls);
        Assert.Equal("team:docs", fake.DirCalls[0].Scope);
    }
}
