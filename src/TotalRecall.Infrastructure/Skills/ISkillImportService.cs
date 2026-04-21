namespace TotalRecall.Infrastructure.Skills;

/// <summary>
/// Orchestrates a local skills scan followed by a cortex import push. Scanner-side
/// errors are merged into the cortex-returned summary; if cortex is unreachable a
/// synthetic error row is produced so callers always receive an actionable summary.
/// </summary>
public interface ISkillImportService
{
    /// <param name="projectPath">Optional project root; null skips project-scope scan.</param>
    Task<SkillImportSummaryDto[]> ImportAsync(string? projectPath, CancellationToken ct);

    /// <summary>
    /// Returns all skills visible to the current user (scope: null = all visible).
    /// Fetches with <c>take: int.MaxValue</c> — a single unbounded page. If the
    /// Cortex server enforces its own page cap the response <c>Total</c> may exceed
    /// <c>Items.Count</c>; callers should surface all returned items and treat the
    /// result as best-effort.
    /// </summary>
    Task<SkillListResponseDto> ListVisibleAsync(CancellationToken ct);

    /// <summary>
    /// Scans extra_dirs from config and returns the discovered skills without
    /// pushing to cortex. Returns an empty result if no extra dirs are configured.
    /// </summary>
    Task<ClaudeCodeScanResult> ScanExtraDirsAsync(CancellationToken ct);
}
