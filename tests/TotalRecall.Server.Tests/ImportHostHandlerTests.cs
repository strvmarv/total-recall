// Plan 6 Task 6.0d — ImportHostHandler contract tests.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Importers;
using TotalRecall.Server.Handlers;
using TotalRecall.Server.Tests.TestSupport;
using Xunit;

namespace TotalRecall.Server.Tests;

public class ImportHostHandlerTests
{
    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static IReadOnlyList<IImporter> TwoFakes(bool claudeDetected = true)
    {
        return new List<IImporter>
        {
            new FakeImporter("claude-code", detect: claudeDetected)
            {
                MemoriesResult = new ImportResult(Imported: 3, Skipped: 1, Errors: Array.Empty<string>()),
                KnowledgeResult = new ImportResult(Imported: 2, Skipped: 0, Errors: Array.Empty<string>()),
            },
            new FakeImporter("copilot-cli", detect: false),
        };
    }

    [Fact]
    public async Task HappyPath_ReturnsPerSourceSummary()
    {
        var handler = new ImportHostHandler(() => TwoFakes());
        var result = await handler.ExecuteAsync(null, CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
        var results = doc.RootElement.GetProperty("results");
        Assert.Equal(2, results.GetArrayLength());

        var claude = results[0];
        Assert.Equal("claude-code", claude.GetProperty("source").GetString());
        Assert.True(claude.GetProperty("detected").GetBoolean());
        Assert.Equal(3, claude.GetProperty("memories_imported").GetInt32());
        Assert.Equal(2, claude.GetProperty("knowledge_imported").GetInt32());
        Assert.Equal(1, claude.GetProperty("skipped").GetInt32());

        var copilot = results[1];
        Assert.Equal("copilot-cli", copilot.GetProperty("source").GetString());
        Assert.False(copilot.GetProperty("detected").GetBoolean());
        Assert.Equal(0, copilot.GetProperty("memories_imported").GetInt32());
    }

    [Fact]
    public async Task SourceFilter_RestrictsToNamedImporter()
    {
        IReadOnlyList<IImporter> all = TwoFakes();
        var handler = new ImportHostHandler(() => all);
        var result = await handler.ExecuteAsync(
            Args("""{"source":"claude-code"}"""),
            CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("claude-code", doc.RootElement.GetProperty("results")[0].GetProperty("source").GetString());
    }

    [Fact]
    public async Task DryRun_SkipsImport()
    {
        var fakes = TwoFakes();
        var handler = new ImportHostHandler(() => fakes);
        var result = await handler.ExecuteAsync(
            Args("""{"dry_run":true}"""),
            CancellationToken.None);

        var claude = (FakeImporter)fakes[0];
        Assert.Equal(0, claude.ImportMemoriesCalls);
        Assert.Equal(0, claude.ImportKnowledgeCalls);
        Assert.Equal(1, claude.ScanCalls);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        var claudeResult = doc.RootElement.GetProperty("results")[0];
        Assert.Equal(0, claudeResult.GetProperty("memories_imported").GetInt32());
        Assert.True(claudeResult.GetProperty("detected").GetBoolean());
    }

    [Fact]
    public async Task InvalidDryRun_Throws()
    {
        var handler = new ImportHostHandler(() => TwoFakes());
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(Args("""{"dry_run":"yes"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task JsonRoundtrip_DtoShape()
    {
        var handler = new ImportHostHandler(() => TwoFakes());
        var result = await handler.ExecuteAsync(null, CancellationToken.None);
        var dto = JsonSerializer.Deserialize(result.Content[0].Text, JsonContext.Default.ImportHostResultDto);
        Assert.NotNull(dto);
        Assert.Equal(2, dto!.Count);
        Assert.Equal("claude-code", dto.Results[0].Source);
    }

    [Fact]
    public void Name_Is_import_host()
    {
        var handler = new ImportHostHandler(() => TwoFakes());
        Assert.Equal("import_host", handler.Name);
    }
}
