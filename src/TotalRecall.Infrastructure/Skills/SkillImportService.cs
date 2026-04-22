using TotalRecall.Infrastructure.Sync;  // for CortexUnreachableException

namespace TotalRecall.Infrastructure.Skills;

public sealed class SkillImportService(
    IClaudeCodeSkillScanner scanner,
    ISkillClient client,
    ICustomDirsSkillScanner? customDirsScanner = null) : ISkillImportService
{
    public async Task<SkillImportSummaryDto[]> ImportAsync(
        string? projectPath, CancellationToken ct)
    {
        var claudeScan = await scanner.ScanAsync(projectPath, ct);

        var allSkills = new List<ImportedSkill>(claudeScan.Skills);
        var allErrors = new List<ScanError>(claudeScan.Errors);

        if (customDirsScanner is not null)
        {
            var customScan = await customDirsScanner.ScanAsync(ct);
            allSkills.AddRange(customScan.Skills);
            allErrors.AddRange(customScan.Errors);
        }

        try
        {
            await client.ImportAsync("claude-code", allSkills, ct);

            // The new endpoint returns 202 with no body — build an optimistic
            // summary locally. Scanner-side errors are merged in here.
            var errors = allErrors.Select(e => $"{e.SourcePath}: {e.Error}").ToArray();
            return
            [
                new SkillImportSummaryDto(
                    Adapter: "claude-code",
                    Scanned: allSkills.Count,
                    Imported: 0, Updated: 0, Unchanged: 0, Orphaned: 0,
                    Errors: errors)
            ];
        }
        catch (CortexUnreachableException ex)
        {
            return
            [
                new SkillImportSummaryDto(
                    Adapter: "claude-code",
                    Scanned: allSkills.Count,
                    Imported: 0, Updated: 0, Unchanged: 0, Orphaned: 0,
                    Errors: [$"cortex_unreachable: {ex.Message}"])
            ];
        }
    }

    /// <inheritdoc />
    public Task<SkillListResponseDto> ListVisibleAsync(CancellationToken ct) =>
        // skip: 0, take: int.MaxValue — single-page fetch to get all visible skills.
        client.ListAsync(scope: null, tags: null, skip: 0, take: int.MaxValue, ct);

    /// <inheritdoc />
    public Task<ClaudeCodeScanResult> ScanExtraDirsAsync(CancellationToken ct)
    {
        if (customDirsScanner is null)
            return Task.FromResult(new ClaudeCodeScanResult(
                Array.Empty<ImportedSkill>(), Array.Empty<ScanError>()));
        return customDirsScanner.ScanAsync(ct);
    }
}
