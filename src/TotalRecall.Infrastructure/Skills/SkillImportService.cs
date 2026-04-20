using TotalRecall.Infrastructure.Sync;  // for CortexUnreachableException

namespace TotalRecall.Infrastructure.Skills;

public sealed class SkillImportService(
    IClaudeCodeSkillScanner scanner,
    ISkillClient client) : ISkillImportService
{
    public async Task<SkillImportSummaryDto[]> ImportAsync(
        string? projectPath, CancellationToken ct)
    {
        var scan = await scanner.ScanAsync(projectPath, ct);

        try
        {
            var summaries = await client.ImportAsync("claude-code", scan.Skills, ct);

            // Merge scanner-side errors into the first (and only) summary row.
            // Cortex returns one SkillImportSummary per adapter run; we only use
            // the "claude-code" adapter, so there will be at most one row.
            if (scan.Errors.Count > 0 && summaries.Length > 0)
            {
                var first = summaries[0];
                var mergedErrors = first.Errors
                    .Concat(scan.Errors.Select(e => $"{e.SourcePath}: {e.Error}"))
                    .ToArray();
                summaries[0] = first with { Errors = mergedErrors };
            }

            return summaries;
        }
        catch (CortexUnreachableException ex)
        {
            return new[]
            {
                new SkillImportSummaryDto(
                    Adapter: "claude-code",
                    Scanned: scan.Skills.Count,
                    Imported: 0, Updated: 0, Unchanged: 0, Orphaned: 0,
                    Errors: new[] { $"cortex_unreachable: {ex.Message}" })
            };
        }
    }
}
