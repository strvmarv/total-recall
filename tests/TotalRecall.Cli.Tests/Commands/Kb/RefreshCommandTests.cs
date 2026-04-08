using System;
using System.IO;
using System.Threading.Tasks;
using TotalRecall.Cli.Commands.Kb;
using TotalRecall.Cli.Tests.TestSupport;
using TotalRecall.Core;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands.Kb;

[Collection("ConsoleCapture")]
public sealed class RefreshCommandTests : IDisposable
{
    private readonly TextWriter _origOut;
    private readonly TextWriter _origErr;
    private readonly StringWriter _outWriter = new();
    private readonly StringWriter _errWriter = new();

    public RefreshCommandTests()
    {
        _origOut = Console.Out;
        _origErr = Console.Error;
        Console.SetOut(_outWriter);
        Console.SetError(_errWriter);
    }

    public void Dispose()
    {
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
    }

    [Fact]
    public async Task MissingId_ReturnsExit2()
    {
        var cmd = new RefreshCommand(new FakeSqliteStore(), new FakeVectorSearch(), new FakeFileIngester(), new StringWriter());
        var code = await cmd.RunAsync(Array.Empty<string>());
        Assert.Equal(2, code);
    }

    [Fact]
    public async Task NotFound_ReturnsExit1()
    {
        var cmd = new RefreshCommand(new FakeSqliteStore(), new FakeVectorSearch(), new FakeFileIngester(), new StringWriter());
        var code = await cmd.RunAsync(new[] { "nope" });
        Assert.Equal(1, code);
        Assert.Contains("not found", _errWriter.ToString());
    }

    [Fact]
    public async Task MissingSourcePath_ReturnsExit1()
    {
        var store = new FakeSqliteStore();
        store.Seed(Tier.Cold, ContentType.Knowledge, EntryFactory.Make(
            id: "coll-a",
            metadataJson: "{\"type\":\"collection\",\"name\":\"Alpha\"}"));
        var cmd = new RefreshCommand(store, new FakeVectorSearch(), new FakeFileIngester(), new StringWriter());

        var code = await cmd.RunAsync(new[] { "coll-a" });

        Assert.Equal(1, code);
        Assert.Contains("no source_path", _errWriter.ToString());
    }

    [Fact]
    public async Task SourcePathMissing_ReturnsExit1()
    {
        var store = new FakeSqliteStore();
        var bogus = Path.Combine(Path.GetTempPath(), "totalrecall-refresh-missing-" + Guid.NewGuid().ToString("N"));
        store.Seed(Tier.Cold, ContentType.Knowledge, EntryFactory.Make(
            id: "coll-a",
            metadataJson: "{\"type\":\"collection\",\"name\":\"Alpha\",\"source_path\":\"" + bogus.Replace("\\", "\\\\") + "\"}"));
        var cmd = new RefreshCommand(store, new FakeVectorSearch(), new FakeFileIngester(), new StringWriter());

        var code = await cmd.RunAsync(new[] { "coll-a" });

        Assert.Equal(1, code);
        Assert.Contains("does not exist", _errWriter.ToString());
    }

    [Fact]
    public async Task HappyPath_FileSource()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "totalrecall-refresh-" + Guid.NewGuid().ToString("N") + ".md");
        File.WriteAllText(tempFile, "# test\n");
        try
        {
            var store = new FakeSqliteStore();
            var vec = new FakeVectorSearch();
            store.Seed(Tier.Cold, ContentType.Knowledge, EntryFactory.Make(
                id: "coll-a",
                metadataJson: "{\"type\":\"collection\",\"name\":\"Alpha\",\"source_path\":\"" + tempFile.Replace("\\", "\\\\") + "\"}"));
            store.Seed(Tier.Cold, ContentType.Knowledge, EntryFactory.Make(
                id: "doc-1", collectionId: "coll-a"));
            store.Seed(Tier.Cold, ContentType.Knowledge, EntryFactory.Make(
                id: "chunk-1", parentId: "doc-1", collectionId: "coll-a"));
            var ingester = new FakeFileIngester();
            var injected = new StringWriter();
            var cmd = new RefreshCommand(store, vec, ingester, injected);

            var code = await cmd.RunAsync(new[] { "coll-a" });

            Assert.Equal(0, code);
            // 2 children + 1 root deletes
            Assert.Equal(3, vec.Deletes.Count);
            Assert.Equal(3, store.DeleteCalls.Count);
            Assert.Single(ingester.FileCalls);
            Assert.Equal(tempFile, ingester.FileCalls[0].Path);
            Assert.Empty(ingester.DirCalls);
            Assert.Contains("refreshed coll-a", injected.ToString());
            Assert.Contains("1 files", injected.ToString());
            Assert.Contains("3 chunks", injected.ToString());
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task HappyPath_DirectorySource()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "totalrecall-refresh-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new FakeSqliteStore();
            var vec = new FakeVectorSearch();
            store.Seed(Tier.Cold, ContentType.Knowledge, EntryFactory.Make(
                id: "coll-a",
                metadataJson: "{\"type\":\"collection\",\"name\":\"Alpha\",\"source_path\":\"" + tempDir.Replace("\\", "\\\\") + "\"}"));
            var ingester = new FakeFileIngester
            {
                DirResult = new TotalRecall.Infrastructure.Ingestion.IngestDirectoryResult(
                    "new-coll", 4, 17, Array.Empty<string>(), true, Array.Empty<string>()),
            };
            var injected = new StringWriter();
            var cmd = new RefreshCommand(store, vec, ingester, injected);

            var code = await cmd.RunAsync(new[] { "coll-a" });

            Assert.Equal(0, code);
            Assert.Single(ingester.DirCalls);
            Assert.Equal(tempDir, ingester.DirCalls[0].Path);
            Assert.Empty(ingester.FileCalls);
            Assert.Contains("4 files", injected.ToString());
            Assert.Contains("17 chunks", injected.ToString());
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
