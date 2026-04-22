using TotalRecall.Infrastructure.Sync;

namespace TotalRecall.Infrastructure.Skills;

/// <summary>
/// Client interface for the cortex <c>/api/me/skills/*</c> endpoints.
/// </summary>
public interface ISkillClient
{
    Task<SkillSearchHitDto[]> SearchAsync(
        string query, string? scope, IReadOnlyList<string>? tags, int limit, CancellationToken ct);

    Task<SkillBundleDto?> GetByIdAsync(Guid id, CancellationToken ct);

    Task<SkillBundleDto?> GetByNaturalKeyAsync(
        string name, string scope, string scopeId, CancellationToken ct);

    Task<SkillListResponseDto> ListAsync(
        string? scope, IReadOnlyList<string>? tags, int skip, int take, CancellationToken ct);

    Task DeleteAsync(Guid id, CancellationToken ct);

    Task ImportAsync(string adapter, IReadOnlyList<ImportedSkill> skills, CancellationToken ct);

    Task<PluginSyncSkillDto[]> GetModifiedSinceAsync(DateTime? since, CancellationToken ct);
}
