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
    /// Delegates to the underlying skill client.
    /// </summary>
    Task<SkillListResponseDto> ListVisibleAsync(CancellationToken ct);
}
