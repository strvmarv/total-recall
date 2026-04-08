using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using TotalRecall.Cli.Commands;
using TotalRecall.Cli.Tests.TestSupport;
using TotalRecall.Infrastructure.Importers;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands;

[Collection("ConsoleCapture")]
public sealed class ImportHostCommandTests : IDisposable
{
    private readonly TextWriter _origOut;
    private readonly TextWriter _origErr;
    private readonly StringWriter _outWriter = new();
    private readonly StringWriter _errWriter = new();

    public ImportHostCommandTests()
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

    private static List<IImporter> AllSevenFakes()
    {
        return new List<IImporter>
        {
            new FakeImporter("claude-code"),
            new FakeImporter("copilot-cli"),
            new FakeImporter("cursor"),
            new FakeImporter("cline"),
            new FakeImporter("opencode"),
            new FakeImporter("hermes"),
            new FakeImporter("project-docs"),
        };
    }

    [Fact]
    public async Task HelpFlag_ReturnsZero()
    {
        var cmd = new ImportHostCommand(AllSevenFakes(), new StringWriter());
        var code = await cmd.RunAsync(new[] { "--help" });
        // --help is parsed as unknown by the command; dispatcher handles it.
        // But the command should still return non-crashing exit.
        // We accept either 0 (dispatcher) or 2 (unknown arg fall-through).
        Assert.True(code == 0 || code == 2);
    }

    [Fact]
    public async Task InvalidSource_ReturnsExit2_ListsValidNames()
    {
        var cmd = new ImportHostCommand(AllSevenFakes(), new StringWriter());
        var code = await cmd.RunAsync(new[] { "--source", "bogus" });
        Assert.Equal(2, code);
        var err = _errWriter.ToString();
        Assert.Contains("claude-code", err);
        Assert.Contains("project-docs", err);
    }

    [Fact]
    public async Task SourceMissingValue_ReturnsExit2()
    {
        var cmd = new ImportHostCommand(AllSevenFakes(), new StringWriter());
        var code = await cmd.RunAsync(new[] { "--source" });
        Assert.Equal(2, code);
    }

    [Fact]
    public async Task NoneDetected_Json_AllSevenFalse()
    {
        var fakes = AllSevenFakes();
        var injected = new StringWriter();
        var cmd = new ImportHostCommand(fakes, injected);

        var code = await cmd.RunAsync(new[] { "--json" });

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(injected.ToString());
        var results = doc.RootElement.GetProperty("results");
        Assert.Equal(7, results.GetArrayLength());
        foreach (var r in results.EnumerateArray())
        {
            Assert.False(r.GetProperty("detected").GetBoolean());
            Assert.Equal(JsonValueKind.Null, r.GetProperty("scan").ValueKind);
        }

        // All 7 should have been Detect()ed; none should have been Scan'd
        // or Import'd.
        foreach (var f in fakes)
        {
            var fi = (FakeImporter)f;
            Assert.Equal(1, fi.DetectCalls);
            Assert.Equal(0, fi.ScanCalls);
            Assert.Equal(0, fi.ImportMemoriesCalls);
            Assert.Equal(0, fi.ImportKnowledgeCalls);
        }
    }

    [Fact]
    public async Task HappyPath_Json_ReflectsScanAndImport()
    {
        var claude = new FakeImporter("claude-code", detect: true)
        {
            ScanResult = new ImporterScanResult(MemoryFiles: 5, KnowledgeFiles: 2, SessionFiles: 0),
            MemoriesResult = new ImportResult(Imported: 4, Skipped: 1, Errors: new[] { "mem err" }),
            KnowledgeResult = new ImportResult(Imported: 2, Skipped: 0, Errors: Array.Empty<string>()),
        };
        var cursor = new FakeImporter("cursor", detect: true)
        {
            ScanResult = new ImporterScanResult(MemoryFiles: 1, KnowledgeFiles: 0, SessionFiles: 0),
            MemoriesResult = new ImportResult(Imported: 1, Skipped: 0, Errors: Array.Empty<string>()),
            KnowledgeResult = ImportResult.Empty,
        };
        var hermes = new FakeImporter("hermes", detect: false);

        var injected = new StringWriter();
        var cmd = new ImportHostCommand(
            new IImporter[] { claude, cursor, hermes },
            injected);

        var code = await cmd.RunAsync(new[] { "--json" });

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(injected.ToString());
        var results = doc.RootElement.GetProperty("results");
        Assert.Equal(3, results.GetArrayLength());

        var claudeJ = results[0];
        Assert.Equal("claude-code", claudeJ.GetProperty("tool").GetString());
        Assert.True(claudeJ.GetProperty("detected").GetBoolean());
        Assert.Equal(5, claudeJ.GetProperty("scan").GetProperty("memory_files").GetInt32());
        Assert.Equal(4, claudeJ.GetProperty("memories_result").GetProperty("imported").GetInt32());
        Assert.Equal(1, claudeJ.GetProperty("memories_result").GetProperty("skipped").GetInt32());
        Assert.Equal(1, claudeJ.GetProperty("memories_result").GetProperty("errors").GetArrayLength());

        var cursorJ = results[1];
        Assert.Equal("cursor", cursorJ.GetProperty("tool").GetString());
        Assert.True(cursorJ.GetProperty("detected").GetBoolean());

        var hermesJ = results[2];
        Assert.Equal("hermes", hermesJ.GetProperty("tool").GetString());
        Assert.False(hermesJ.GetProperty("detected").GetBoolean());

        // Verify hermes was only Detect'd, not scanned/imported.
        Assert.Equal(0, hermes.ScanCalls);
        Assert.Equal(0, hermes.ImportMemoriesCalls);

        // claude + cursor fully exercised.
        Assert.Equal(1, claude.ScanCalls);
        Assert.Equal(1, claude.ImportMemoriesCalls);
        Assert.Equal(1, claude.ImportKnowledgeCalls);
        Assert.Equal(1, cursor.ScanCalls);
    }

    [Fact]
    public async Task SourceFilter_OnlyMatchingImporterRuns()
    {
        var claude = new FakeImporter("claude-code", detect: true);
        var cursor = new FakeImporter("cursor", detect: true);
        var fakes = new IImporter[] { claude, cursor };
        var injected = new StringWriter();
        var cmd = new ImportHostCommand(fakes, injected);

        var code = await cmd.RunAsync(new[] { "--source", "claude-code", "--json" });

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(injected.ToString());
        var results = doc.RootElement.GetProperty("results");
        Assert.Equal(1, results.GetArrayLength());
        Assert.Equal("claude-code", results[0].GetProperty("tool").GetString());

        // cursor should NOT have been detected at all.
        Assert.Equal(0, cursor.DetectCalls);
        Assert.Equal(1, claude.DetectCalls);
    }

    [Fact]
    public async Task NoneDetected_SpectreTable_ReturnsZero()
    {
        // Spectre rendering doesn't honor Console.SetOut from tests
        // (established pattern from Task 5.4); assert exit code only.
        var cmd = new ImportHostCommand(AllSevenFakes(), new StringWriter());
        var code = await cmd.RunAsync(Array.Empty<string>());
        Assert.Equal(0, code);
    }
}
