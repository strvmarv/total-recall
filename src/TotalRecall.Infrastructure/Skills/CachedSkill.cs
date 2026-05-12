namespace TotalRecall.Infrastructure.Skills;

/// <summary>
/// Row shape returned by SqliteSkillCache readers. Includes content +
/// embedding so callers can render bundles and run hybrid search without
/// extra round-trips. Mirrors PluginSyncSkillDto plus local-only fields.
/// </summary>
public sealed record CachedSkill(
    Guid Id,
    string Name,
    string? Description,
    string Content,
    string? FrontmatterJson,
    string? ContentHash,
    string Scope,
    string ScopeId,
    string[] Tags,
    string? Source,
    int Version,
    bool IsOrphaned,
    float[]? ContentEmbedding,
    string? EmbedderFingerprint,
    DateTime UpdatedAt);
