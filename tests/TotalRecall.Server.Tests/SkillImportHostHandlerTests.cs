// Plan 2 Task 13 — SkillImportHostHandler contract tests.

using System.Text.Json;
using TotalRecall.Infrastructure.Skills;
using TotalRecall.Server.Handlers;
using Xunit;

namespace TotalRecall.Server.Tests;

public class SkillImportHostHandlerTests
{
    private sealed class FakeImportService : ISkillImportService
    {
        public string? LastProjectPath;
        public int Calls;
        public SkillImportSummaryDto[] Result = Array.Empty<SkillImportSummaryDto>();

        public Task<SkillImportSummaryDto[]> ImportAsync(string? projectPath, CancellationToken ct)
        {
            LastProjectPath = projectPath;
            Calls++;
            return Task.FromResult(Result);
        }

        public Task<SkillListResponseDto> ListVisibleAsync(CancellationToken ct) =>
            Task.FromResult(new SkillListResponseDto(0, 0, 0, Array.Empty<SkillDto>()));

        public Task<ClaudeCodeScanResult> ScanExtraDirsAsync(CancellationToken ct) =>
            Task.FromResult(new ClaudeCodeScanResult(
                Array.Empty<ImportedSkill>(), Array.Empty<ScanError>()));
    }

    [Fact]
    public void Name_Is_skill_import_host()
    {
        var handler = new SkillImportHostHandler(new FakeImportService(), () => null);
        Assert.Equal("skill_import_host", handler.Name);
    }

    [Fact]
    public async Task HappyPath_PassesProjectPathAndSerializesSummaries()
    {
        var service = new FakeImportService
        {
            Result = new[]
            {
                new SkillImportSummaryDto(
                    "claude-code",
                    Scanned: 5,
                    Imported: 3,
                    Updated: 1,
                    Unchanged: 1,
                    Orphaned: 0,
                    Errors: Array.Empty<string>()),
            },
        };
        var handler = new SkillImportHostHandler(service, () => "C:/proj/alpha");

        var result = await handler.ExecuteAsync(null, CancellationToken.None);

        Assert.Equal(1, service.Calls);
        Assert.Equal("C:/proj/alpha", service.LastProjectPath);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(1, doc.RootElement.GetArrayLength());
        var row = doc.RootElement[0];
        Assert.Equal("claude-code", row.GetProperty("adapter").GetString());
        Assert.Equal(3, row.GetProperty("imported").GetInt32());
        Assert.Equal(5, row.GetProperty("scanned").GetInt32());
    }

    [Fact]
    public async Task NullProjectPath_IsForwarded()
    {
        var service = new FakeImportService();
        var handler = new SkillImportHostHandler(service, () => null);

        await handler.ExecuteAsync(null, CancellationToken.None);

        Assert.Null(service.LastProjectPath);
    }

    [Fact]
    public async Task EmptySummaries_SerializeAsArray()
    {
        var service = new FakeImportService { Result = Array.Empty<SkillImportSummaryDto>() };
        var handler = new SkillImportHostHandler(service, () => null);

        var result = await handler.ExecuteAsync(null, CancellationToken.None);

        Assert.Equal("[]", result.Content[0].Text);
    }
}
