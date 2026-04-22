namespace TotalRecall.Infrastructure.Skills;

/// <summary>
/// No-op <see cref="ISkillClient"/> used in SQLite mode where there is no
/// Cortex endpoint. Import and list calls return empty results so that the
/// local extra_dirs scan still populates the session skills block.
/// </summary>
internal sealed class NullSkillClient : ISkillClient
{
    public static readonly NullSkillClient Instance = new();

    public Task<SkillSearchHitDto[]> SearchAsync(
        string query, string? scope, IReadOnlyList<string>? tags, int limit, CancellationToken ct) =>
        Task.FromResult(Array.Empty<SkillSearchHitDto>());

    public Task<SkillBundleDto?> GetByIdAsync(Guid id, CancellationToken ct) =>
        Task.FromResult<SkillBundleDto?>(null);

    public Task<SkillBundleDto?> GetByNaturalKeyAsync(
        string name, string scope, string scopeId, CancellationToken ct) =>
        Task.FromResult<SkillBundleDto?>(null);

    public Task<SkillListResponseDto> ListAsync(
        string? scope, IReadOnlyList<string>? tags, int skip, int take, CancellationToken ct) =>
        Task.FromResult(new SkillListResponseDto(0, 0, 0, Array.Empty<SkillDto>()));

    public Task DeleteAsync(Guid id, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<SkillImportSummaryDto[]> ImportAsync(
        string adapter, IReadOnlyList<ImportedSkill> skills, CancellationToken ct) =>
        Task.FromResult(Array.Empty<SkillImportSummaryDto>());
}
