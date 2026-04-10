// tests/TotalRecall.Cli.Tests/Commands/Memory/ImportCommandTests.cs
//
// Plan 5 Task 5.6 — coverage for the memory import CLI verb.

using System;
using System.IO;
using System.Threading.Tasks;
using TotalRecall.Cli.Commands.Memory;
using TotalRecall.Cli.Tests.TestSupport;
using TotalRecall.Core;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands.Memory;

[Collection("ConsoleCapture")]
public sealed class ImportCommandTests : IDisposable
{
    private readonly TextWriter _origOut;
    private readonly TextWriter _origErr;
    private readonly StringWriter _outWriter = new();
    private readonly StringWriter _errWriter = new();

    public ImportCommandTests()
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

    private static (ImportCommand Cmd, FakeStore Store, FakeVectorSearch Vec, RecordingEmbedder Emb, StringWriter Out) MakeCmd()
    {
        var store = new FakeStore();
        var vec = new FakeVectorSearch();
        var emb = new RecordingEmbedder();
        var sw = new StringWriter();
        var cmd = new ImportCommand(store, vec, emb, sw);
        return (cmd, store, vec, emb, sw);
    }

    private static string WriteTempFile(string content)
    {
        var p = Path.Combine(Path.GetTempPath(), $"tr-import-{Guid.NewGuid():N}.json");
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public async Task MissingPath_ReturnsExit2()
    {
        var (cmd, _, _, _, _) = MakeCmd();
        var code = await cmd.RunAsync(Array.Empty<string>());
        Assert.Equal(2, code);
        Assert.Contains("<path>", _errWriter.ToString());
    }

    [Fact]
    public async Task NonexistentFile_ReturnsExit1()
    {
        var (cmd, _, _, _, _) = MakeCmd();
        var code = await cmd.RunAsync(new[] { "/nonexistent/path/abc.json" });
        Assert.Equal(1, code);
        Assert.Contains("Failed to read file", _errWriter.ToString());
    }

    [Fact]
    public async Task InvalidJson_ReturnsExit1()
    {
        var (cmd, _, _, _, _) = MakeCmd();
        var p = WriteTempFile("{ not valid json");
        try
        {
            var code = await cmd.RunAsync(new[] { p });
            Assert.Equal(1, code);
            Assert.Contains("Invalid JSON", _errWriter.ToString());
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public async Task MissingEntriesArray_ReturnsExit1()
    {
        var (cmd, _, _, _, _) = MakeCmd();
        var p = WriteTempFile("{\"version\":1}");
        try
        {
            var code = await cmd.RunAsync(new[] { p });
            Assert.Equal(1, code);
            Assert.Contains("missing entries", _errWriter.ToString());
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public async Task HappyPath_ImportsThree()
    {
        var (cmd, store, vec, emb, sw) = MakeCmd();
        var body = "{\"version\":1,\"exported_at\":123,\"entries\":[" +
            "{\"id\":\"x1\",\"content\":\"alpha\",\"tier\":\"hot\",\"content_type\":\"memory\",\"tags\":[\"t1\"]}," +
            "{\"id\":\"x2\",\"content\":\"beta\",\"tier\":\"warm\",\"content_type\":\"memory\",\"tags\":[]}," +
            "{\"id\":\"x3\",\"content\":\"gamma\",\"tier\":\"cold\",\"content_type\":\"knowledge\",\"tags\":[]}" +
            "]}";
        var p = WriteTempFile(body);
        try
        {
            var code = await cmd.RunAsync(new[] { p });
            Assert.Equal(0, code);
            Assert.Equal(3, store.InsertCalls.Count);
            Assert.Equal(3, vec.Inserts.Count);
            Assert.Equal(3, emb.Calls.Count);
            Assert.Contains("imported 3, skipped 0", sw.ToString());
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public async Task DedupById_SkipsExisting()
    {
        var (cmd, store, _, _, sw) = MakeCmd();
        store.Seed(Tier.Hot, ContentType.Memory, EntryFactory.Make(id: "X", content: "preexisting"));
        var body = "{\"entries\":[{\"id\":\"X\",\"content\":\"brand new content\",\"tier\":\"hot\",\"content_type\":\"memory\"}]}";
        var p = WriteTempFile(body);
        try
        {
            var code = await cmd.RunAsync(new[] { p });
            Assert.Equal(0, code);
            // Only the seeded insert happened; no new insert calls.
            Assert.Empty(store.InsertCalls);
            Assert.Contains("imported 0, skipped 1", sw.ToString());
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public async Task DedupByContent_SkipsDuplicate()
    {
        var (cmd, store, _, _, sw) = MakeCmd();
        store.Seed(Tier.Hot, ContentType.Memory, EntryFactory.Make(id: "X", content: "hello"));
        var body = "{\"entries\":[{\"id\":\"other\",\"content\":\"hello\",\"tier\":\"hot\",\"content_type\":\"memory\"}]}";
        var p = WriteTempFile(body);
        try
        {
            var code = await cmd.RunAsync(new[] { p });
            Assert.Equal(0, code);
            Assert.Empty(store.InsertCalls);
            Assert.Contains("imported 0, skipped 1", sw.ToString());
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public async Task InvalidContent_Skipped()
    {
        var (cmd, store, _, _, sw) = MakeCmd();
        var body = "{\"entries\":[" +
            "{\"id\":\"a\",\"content\":123}," + // non-string content
            "{\"id\":\"b\",\"content\":\"\"}," + // empty content
            "{\"id\":\"c\",\"content\":\"ok\",\"tier\":\"hot\",\"content_type\":\"memory\"}" +
            "]}";
        var p = WriteTempFile(body);
        try
        {
            var code = await cmd.RunAsync(new[] { p });
            Assert.Equal(0, code);
            Assert.Single(store.InsertCalls);
            Assert.Contains("imported 1, skipped 2", sw.ToString());
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public async Task DryRun_DoesNotTouchStoreOrEmbedder()
    {
        var (cmd, store, vec, emb, sw) = MakeCmd();
        var body = "{\"entries\":[{\"id\":\"a\",\"content\":\"alpha\",\"tier\":\"hot\",\"content_type\":\"memory\"}]}";
        var p = WriteTempFile(body);
        try
        {
            var code = await cmd.RunAsync(new[] { p, "--dry-run" });
            Assert.Equal(0, code);
            Assert.Empty(store.InsertCalls);
            Assert.Empty(vec.Inserts);
            Assert.Empty(emb.Calls);
            Assert.Contains("DRY RUN", sw.ToString());
            Assert.Contains("would import 1", sw.ToString());
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public async Task InvalidTier_FallsBackToHot()
    {
        var (cmd, store, _, _, _) = MakeCmd();
        var body = "{\"entries\":[{\"id\":\"a\",\"content\":\"alpha\",\"tier\":\"bogus\",\"content_type\":\"memory\"}]}";
        var p = WriteTempFile(body);
        try
        {
            var code = await cmd.RunAsync(new[] { p });
            Assert.Equal(0, code);
            Assert.Single(store.InsertCalls);
            Assert.Equal(Tier.Hot, store.InsertCalls[0].Tier);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public async Task SourceTool_RoundTripsFromImportFile()
    {
        // Task 5.10 item 3: source_tool in the import envelope must be
        // parsed and threaded through to InsertEntryOpts.
        var (cmd, store, _, _, _) = MakeCmd();
        var body = "{\"entries\":[" +
            "{\"id\":\"a\",\"content\":\"alpha\",\"tier\":\"hot\",\"content_type\":\"memory\",\"source_tool\":\"claude-code\"}" +
            "]}";
        var p = WriteTempFile(body);
        try
        {
            var code = await cmd.RunAsync(new[] { p });
            Assert.Equal(0, code);
            Assert.Single(store.InsertCalls);
            var opts = store.InsertCalls[0].Opts;
            Assert.NotNull(opts.SourceTool);
            Assert.True(opts.SourceTool!.IsClaudeCode);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public async Task InvalidContentType_FallsBackToMemory()
    {
        var (cmd, store, _, _, _) = MakeCmd();
        var body = "{\"entries\":[{\"id\":\"a\",\"content\":\"alpha\",\"tier\":\"hot\",\"content_type\":\"bogus\"}]}";
        var p = WriteTempFile(body);
        try
        {
            var code = await cmd.RunAsync(new[] { p });
            Assert.Equal(0, code);
            Assert.Single(store.InsertCalls);
            Assert.Equal(ContentType.Memory, store.InsertCalls[0].Type);
        }
        finally { File.Delete(p); }
    }
}
